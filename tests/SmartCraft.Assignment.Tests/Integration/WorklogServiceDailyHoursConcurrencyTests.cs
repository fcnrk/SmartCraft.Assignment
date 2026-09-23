using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// Rule 3 under genuine cross-connection concurrency (docs/04-concurrency-idempotency-auth.md
/// "Scenario: daily-hours race" — "current total is 20 hours, two concurrent requests each
/// add 3 hours... both can read 20 and independently decide 23 is valid, resulting in 26").
/// Every WorklogService instance here has its own AppDbContext/SqliteConnection against the
/// same file, and every attempt is gated behind a shared TaskCompletionSource so they start
/// together rather than sequentially.
/// </summary>
public class WorklogServiceDailyHoursConcurrencyTests
{
    private static readonly DateOnly Day = new(2026, 9, 23);

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task Concurrent_3h_creates_against_an_existing_20h_total_allow_exactly_one_success(int concurrency)
    {
        using var fx = new SqliteWorklogFixture();
        var seedService = fx.NewService();
        await seedService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 20m, "seed"));

        var services = Enumerable.Range(0, concurrency).Select(_ => fx.NewService()).ToArray();
        var gate = new TaskCompletionSource();

        var tasks = services.Select((service, i) => Task.Run(async () =>
        {
            await gate.Task;
            return await ConcurrencyTestHelpers.AttemptAsync(() =>
                service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 3m, $"attempt-{i}")));
        })).ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.All(results.Where(r => !r.Success), r => Assert.Equal(DomainErrorKind.Validation, r.FailureKind));

        using var freshDb = fx.CreateContext();
        var total = (await freshDb.Worklogs
            .Where(w => w.WorkerId == fx.WorkerId && w.WorkDate == Day)
            .Select(w => w.Hours)
            .ToListAsync()).Sum();

        Assert.Equal(23m, total);
        Assert.True(total <= 24m);
    }

    [Fact]
    public async Task Concurrent_update_and_create_on_the_same_worker_day_allow_exactly_one_success()
    {
        using var fx = new SqliteWorklogFixture();
        var seedService = fx.NewService();
        var existing = await seedService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 20m, "existing"));

        var updateService = fx.NewService();
        var createService = fx.NewService();
        var gate = new TaskCompletionSource();

        // Standalone, both would succeed (22h alone fits under 24; +3h on top of the
        // original 20h also fits) — only running them concurrently proves the daily-hours
        // check is safe against the other writer's in-flight change.
        var updateTask = Task.Run(async () =>
        {
            await gate.Task;
            return await ConcurrencyTestHelpers.AttemptAsync(() =>
                updateService.UpdateWorklogAsync(new UpdateWorklogCommand(existing.Id, existing.Version, Day, 22m, "bumped")));
        });
        var createTask = Task.Run(async () =>
        {
            await gate.Task;
            return await ConcurrencyTestHelpers.AttemptAsync(() =>
                createService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 3m, "new")));
        });

        gate.SetResult();
        var results = await Task.WhenAll(updateTask, createTask);

        Assert.Equal(1, results.Count(r => r.Success));

        using var freshDb = fx.CreateContext();
        var total = (await freshDb.Worklogs
            .Where(w => w.WorkerId == fx.WorkerId && w.WorkDate == Day)
            .Select(w => w.Hours)
            .ToListAsync()).Sum();

        Assert.True(total <= 24m);
    }
}
