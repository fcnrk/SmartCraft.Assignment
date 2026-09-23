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

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
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
        }
        catch (SqliteException ex) when (IsBusyOrLocked(ex))
        {
            throw BusyConflict(ex);
        }
    }

    /// <summary>
    /// Rule 4/8 (Draft-only, enforced by Worklog.Update itself) plus rule 3 for the (possibly
    /// new) date. Rule 1 is not re-checked here: WorkerId/ProjectId cannot change via Update,
    /// and there is no worker-unassignment operation yet, so a worklog that was validly
    /// assigned at Create time cannot become invalid later (see AI_LOG AI-006).
    /// </summary>
    public async Task<Worklog> UpdateWorklogAsync(UpdateWorklogCommand command, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var worklog = await db.Worklogs.FirstOrDefaultAsync(w => w.Id == command.WorklogId, ct)
                ?? throw NotFound(command.WorklogId);

            CheckExpectedVersion(worklog, command.ExpectedVersion);

            await EnsureDailyHoursWithinLimitAsync(worklog.WorkerId, command.WorkDate, command.Hours, excludingWorklogId: worklog.Id, ct);

            worklog.Update(command.WorkDate, command.Hours, command.Description);

            await SaveWithConcurrencyCheckAsync(ct);
            await transaction.CommitAsync(ct);
            return worklog;
        }
        catch (SqliteException ex) when (IsBusyOrLocked(ex))
        {
            throw BusyConflict(ex);
        }
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
        var worklog = await db.Worklogs.FirstOrDefaultAsync(w => w.Id == worklogId, ct)
            ?? throw NotFound(worklogId);

        CheckExpectedVersion(worklog, expectedVersion);

        transition(worklog);

        await SaveWithConcurrencyCheckAsync(ct);
        return worklog;
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
    /// Rule 3: a worker's total Worklog hours for one calendar date, across every status
    /// (Draft included — docs/01-domain.md says "total recorded hours", not "approved
    /// hours"), must not exceed 24.
    ///
    /// ponytail: correctness here relies on SQLite's single-writer lock. Both callers
    /// (CreateWorklogAsync/UpdateWorklogAsync) open their transaction with
    /// `Database.BeginTransactionAsync()` *before* reaching this method. Verified against
    /// Microsoft.Data.Sqlite 10.0.12 by decompiling SqliteConnection/SqliteTransaction: EF
    /// Core's parameterless BeginTransactionAsync passes IsolationLevel.Unspecified, which
    /// SqliteConnection.BeginTransaction(IsolationLevel) normalizes to Serializable while
    /// computing deferred = (isolationLevel == ReadUncommitted) = false; SqliteTransaction's
    /// constructor then executes literally "BEGIN IMMEDIATE;" (not "BEGIN;"). So opening the
    /// transaction already takes SQLite's write lock, before this method's SELECT runs — no
    /// isolation level/deferred flag needs to be passed explicitly, this is EF Core's default
    /// against Sqlite. A second writer that tries to open a conflicting BEGIN IMMEDIATE while
    /// this one is open blocks (does not fail immediately) for up to the connection's busy
    /// timeout (Microsoft.Data.Sqlite's "Default Timeout" keyword, 30s by default, maps to
    /// sqlite3_busy_timeout); once the timeout elapses SQLite raises SQLITE_BUSY, which
    /// surfaces as a SqliteException that CreateWorklogAsync/UpdateWorklogAsync map to an
    /// explicit (retryable) Conflict rather than a raw exception.
    ///
    /// Ceiling: this is SQLite-specific. On Postgres/SQL Server at READ COMMITTED, two
    /// concurrent transactions could each read the same pre-update total and both decide
    /// their own addition keeps it under 24, producing a lost update (docs/04, "daily-hours
    /// race"). Production fix: lock a per-(worker, date) row — e.g. an upsert into a
    /// WorkerDay(WorkerId, WorkDate, TotalHours) table via `SELECT ... FOR UPDATE` or a
    /// `CHECK(TotalHours <= 24)` maintained transactionally — or run this transaction at
    /// SERIALIZABLE and retry on serialization failure.
    ///
    /// SQLite SUM() is also not decimal-safe (no arbitrary-precision arithmetic), so this
    /// loads the worker/date's rows and sums them as C# decimal instead of summing in SQL;
    /// the row count per worker/day is small, so this is cheap.
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

    private static bool IsBusyOrLocked(SqliteException ex) => ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY / SQLITE_LOCKED

    private static DomainException BusyConflict(SqliteException ex) =>
        new(DomainErrorKind.Conflict, $"Database was busy and the write lock timed out ({ex.Message}). Retry the request.");

    private static DomainException NotFound(Guid worklogId) =>
        new(DomainErrorKind.NotFound, $"Worklog {worklogId} was not found.");
}
