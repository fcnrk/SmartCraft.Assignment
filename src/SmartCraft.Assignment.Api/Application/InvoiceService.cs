using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Domain;
using SmartCraft.Assignment.Api.Infrastructure;

namespace SmartCraft.Assignment.Api.Application;

/// <summary>
/// Cross-aggregate orchestration for invoice generation (docs/02-architecture.md "Application
/// orchestration", docs/03-api-transactions.md "Create invoice"). Uses AppDbContext directly,
/// same as WorklogService. Idempotency-key handling (docs/04-concurrency-idempotency-auth.md
/// "Idempotency") is explicitly out of scope for this iteration — see CreateInvoiceAsync's doc
/// comment.
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
    /// SaveInvoiceWithConcurrencyCheckAsync.
    /// </summary>
    public async Task<Invoice> CreateInvoiceAsync(Guid projectId, CancellationToken ct = default)
    {
        return await WithBusyMappingAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

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

            await SaveInvoiceWithConcurrencyCheckAsync(ct);
            await transaction.CommitAsync(ct);
            return invoice;
        });
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

    /// <summary>Catches two distinct races between this transaction's own reads above and
    /// another writer's commit in between: a worklog claimed/modified concurrently (Worklog's
    /// Version concurrency token — same mechanism as WorklogService), and a worklog already
    /// carrying an invoice line (InvoiceLine.WorklogId is its primary key — rule 9's DB-level
    /// guard, see AppDbContext). On SQLite under this method's BEGIN IMMEDIATE neither can
    /// actually occur (see CreateInvoiceAsync's doc comment); this exists for any DB/isolation
    /// level where that exclusivity does not hold.</summary>
    private async Task SaveInvoiceWithConcurrencyCheckAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(
                DomainErrorKind.Conflict,
                "One or more worklogs were modified or claimed by another request between load and save. Reload and retry.");
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new DomainException(
                DomainErrorKind.Conflict,
                "One or more worklogs have already been invoiced by another request. Reload and retry.");
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: 19 }; // SQLITE_CONSTRAINT

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
