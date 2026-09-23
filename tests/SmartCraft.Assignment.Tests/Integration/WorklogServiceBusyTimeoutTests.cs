using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// docs/04-concurrency-idempotency-auth.md POC decision: a writer that cannot acquire
/// SQLite's single database-wide write lock blocks for up to the connection's busy timeout,
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
}
