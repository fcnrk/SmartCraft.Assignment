using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// docs/04-concurrency-idempotency-auth.md POC decision: a writer that cannot acquire
/// SQLite's single database-wide write lock blocks for up to the connection's busy timeout
/// (Microsoft.Data.Sqlite's managed Thread.Sleep(150) retry loop governed by CommandTimeout/
/// the "Default Timeout" connection-string keyword — not sqlite3_busy_timeout, see docs/04),
/// then gets SQLITE_BUSY/SQLITE_LOCKED, which WorklogService maps to a retryable
/// DomainErrorKind.Conflict instead of letting a raw SqliteException escape. Reproduced with
/// a 1-second busy timeout so the test stays fast: hold a write transaction open on one
/// connection, then attempt a create on a second connection and confirm it blocks for close
/// to that long before surfacing as Conflict.
/// </summary>
public class WorklogServiceBusyTimeoutTests
{
    [Fact]
    public async Task A_write_blocked_by_another_open_transaction_surfaces_as_a_retryable_Conflict_after_the_busy_timeout()
    {
        using var fx = new SqliteWorklogFixture(busyTimeoutSeconds: 1);

        using var blockerDb = fx.CreateContext();
        await using var blockerTx = await blockerDb.Database.BeginTransactionAsync();
        blockerDb.Workers.Add(Worker.Create("lock-holder"));
        await blockerDb.SaveChangesAsync(); // takes SQLite's write lock (BEGIN IMMEDIATE); left uncommitted below

        var service = fx.NewService();
        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, new DateOnly(2026, 9, 23), 2m, "blocked")));
        stopwatch.Stop();

        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900),
            $"expected the second writer to block for close to the 1s busy timeout before failing, actually took {stopwatch.Elapsed}");
    }

    /// <summary>
    /// Same scenario as above, plus the two assertions the original test didn't make: the
    /// blocked call must not have persisted anything, and a subsequent (unblocked) retry must
    /// succeed normally.
    /// </summary>
    [Fact]
    public async Task Create_blocked_by_another_open_transaction_persists_nothing_then_a_retry_succeeds_once_unblocked()
    {
        using var fx = new SqliteWorklogFixture(busyTimeoutSeconds: 1);

        using var blockerDb = fx.CreateContext();
        await using var blockerTx = await blockerDb.Database.BeginTransactionAsync();
        blockerDb.Workers.Add(Worker.Create("lock-holder"));
        await blockerDb.SaveChangesAsync(); // takes SQLite's write lock; left uncommitted below

        var service = fx.NewService();
        var day = new DateOnly(2026, 9, 23);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, day, 2m, "blocked")));
        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);

        await blockerTx.RollbackAsync();

        using (var freshDb = fx.CreateContext())
        {
            Assert.Equal(0, await freshDb.Worklogs.CountAsync());
        }

        var retryService = fx.NewService();
        var retried = await retryService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, day, 2m, "retried"));
        Assert.Equal(2m, retried.Hours);
        Assert.Equal(0, retried.Version);
    }

    /// <summary>
    /// <see cref="WorklogService.UpdateWorklogAsync"/> opens its own explicit transaction, so
    /// (like Create above) the block happens at `BeginTransactionAsync` and surfaces as a raw
    /// SqliteException that <c>WithBusyMappingAsync</c>'s first catch clause maps to Conflict.
    /// </summary>
    [Fact]
    public async Task Update_blocked_by_another_open_transaction_persists_nothing_then_a_retry_succeeds_once_unblocked()
    {
        using var fx = new SqliteWorklogFixture(busyTimeoutSeconds: 1);
        var day = new DateOnly(2026, 9, 23);
        var setupService = fx.NewService();
        var created = await setupService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, day, 4m, "orig"));

        using var blockerDb = fx.CreateContext();
        await using var blockerTx = await blockerDb.Database.BeginTransactionAsync();
        blockerDb.Workers.Add(Worker.Create("lock-holder"));
        await blockerDb.SaveChangesAsync(); // takes SQLite's write lock; left uncommitted below

        var service = fx.NewService();
        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateWorklogAsync(new UpdateWorklogCommand(created.Id, created.Version, day, 5m, "blocked")));
        stopwatch.Stop();

        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900),
            $"expected the second writer to block for close to the 1s busy timeout before failing, actually took {stopwatch.Elapsed}");

        await blockerTx.RollbackAsync();

        using (var freshDb = fx.CreateContext())
        {
            var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == created.Id);
            Assert.Equal(4m, reloaded.Hours);
            Assert.Equal("orig", reloaded.Description);
            Assert.Equal(day, reloaded.WorkDate);
            Assert.Equal(0, reloaded.Version);
        }

        var retryService = fx.NewService();
        var retried = await retryService.UpdateWorklogAsync(new UpdateWorklogCommand(created.Id, created.Version, day, 5m, "retried"));
        Assert.Equal(5m, retried.Hours);
        Assert.Equal(1, retried.Version);
    }

    /// <summary>
    /// <see cref="WorklogService.SubmitWorklogAsync"/> (via <c>TransitionAsync</c>) opens no
    /// explicit transaction: it loads with a plain SELECT, mutates in memory, then relies on
    /// <c>SaveChangesAsync</c> alone to persist the write. The blocker here deliberately does
    /// NOT perform any write of its own — only `BeginTransactionAsync` (BEGIN IMMEDIATE), which
    /// takes SQLite's RESERVED write lock immediately without needing a write statement (see
    /// docs/04) — so the SELECT still succeeds on the blocked connection and the block is
    /// observed only once <c>SaveChangesAsync</c> tries to write, exercising
    /// <c>WithBusyMappingAsync</c>'s <c>DbUpdateException</c>-wrapped catch clause rather than
    /// the raw-SqliteException one already covered by Create/Update above.
    /// </summary>
    [Fact]
    public async Task Submit_blocked_while_SaveChanges_cannot_acquire_the_write_lock_persists_nothing_then_a_retry_succeeds_once_unblocked()
    {
        using var fx = new SqliteWorklogFixture(busyTimeoutSeconds: 1);
        var day = new DateOnly(2026, 9, 23);
        var setupService = fx.NewService();
        var created = await setupService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, day, 4m, "orig"));

        using var blockerDb = fx.CreateContext();
        await using var blockerTx = await blockerDb.Database.BeginTransactionAsync(); // RESERVED only, no write — see summary above

        var service = fx.NewService();
        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version)));
        stopwatch.Stop();

        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900),
            $"expected the blocked SaveChanges to wait close to the 1s busy timeout before failing, actually took {stopwatch.Elapsed}");

        await blockerTx.RollbackAsync();

        using (var freshDb = fx.CreateContext())
        {
            var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == created.Id);
            Assert.Equal(WorklogStatus.Draft, reloaded.Status);
            Assert.Equal(0, reloaded.Version);
        }

        var retryService = fx.NewService();
        var retried = await retryService.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version));
        Assert.Equal(WorklogStatus.Submitted, retried.Status);
        Assert.Equal(1, retried.Version);
    }

    /// <summary>Same reasoning as Submit above, against Approve instead.</summary>
    [Fact]
    public async Task Approve_blocked_while_SaveChanges_cannot_acquire_the_write_lock_persists_nothing_then_a_retry_succeeds_once_unblocked()
    {
        using var fx = new SqliteWorklogFixture(busyTimeoutSeconds: 1);
        var day = new DateOnly(2026, 9, 23);
        var setupService = fx.NewService();
        var created = await setupService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, day, 4m, "orig"));
        var submitted = await setupService.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version));

        using var blockerDb = fx.CreateContext();
        await using var blockerTx = await blockerDb.Database.BeginTransactionAsync(); // RESERVED only, no write

        var service = fx.NewService();
        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.ApproveWorklogAsync(new ApproveWorklogCommand(submitted.Id, submitted.Version)));
        stopwatch.Stop();

        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900),
            $"expected the blocked SaveChanges to wait close to the 1s busy timeout before failing, actually took {stopwatch.Elapsed}");

        await blockerTx.RollbackAsync();

        using (var freshDb = fx.CreateContext())
        {
            var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == submitted.Id);
            Assert.Equal(WorklogStatus.Submitted, reloaded.Status);
            Assert.Equal(1, reloaded.Version);
        }

        var retryService = fx.NewService();
        var retried = await retryService.ApproveWorklogAsync(new ApproveWorklogCommand(submitted.Id, submitted.Version));
        Assert.Equal(WorklogStatus.Approved, retried.Status);
        Assert.Equal(2, retried.Version);
    }
}
