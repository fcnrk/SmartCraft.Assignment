using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Domain;
using SmartCraft.Assignment.Api.Infrastructure;

namespace SmartCraft.Assignment.Api.Application;

/// <summary>
/// Cross-aggregate orchestration for the Worklog lifecycle (docs/02-architecture.md
/// "Application orchestration"). Uses AppDbContext directly — no repository layer.
/// </summary>
public sealed class WorklogService(AppDbContext db)
{
    public async Task<Worklog> GetWorklogAsync(Guid worklogId, CancellationToken ct = default)
        => await db.Worklogs.FindAsync([worklogId], ct)
            ?? throw NotFound(worklogId);

    /// <summary>
    /// Rule 1 (worker must be assigned to the project) and rule 3 (worker's total hours
    /// for the date, across all statuses, must not exceed 24) are both checked here, inside
    /// one write transaction — see <see cref="EnsureDailyHoursWithinLimitAsync"/> for why
    /// that transaction has to acquire SQLite's write lock up front.
    /// </summary>
    public async Task<Worklog> CreateWorklogAsync(CreateWorklogCommand command, CancellationToken ct = default)
    {
        // Fail fast on malformed input (empty ids, hours out of range) before taking the
        // write lock below — no DB access, so no reason to serialize on it.
        var worklog = Worklog.Create(command.WorkerId, command.ProjectId, command.WorkDate, command.Hours, command.Description);

        return await WithBusyMappingAsync(async () =>
        {
            // BeginTransactionAsync itself is what takes SQLite's write lock (BEGIN IMMEDIATE —
            // see EnsureDailyHoursWithinLimitAsync), so it has to run inside the busy-mapping
            // guard too, not before it.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == command.ProjectId, ct)
                ?? throw new DomainException(DomainErrorKind.NotFound, $"Project {command.ProjectId} was not found.");

            if (!await db.Workers.AnyAsync(w => w.Id == command.WorkerId, ct))
            {
                throw new DomainException(DomainErrorKind.NotFound, $"Worker {command.WorkerId} was not found.");
            }

            if (!project.HasAssignment(command.WorkerId))
            {
                throw new DomainException(DomainErrorKind.Validation, "Worker is not assigned to this project.");
            }

            await EnsureDailyHoursWithinLimitAsync(command.WorkerId, command.WorkDate, command.Hours, excludingWorklogId: null, ct);

            db.Worklogs.Add(worklog);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return worklog;
        });
    }

    /// <summary>
    /// Rule 4/8 (Draft-only, enforced by Worklog.Update) plus rule 3 for the (possibly new)
    /// date. Rule 1 is not re-checked: WorkerId/ProjectId are immutable and there is no
    /// unassignment operation (AI_LOG AI-006).
    ///
    /// <see cref="Worklog.Update"/> runs before the daily-hours check so a lifecycle or
    /// hours-range error is reported as itself, not masked by the daily limit. The in-memory
    /// mutation this leaves behind on failure is discarded by
    /// <see cref="WithBusyMappingAsync{T}"/> (AI_LOG AI-009/AI-011).
    /// </summary>
    public async Task<Worklog> UpdateWorklogAsync(UpdateWorklogCommand command, CancellationToken ct = default)
    {
        return await WithBusyMappingAsync(async () =>
        {
            // See CreateWorklogAsync: BeginTransactionAsync belongs inside the busy-mapping
            // guard, not before it.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var worklog = await db.Worklogs.FirstOrDefaultAsync(w => w.Id == command.WorklogId, ct)
                ?? throw NotFound(command.WorklogId);

            CheckExpectedVersion(worklog, command.ExpectedVersion);

            worklog.Update(command.WorkDate, command.Hours, command.Description);

            await EnsureDailyHoursWithinLimitAsync(worklog.WorkerId, command.WorkDate, command.Hours, excludingWorklogId: worklog.Id, ct);

            await SaveWithConcurrencyCheckAsync(ct);
            await transaction.CommitAsync(ct);
            return worklog;
        });
    }

    /// <summary>Load -> expected-version check -> transition -> save. No explicit transaction:
    /// this doesn't touch the daily-hours invariant, so a single SaveChanges (implicitly
    /// atomic) plus the concurrency token is sufficient (docs/03-api-transactions.md).</summary>
    public async Task<Worklog> SubmitWorklogAsync(SubmitWorklogCommand command, CancellationToken ct = default)
        => await TransitionAsync(command.WorklogId, command.ExpectedVersion, w => w.Submit(), ct);

