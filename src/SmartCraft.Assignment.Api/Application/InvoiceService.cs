using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Domain;
using SmartCraft.Assignment.Api.Infrastructure;

namespace SmartCraft.Assignment.Api.Application;

/// <summary>Result of <see cref="InvoiceService.CreateInvoiceAsync"/>: the invoice, plus
/// whether this call actually created it (<c>false</c>) or replayed an earlier successful
/// call with the same Idempotency-Key (<c>true</c>) — the endpoint uses this to answer 201 vs
/// 200 (docs/03-api-transactions.md).</summary>
public sealed record InvoiceCreationResult(Invoice Invoice, bool IsReplay);

/// <summary>
/// Cross-aggregate orchestration for invoice generation (docs/02-architecture.md "Application
/// orchestration", docs/03-api-transactions.md "Create invoice"). Uses AppDbContext directly,
/// same as WorklogService.
/// </summary>
public sealed class InvoiceService(AppDbContext db, IInvoiceCalculator calculator, TimeProvider timeProvider)
{
    public async Task<Invoice> GetInvoiceAsync(Guid invoiceId, CancellationToken ct = default)
        => await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new DomainException(DomainErrorKind.NotFound, $"Invoice {invoiceId} was not found.");

    /// <summary>
    /// docs/03-api-transactions.md "Create invoice": load project + eligible worklogs,
    /// calculate, persist invoice + lines, mark worklogs invoiced — one transaction (rule 16).
    /// Same BEGIN IMMEDIATE pattern as WorklogService (see its
    /// EnsureDailyHoursWithinLimitAsync doc comment for the full decompiled-verified story):
    /// opening the transaction up front takes SQLite's single write lock before this method's
    /// SELECTs run, so two concurrent CreateInvoiceAsync calls (same or different projects)
    /// are fully serialized against this file — neither can read approved/uninvoiced worklogs
    /// or prior-invoiced normal hours the other has not yet committed, so neither can both
    /// claim the same worklog nor both under-count a shared worker/day's already-consumed
    /// normal capacity (docs/04 "competing invoice requests"; docs/01-domain.md "overtime
    /// allocation" cross-project consumption note).
    ///
    /// Ceiling: this is SQLite's single-writer model doing the work, not a portable technique
    /// (same ceiling WorklogService already documents for rule 3). On Postgres/SQL Server at
    /// READ COMMITTED, two concurrent invoice-creation transactions could each read the same
    /// pre-invoice "prior normal hours" total for a shared worker/day and both allocate that
    /// worker's first 8 hours as normal on that day — an under-counted-overtime race that a
    /// normal transaction does not prevent there. Production fix: the same kind of per-
    /// (worker,date) lock/serialization WorklogService's rule-3 note already asks for (e.g. a
    /// WorkerDay row locked FOR UPDATE, read as part of computing prior-normal-hours), or
    /// SERIALIZABLE + retry. What SQLite exclusivity is NOT the only thing protecting: the
    /// Worklog.Version concurrency token (a worklog claimed/modified between load and save)
    /// and InvoiceLine's WorklogId-as-primary-key (rule 9, double-invoicing) are DB constraints
    /// that hold on any database, independent of isolation level — see
    /// <see cref="TrySaveAsync"/>.
    ///
    /// Idempotency (docs/04 "Idempotency"): <paramref name="idempotencyKey"/> is required by
    /// the endpoint. The key is looked up inside the same BEGIN IMMEDIATE transaction, before
    /// any other read: same key + same <paramref name="projectId"/> replays the original
    /// invoice (no new effect); same key + a different project is a Conflict. A record is only
    /// written on a successful invoice creation, in the same SaveChanges/commit as the invoice
    /// and worklog claims — a failed attempt (e.g. no eligible worklogs) leaves no record, so a
    /// retry with the same key after a failure runs the request fresh rather than replaying a
    /// failure. The DB primary key on <c>IdempotencyRecord.Key</c> is defense-in-depth for a
    /// concurrent same-key request that reaches SaveChanges after this method's own lookup
    /// found nothing (see <see cref="TrySaveAsync"/>): the loser rolls back and replays the
    /// winner's now-committed record instead of erroring.
    /// </summary>
    public async Task<InvoiceCreationResult> CreateInvoiceAsync(Guid projectId, string idempotencyKey, CancellationToken ct = default)
    {
        return await WithBusyMappingAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var existingRecord = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.Key == idempotencyKey, ct);
            if (existingRecord is not null)
            {
                return await ResolveReplayAsync(existingRecord, projectId, ct);
            }

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
                ?? throw new DomainException(DomainErrorKind.NotFound, $"Project {projectId} was not found.");

