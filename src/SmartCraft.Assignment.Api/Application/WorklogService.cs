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
    /// Rule 4/8 (Draft-only, enforced by Worklog.Update itself) plus rule 3 for the (possibly
    /// new) date. Rule 1 is not re-checked here: WorkerId/ProjectId cannot change via Update,
    /// and there is no worker-unassignment operation yet, so a worklog that was validly
    /// assigned at Create time cannot become invalid later (see AI_LOG AI-006).
    ///
    /// Ordering: <see cref="Worklog.Update"/> (rule 4/8 lifecycle check, rule 2 hours-range
    /// check) runs *before* <see cref="EnsureDailyHoursWithinLimitAsync"/> (rule 3), so a
    /// non-Draft worklog or an out-of-range hours value is rejected with its own Conflict/
    /// Validation error rather than being masked by an unrelated daily-limit Validation error.
    /// This is safe to do before the daily-hours check even though nothing has been saved yet:
    /// <c>Worklog.Update</c> only mutates the in-memory tracked entity — if the daily-hours
    /// check below throws, the transaction is rolled back (disposed without Commit) and nothing
    /// reaches the database. The daily-hours query itself is unaffected by the ordering either
    /// way, because it already excludes this worklog's own row by id and reads the DB, not the
    /// change tracker. A failed call does leave the tracked entity holding the new, unsaved
    /// values in memory (reviewer note M1/AI-009); this was originally left uncleared on the
    /// (incorrect) assumption that WorklogService is always used one-call-per-request. That
    /// assumption does not hold — nothing enforces it, WorklogService is not even registered in
    /// DI yet — and a second finding confirmed the resulting hole with a concrete repro (Update
    /// rejected by rule 3 leaves the entity dirty; a subsequent Submit on the same service
    /// instance then persists the never-validated hours). <see cref="WithBusyMappingAsync{T}"/>
    /// now calls <c>db.ChangeTracker.Clear()</c> on every failure from this method (and every
    /// other write path), which closes that hole regardless of how the service is reused.
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
    /// timeout. Corrected (was previously documented as sqlite3_busy_timeout — verified wrong
    /// by decompiling Microsoft.Data.Sqlite 10.0.12: there is no P/Invoke call to
    /// sqlite3_busy_timeout or a native busy_handler anywhere in the library): it is a managed
    /// retry loop in SqliteCommand/SqliteDataReader — on SQLITE_BUSY/SQLITE_LOCKED it calls
    /// `Thread.Sleep(150)` and retries until the command's CommandTimeout elapses.
    /// CommandTimeout defaults to the connection's DefaultTimeout ("Default Timeout"
    /// connection-string keyword, 30s by default). Because the wait is `Thread.Sleep`, it
    /// blocks the calling thread even through the async APIs and does not observe
    /// CancellationToken. Once the timeout elapses SQLite raises SQLITE_BUSY/SQLITE_LOCKED,
    /// which surfaces as a SqliteException (raw, or wrapped in DbUpdateException by
    /// SaveChanges) that <see cref="WithBusyMappingAsync{T}"/> maps to an explicit (retryable)
    /// Conflict rather than a raw exception.
    ///
    /// Ceiling: this is SQLite-specific, and even on SQLite it only serializes writers that
    /// share the same local database file on one host/process group — it says nothing about
    /// multiple hosts or a networked database. On Postgres/SQL Server at READ COMMITTED, two
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

    /// <summary>
    /// Shared guard that every write path (Create/Update/Submit/Approve) routes through,
    /// instead of a catch copy-pasted onto each one. Two independent jobs:
    ///
    /// 1. Busy/locked mapping: SQLITE_BUSY/SQLITE_LOCKED can surface either as a raw
    /// <see cref="SqliteException"/> (e.g. while <c>BeginTransactionAsync</c> is blocked
    /// acquiring BEGIN IMMEDIATE's write lock, or while <c>CommitAsync</c> is blocked
    /// acquiring the lock needed to flush at commit) or wrapped in a
    /// <see cref="DbUpdateException"/> (SaveChanges wraps provider exceptions). Both are
    /// mapped to the same retryable Conflict.
    ///
    /// 2. Change-tracker cleanup: on *any* failure (not just busy/locked — every exception,
    /// including domain Validation/Conflict/NotFound), <c>db.ChangeTracker.Clear()</c> is
    /// called before rethrowing. This is required, not just tidy: an in-progress mutation
    /// (e.g. <c>UpdateWorklogAsync</c> calling <c>worklog.Update(...)</c> before the
    /// daily-hours check) leaves the tracked entity holding the new, unsaved values even
    /// though the surrounding DB transaction was rolled back — EF's ChangeTracker is not
    /// transaction-scoped and a disposed-without-commit transaction does not revert it. If
    /// the same <see cref="WorklogService"/>/<see cref="AppDbContext"/> instance is then
    /// reused for a later, unrelated call against the same tracked entity (nothing in this
    /// project currently guarantees one <see cref="WorklogService"/> per request —
    /// it is not registered in DI), a query for that entity returns the still-dirty tracked
    /// instance (EF's identity resolution does not overwrite in-memory values with the
    /// freshly-queried ones), and an unrelated later SaveChanges (e.g. Submit) would persist
    /// the abandoned, never-validated change. Clearing the tracker on every failure closes
    /// that hole. The optimistic-concurrency <c>Version</c> token does NOT close it: it only
    /// detects concurrent *database* changes made by another writer, not a stale in-memory
    /// mutation reused within the same context/request.
    ///
    /// <see cref="DbUpdateConcurrencyException"/> is itself a <see cref="DbUpdateException"/>,
    /// so ordering matters: it is explicitly excluded from the DbUpdateException-busy catch
    /// below so it keeps propagating to <see cref="SaveWithConcurrencyCheckAsync"/>'s own
    /// catch, which maps it to its own (more specific) Conflict message before it reaches the
    /// final catch-all here (which still clears the tracker for it, like for every other
    /// exception). In practice a DbUpdateConcurrencyException's InnerException is never a
    /// busy/locked SqliteException anyway (it's raised by EF's own affected-row-count check,
    /// not a provider error), so the `when` filter alone would already exclude it — the
    /// explicit exclusion just makes that invariant visible rather than relying on it
    /// implicitly.
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