    public async Task<Worklog> ApproveWorklogAsync(ApproveWorklogCommand command, CancellationToken ct = default)
        => await TransitionAsync(command.WorklogId, command.ExpectedVersion, w => w.Approve(), ct);

    private async Task<Worklog> TransitionAsync(Guid worklogId, long expectedVersion, Action<Worklog> transition, CancellationToken ct)
    {
        return await WithBusyMappingAsync(async () =>
        {
            var worklog = await db.Worklogs.FirstOrDefaultAsync(w => w.Id == worklogId, ct)
                ?? throw NotFound(worklogId);

            CheckExpectedVersion(worklog, expectedVersion);

            transition(worklog);

            await SaveWithConcurrencyCheckAsync(ct);
            return worklog;
        });
    }

    private static void CheckExpectedVersion(Worklog worklog, long expectedVersion)
    {
        if (worklog.Version != expectedVersion)
        {
            throw new DomainException(
                DomainErrorKind.Conflict,
                $"Worklog {worklog.Id} has been modified since it was loaded (expected version {expectedVersion}, current {worklog.Version}). Reload and retry.");
        }
    }

    /// <summary>Catches the race between this request's own load-time version check above and
    /// another writer's commit in between: EF throws DbUpdateConcurrencyException when the
    /// UPDATE's `WHERE Version = @original` matches zero rows.</summary>
    private async Task SaveWithConcurrencyCheckAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(DomainErrorKind.Conflict, "Worklog was modified by another request between load and save. Reload and retry.");
        }
    }

    /// <summary>
    /// Rule 3: a worker's total hours for one date, across every status (Draft included —
    /// "total recorded hours"), must not exceed 24.
    ///
    /// ponytail: correct only because callers open the transaction first, and EF Core's
    /// default BeginTransaction on SQLite is <c>BEGIN IMMEDIATE</c> — the write lock is held
    /// before this SELECT runs. Waiting writers spin in Microsoft.Data.Sqlite's managed
    /// <c>Thread.Sleep(150)</c> retry loop (blocks the thread, ignores cancellation) until
    /// the command timeout. Both facts verified by decompiling 10.0.12 (docs/04 "daily-hours
    /// race"). Postgres/SQL Server at READ COMMITTED would allow write skew here; production
    /// fix is a per-(worker, date) row locked FOR UPDATE, or SERIALIZABLE + retry.
    ///
    /// Summed in C# decimal, not SQL SUM(): SQLite has no exact decimal arithmetic.
    /// </summary>
    private async Task EnsureDailyHoursWithinLimitAsync(Guid workerId, DateOnly workDate, decimal additionalHours, Guid? excludingWorklogId, CancellationToken ct)
    {
        var existingHours = await db.Worklogs
            .Where(w => w.WorkerId == workerId && w.WorkDate == workDate && w.Id != (excludingWorklogId ?? Guid.Empty))
            .Select(w => w.Hours)
            .ToListAsync(ct);

        var total = existingHours.Sum() + additionalHours;
        if (total > 24m)
        {
            throw new DomainException(
                DomainErrorKind.Validation,
                $"Worker's total hours on {workDate:yyyy-MM-dd} would be {total}, exceeding the 24-hour daily limit.");
        }
    }

    /// <summary>
    /// Guard every write path routes through. Two jobs:
    ///
    /// 1. SQLITE_BUSY/SQLITE_LOCKED — raw from BeginTransaction/Commit, or wrapped in
    /// DbUpdateException by SaveChanges — becomes a retryable Conflict.
    ///
    /// 2. On any failure, <c>ChangeTracker.Clear()</c>. EF's tracker is not transaction-scoped:
    /// a rolled-back Update leaves the entity dirty in memory, and a later SaveChanges on the
    /// same context (e.g. Submit) would persist the never-validated values. The Version token
    /// does not catch this — it detects other writers, not our own stale state (AI_LOG AI-011).
    /// </summary>
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

    private static DomainException NotFound(Guid worklogId) =>
        new(DomainErrorKind.NotFound, $"Worklog {worklogId} was not found.");
}