            // Rule 11: only approved, uninvoiced worklogs for this project. Status == Approved
            // already implies InvoiceId == null (MarkInvoiced is the only path to Invoiced and
            // sets both together), but the explicit InvoiceId == null filter is kept as a
            // second, independent guard rather than relying solely on Status.
            var eligibleWorklogs = await db.Worklogs
                .Where(w => w.ProjectId == projectId && w.Status == WorklogStatus.Approved && w.InvoiceId == null)
                .ToListAsync(ct);

            if (eligibleWorklogs.Count == 0)
            {
                throw new DomainException(DomainErrorKind.Validation, $"Project {projectId} has no eligible worklogs to invoice.");
            }

            var billingInputs = new List<WorklogBillingInput>(eligibleWorklogs.Count);
            foreach (var worklog in eligibleWorklogs)
            {
                var assignment = project.Assignments.FirstOrDefault(a => a.WorkerId == worklog.WorkerId)
                    ?? throw new DomainException(
                        DomainErrorKind.Conflict,
                        $"Worker {worklog.WorkerId} has an approved worklog on project {projectId} but no current project assignment.");

                billingInputs.Add(new WorklogBillingInput(
                    worklog.Id, worklog.WorkerId, worklog.WorkDate, worklog.Hours, assignment.WorkerRole, assignment.HourlyRate));
            }

            var priorNormalHoursByWorkerDate = await LoadPriorNormalHoursByWorkerDateAsync(billingInputs, ct);

            var calculatedLines = calculator.Calculate(billingInputs, priorNormalHoursByWorkerDate);

            // Reviewer finding I1: the calculator is an external seam (a future legacy adapter
            // implements the same interface) — its output is not trusted blindly. A mismatch
            // here is a calculator bug, not a client error, so it throws InvalidOperationException
            // (-> 500) before anything is persisted, rather than silently short-invoicing or
            // corrupting a snapshot.
            ValidateCalculatedLines(billingInputs, calculatedLines);

            var invoiceLines = calculatedLines
                .Select(l => InvoiceLine.Create(
                    l.WorklogId, l.WorkerId, l.WorkDate, l.WorkerRole, l.HourlyRate,
                    l.NormalHours, l.OvertimeHours, l.OvertimeMultiplier, l.NormalAmount, l.OvertimeAmount, l.LineTotal))
                .ToList();

            var invoice = Invoice.Create(projectId, invoiceLines, timeProvider.GetUtcNow());
            db.Invoices.Add(invoice);

            var worklogsById = eligibleWorklogs.ToDictionary(w => w.Id);
            foreach (var line in calculatedLines)
            {
                worklogsById[line.WorklogId].MarkInvoiced(invoice.Id);
            }

            db.IdempotencyRecords.Add(IdempotencyRecord.Create(idempotencyKey, projectId, invoice.Id, timeProvider.GetUtcNow()));

