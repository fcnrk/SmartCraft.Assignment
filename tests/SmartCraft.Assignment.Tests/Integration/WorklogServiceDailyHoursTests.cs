using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;
using SmartCraft.Assignment.Api.Infrastructure;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// Rule 3 (docs/01-domain.md): total recorded hours for a worker on one calendar date must
/// not exceed 24, across every status and every project, summed against real persisted rows
/// (not an in-memory fake — SQLite's decimal-as-TEXT storage plus the C#-side sum is what's
/// actually exercised here).
/// </summary>
public class WorklogServiceDailyHoursTests
{
    private static readonly DateOnly Day1 = new(2026, 9, 23);
    private static readonly DateOnly Day2 = new(2026, 9, 24);

    [Fact]
    public async Task Existing_20h_plus_4h_is_allowed_at_exactly_the_24h_boundary()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();
        await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 20m, "a"));

        var created = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 4m, "b"));

        Assert.Equal(4m, created.Hours);
        using var freshDb = fx.CreateContext();
        Assert.Equal(24m, await SumHoursAsync(freshDb, fx.WorkerId, Day1));
    }

    [Fact]
    public async Task Existing_20h_plus_4_01h_is_rejected_just_past_the_boundary()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();
        await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 20m, "a"));

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 4.01m, "b")));

        Assert.Equal(DomainErrorKind.Validation, ex.Kind);

        using var freshDb = fx.CreateContext();
        Assert.Equal(20m, await SumHoursAsync(freshDb, fx.WorkerId, Day1));
    }

    [Fact]
    public async Task Hours_across_different_projects_on_the_same_day_count_together()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();
        await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 20m, "alpha"));

        // Same worker, same day, a DIFFERENT project — still counts against the 24h total.
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.SecondProjectId, Day1, 4.01m, "beta")));
        Assert.Equal(DomainErrorKind.Validation, ex.Kind);

        // Exactly the remaining 4h on the second project is still allowed.
        var created = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.SecondProjectId, Day1, 4m, "beta-ok"));
        Assert.Equal(4m, created.Hours);
    }

    [Fact]
    public async Task Hours_on_a_different_day_do_not_count_towards_the_limit()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();
        await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 24m, "full-day1"));

        var created = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day2, 24m, "full-day2"));

        Assert.Equal(24m, created.Hours);
    }

    [Fact]
    public async Task Hours_for_a_different_worker_do_not_count_towards_the_limit()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();
        await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 24m, "worker-a-full"));

        var created = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.OtherWorkerId, fx.ProjectId, Day1, 24m, "worker-b-full"));

        Assert.Equal(24m, created.Hours);
    }

    [Theory]
    [InlineData(WorklogStatus.Draft)]
    [InlineData(WorklogStatus.Submitted)]
    [InlineData(WorklogStatus.Approved)]
    public async Task Existing_hours_count_towards_the_limit_regardless_of_status(WorklogStatus status)
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();
        var seed = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 20m, "seed"));

        if (status is WorklogStatus.Submitted or WorklogStatus.Approved)
        {
            seed = await service.SubmitWorklogAsync(new SubmitWorklogCommand(seed.Id, seed.Version));
        }

        if (status is WorklogStatus.Approved)
        {
            seed = await service.ApproveWorklogAsync(new ApproveWorklogCommand(seed.Id, seed.Version));
        }

        Assert.Equal(status, seed.Status);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 4.01m, "extra")));
        Assert.Equal(DomainErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Update_excludes_its_own_previous_hours_so_a_24h_worklog_can_shrink_to_23h()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();
        var created = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 24m, "full"));

        var updated = await service.UpdateWorklogAsync(new UpdateWorklogCommand(created.Id, created.Version, Day1, 23m, "shrunk"));

        Assert.Equal(23m, updated.Hours);
    }

    [Fact]
    public async Task Update_can_keep_a_worklog_at_the_same_24h_total_it_already_occupies()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();
        var created = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 24m, "full"));

        var updated = await service.UpdateWorklogAsync(new UpdateWorklogCommand(created.Id, created.Version, Day1, 24m, "still-full"));

        Assert.Equal(24m, updated.Hours);
    }

    [Fact]
    public async Task Update_moving_a_worklog_to_a_date_that_is_already_full_is_rejected_and_leaves_it_unchanged()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();
        await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day2, 24m, "full-day2"));
        var movable = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 1m, "movable"));

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateWorklogAsync(new UpdateWorklogCommand(movable.Id, movable.Version, Day2, 1m, "moved")));

        Assert.Equal(DomainErrorKind.Validation, ex.Kind);

        using var freshDb = fx.CreateContext();
        var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == movable.Id);
        Assert.Equal(Day1, reloaded.WorkDate);
        Assert.Equal(1m, reloaded.Hours);
        Assert.Equal("movable", reloaded.Description);
        Assert.Equal(0, reloaded.Version);
    }

    [Fact]
    public async Task A_failed_create_persists_no_row_at_all()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();
        await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 24m, "full"));

        await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day1, 0.01m, "overflow")));

        using var freshDb = fx.CreateContext();
        Assert.Equal(1, await freshDb.Worklogs.CountAsync());
    }

    private static async Task<decimal> SumHoursAsync(AppDbContext db, Guid workerId, DateOnly date)
        => (await db.Worklogs.Where(w => w.WorkerId == workerId && w.WorkDate == date).Select(w => w.Hours).ToListAsync()).Sum();
}
