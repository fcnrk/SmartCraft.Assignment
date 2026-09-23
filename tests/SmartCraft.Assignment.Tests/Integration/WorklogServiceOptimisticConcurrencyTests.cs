using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// Optimistic concurrency via Worklog.Version (docs/04-concurrency-idempotency-auth.md
/// "Scenario: lost worklog update", "Scenario: lifecycle race"). Every scenario here loads
/// through one AppDbContext/connection and mutates through another, exactly like two API
/// requests against different instances.
/// </summary>
public class WorklogServiceOptimisticConcurrencyTests
{
    private static readonly DateOnly Day = new(2026, 9, 23);

    [Fact]
    public async Task Update_with_a_stale_expected_version_is_rejected_and_the_row_is_unchanged()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();
        var created = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 4m, "v0"));
        var updated = await service.UpdateWorklogAsync(new UpdateWorklogCommand(created.Id, created.Version, Day, 5m, "v1"));
        Assert.Equal(1, updated.Version);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateWorklogAsync(new UpdateWorklogCommand(created.Id, 0, Day, 9m, "stale")));
        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);

        using var freshDb = fx.CreateContext();
        var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == created.Id);
        Assert.Equal(5m, reloaded.Hours);
        Assert.Equal("v1", reloaded.Description);
        Assert.Equal(1, reloaded.Version);
    }

    [Fact]
    public async Task Version_increments_by_exactly_one_on_each_successful_mutation()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();

        var created = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 4m, "c"));
        Assert.Equal(0, created.Version);

        var updated = await service.UpdateWorklogAsync(new UpdateWorklogCommand(created.Id, created.Version, Day, 5m, "u"));
        Assert.Equal(1, updated.Version);

        var submitted = await service.SubmitWorklogAsync(new SubmitWorklogCommand(updated.Id, updated.Version));
        Assert.Equal(2, submitted.Version);

        var approved = await service.ApproveWorklogAsync(new ApproveWorklogCommand(submitted.Id, submitted.Version));
        Assert.Equal(3, approved.Version);
    }

    [Fact]
    public async Task Two_contexts_loading_the_same_version_and_updating_concurrently_lose_exactly_one_update()
    {
        using var fx = new SqliteWorklogFixture();
        var seedService = fx.NewService();
        var created = await seedService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 4m, "orig"));

        var serviceA = fx.NewService();
        var serviceB = fx.NewService();
        var gate = new TaskCompletionSource();

        var taskA = Task.Run(async () =>
        {
            await gate.Task;
            return await ConcurrencyTestHelpers.AttemptAsync(() =>
                serviceA.UpdateWorklogAsync(new UpdateWorklogCommand(created.Id, created.Version, Day, 6m, "A")));
        });
        var taskB = Task.Run(async () =>
        {
            await gate.Task;
            return await ConcurrencyTestHelpers.AttemptAsync(() =>
                serviceB.UpdateWorklogAsync(new UpdateWorklogCommand(created.Id, created.Version, Day, 7m, "B")));
        });

        gate.SetResult();
        var results = await Task.WhenAll(taskA, taskB);

        // Lost-update prevention (docs/04): exactly one write survives, the loser sees an
        // explicit Conflict rather than silently disappearing.
        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success && r.FailureKind == DomainErrorKind.Conflict));

        var winner = results.Single(r => r.Success).Worklog!;

        using var freshDb = fx.CreateContext();
        var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == created.Id);
        Assert.Equal(1, reloaded.Version);
        Assert.Equal(winner.Description, reloaded.Description);
        Assert.Equal(winner.Hours, reloaded.Hours);
    }

    [Fact]
    public async Task Concurrent_Submit_attempts_on_the_same_worklog_allow_exactly_one_winner()
    {
        using var fx = new SqliteWorklogFixture();
        var seedService = fx.NewService();
        var created = await seedService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 4m, "orig"));

        var serviceA = fx.NewService();
        var serviceB = fx.NewService();
        var gate = new TaskCompletionSource();

        var taskA = Task.Run(async () =>
        {
            await gate.Task;
            return await ConcurrencyTestHelpers.AttemptAsync(() =>
                serviceA.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version)));
        });
        var taskB = Task.Run(async () =>
        {
            await gate.Task;
            return await ConcurrencyTestHelpers.AttemptAsync(() =>
                serviceB.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version)));
        });

        gate.SetResult();
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success && r.FailureKind == DomainErrorKind.Conflict));

        using var freshDb = fx.CreateContext();
        var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == created.Id);
        Assert.Equal(WorklogStatus.Submitted, reloaded.Status);
        Assert.Equal(1, reloaded.Version);
    }

    [Fact]
    public async Task Approving_a_worklog_that_is_concurrently_being_submitted_never_silently_succeeds()
    {
        using var fx = new SqliteWorklogFixture();
        var seedService = fx.NewService();
        var created = await seedService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 4m, "orig"));

        var submitService = fx.NewService();
        var approveService = fx.NewService();
        var gate = new TaskCompletionSource();

        var submitTask = Task.Run(async () =>
        {
            await gate.Task;
            return await ConcurrencyTestHelpers.AttemptAsync(() =>
                submitService.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version)));
        });
        var approveTask = Task.Run(async () =>
        {
            await gate.Task;
            return await ConcurrencyTestHelpers.AttemptAsync(() =>
                approveService.ApproveWorklogAsync(new ApproveWorklogCommand(created.Id, created.Version)));
        });

        gate.SetResult();
        var results = await Task.WhenAll(submitTask, approveTask);

        // Approve is racing a Draft worklog into Submitted. It must never observe a silently
        // approved Draft: either it fails the lifecycle check (Draft is not Submitted, if it
        // reads before the submit commits) or the stale-version check (if it reads after).
        // Either way it surfaces as Conflict and Submit is the only mutation that survives.
        Assert.True(results[0].Success, "submit should not be blocked by the concurrent (failing) approve");
        Assert.False(results[1].Success);
        Assert.Equal(DomainErrorKind.Conflict, results[1].FailureKind);

        using var freshDb = fx.CreateContext();
        var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == created.Id);
        Assert.Equal(WorklogStatus.Submitted, reloaded.Status);
        Assert.Equal(1, reloaded.Version);
    }
}