            if (await TrySaveAsync(ct) == SaveOutcome.IdempotencyKeyRace)
            {
                // Another request with the same key committed first between our lookup above
                // and our own SaveChanges. Discard this attempt (nothing else was committed —
                // SQLite doesn't partially apply a failed statement's transaction) and replay
                // the winner's record instead of surfacing a spurious error.
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                var winningRecord = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.Key == idempotencyKey, ct)
                    ?? throw new DomainException(DomainErrorKind.Conflict, "Idempotency key race could not be resolved. Retry the request.");
                return await ResolveReplayAsync(winningRecord, projectId, ct);
            }

            await transaction.CommitAsync(ct);
            return new InvoiceCreationResult(invoice, IsReplay: false);
        });
    }

    /// <summary>Same key, same project -> replay the original invoice. Same key, different
    /// project -> Conflict: the key does not describe this request.</summary>
    private async Task<InvoiceCreationResult> ResolveReplayAsync(IdempotencyRecord record, Guid projectId, CancellationToken ct)
    {
        if (record.ProjectId != projectId)
        {
            throw new DomainException(
                DomainErrorKind.Conflict,
                $"Idempotency-Key '{record.Key}' was already used for a different project ({record.ProjectId}).");
        }

        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == record.InvoiceId, ct)
            ?? throw new DomainException(DomainErrorKind.Conflict, "Idempotency record exists but its invoice could not be found.");

        return new InvoiceCreationResult(invoice, IsReplay: true);
    }

    /// <summary>
    /// Reviewer finding I1: validates the calculator's output line-by-line against the
    /// billing inputs it was given, rather than trusting it. Checks only what
    /// <see cref="InvoiceLine.Create"/>/<see cref="Invoice.Create"/> do not already enforce
    /// (they validate ProjectId/line count, not per-line consistency).
    /// </summary>
    private static void ValidateCalculatedLines(
        IReadOnlyList<WorklogBillingInput> billingInputs, IReadOnlyList<CalculatedInvoiceLine> calculatedLines)
    {
        var inputsById = billingInputs.ToDictionary(i => i.WorklogId);

        if (calculatedLines.Count != inputsById.Count)
        {
            throw new InvalidOperationException(
                $"Invoice calculator returned {calculatedLines.Count} line(s) for {inputsById.Count} eligible worklog(s).");
        }

        var seenWorklogIds = new HashSet<Guid>();
        foreach (var line in calculatedLines)
        {
            if (!seenWorklogIds.Add(line.WorklogId))
            {
                throw new InvalidOperationException($"Invoice calculator returned a duplicate line for worklog {line.WorklogId}.");
            }

            if (!inputsById.TryGetValue(line.WorklogId, out var input))
            {
                throw new InvalidOperationException($"Invoice calculator returned a line for unknown worklog {line.WorklogId}.");
            }

            if (line.WorkerId != input.WorkerId || line.WorkDate != input.WorkDate
                || line.HourlyRate != input.HourlyRate || line.WorkerRole != input.WorkerRole)
            {
                throw new InvalidOperationException($"Invoice calculator line for worklog {line.WorklogId} does not match its billing input.");
            }

            if (line.NormalHours < 0 || line.OvertimeHours < 0 || line.NormalAmount < 0 || line.OvertimeAmount < 0)
            {
                throw new InvalidOperationException($"Invoice calculator line for worklog {line.WorklogId} produced a negative value.");
            }

            if (line.NormalHours + line.OvertimeHours != input.Hours)
            {
                throw new InvalidOperationException(
                    $"Invoice calculator line for worklog {line.WorklogId} allocates {line.NormalHours + line.OvertimeHours} hours, expected {input.Hours}.");
            }

            if (line.LineTotal != line.NormalAmount + line.OvertimeAmount)
            {
                throw new InvalidOperationException($"Invoice calculator line for worklog {line.WorklogId} has a LineTotal inconsistent with its amounts.");
            }
        }
    }

    /// <summary>
    /// Rule 12/"overtime allocation": prior normal hours already snapshotted on any existing
    /// invoice line (any project) for a worker/date that also appears in this invoice's
    /// candidate set. Loaded as raw rows and summed in C#, not SQL SUM() (see AppDbContext's
    /// decimal-mapping comment: SQLite has no arbitrary-precision decimal arithmetic).
    /// </summary>
    private async Task<Dictionary<(Guid WorkerId, DateOnly WorkDate), decimal>> LoadPriorNormalHoursByWorkerDateAsync(
        List<WorklogBillingInput> billingInputs, CancellationToken ct)
    {
        var workerIds = billingInputs.Select(i => i.WorkerId).Distinct().ToList();

        var priorLines = await db.Invoices
            .SelectMany(i => i.Lines)
            .Where(l => workerIds.Contains(l.WorkerId))
            .Select(l => new { l.WorkerId, l.WorkDate, l.NormalHours })
            .ToListAsync(ct);

        return priorLines
            .GroupBy(l => (l.WorkerId, l.WorkDate))
            .ToDictionary(g => g.Key, g => g.Sum(l => l.NormalHours));
    }

    private enum SaveOutcome
    {
        Success,

        /// <summary>A concurrent request with the same Idempotency-Key committed its
        /// IdempotencyRecord first; see CreateInvoiceAsync's replay-and-discard handling.</summary>
        IdempotencyKeyRace,
    }

    /// <summary>Catches three distinct races between this transaction's own reads above and
    /// another writer's commit in between: a worklog claimed/modified concurrently (Worklog's
    /// Version concurrency token — same mechanism as WorklogService), a worklog already
    /// carrying an invoice line (InvoiceLine.WorklogId is its primary key — rule 9's DB-level
    /// guard, see AppDbContext), and a concurrent request that already claimed the same
    /// Idempotency-Key (IdempotencyRecord.Key primary key). On SQLite under this method's
    /// BEGIN IMMEDIATE none of these can actually occur (see CreateInvoiceAsync's doc comment);
    /// this exists for any DB/isolation level where that exclusivity does not hold. The
    /// idempotency-key race is distinguished from the other two (which the caller cannot
    /// recover from) because it is recoverable: <c>DbUpdateException.Entries</c> identifies
    /// which tracked entity failed to insert.</summary>
    private async Task<SaveOutcome> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return SaveOutcome.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(
                DomainErrorKind.Conflict,
                "One or more worklogs were modified or claimed by another request between load and save. Reload and retry.");
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            if (ex.Entries.Any(e => e.Entity is IdempotencyRecord))
            {
                return SaveOutcome.IdempotencyKeyRace;
            }

            throw new DomainException(
                DomainErrorKind.Conflict,
                "One or more worklogs have already been invoiced by another request. Reload and retry.");
        }
    }

    /// <summary>Reviewer finding M1: narrowed from the broad SQLITE_CONSTRAINT (19) primary
    /// error code to the specific extended codes for a primary-key/unique violation, so this
    /// does not also swallow e.g. a FK or NOT NULL constraint failure as a "Conflict".</summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 }; // SQLITE_CONSTRAINT_PRIMARYKEY / SQLITE_CONSTRAINT_UNIQUE

    /// <summary>Same SQLite busy/locked mapping + change-tracker cleanup as
    /// WorklogService.WithBusyMappingAsync — see that method's doc comment for the full
    /// rationale (not duplicated here). Kept as a separate copy rather than a shared helper:
    /// the two services don't otherwise share a base type/module, and the guard is small
    /// enough that extracting one now would be speculative given neither is registered in DI
    /// or has a caller yet.</summary>
    private async Task<T> WithBusyMappingAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (SqliteException ex) when (IsBusyOrLocked(ex))
        {
            db.ChangeTracker.Clear();
            throw BusyConflict(ex);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException
            && ex.InnerException is SqliteException inner && IsBusyOrLocked(inner))
        {
            db.ChangeTracker.Clear();
            throw BusyConflict(inner);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static bool IsBusyOrLocked(SqliteException ex) => ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY / SQLITE_LOCKED

    private static DomainException BusyConflict(SqliteException ex) =>
        new(DomainErrorKind.Conflict, $"Database was busy and the write lock timed out ({ex.Message}). Retry the request.");
}
