using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// Regression coverage for the AI_LOG AI-009 finding I3: <see cref="WorklogService.UpdateWorklogAsync"/>
/// used to run the rule-3 daily-hours check (docs/01 rule 3) BEFORE <c>Worklog.Update</c>'s own
/// rule-4/8 (Draft-only) and rule-2 (0 &lt; Hours &lt;= 24) checks, so a request that failed for an
/// unrelated reason could still surface the daily-limit's Validation message/kind instead of the
/// more specific one. The fix (git diff, uncommitted) reordered `Worklog.Update(...)` to run first.
/// These tests pin the new ordering down as an explicit, reviewable contract rather than trusting
/// the code's own doc comment.
/// </summary>
public class WorklogServiceUpdateOrderingTests
{
    private static readonly DateOnly Day = new(2026, 9, 23);

    [Theory]
    [InlineData(WorklogStatus.Submitted)]
    [InlineData(WorklogStatus.Approved)]
    public async Task Updating_a_non_Draft_worklog_that_would_also_exceed_the_daily_limit_is_a_lifecycle_Conflict_not_a_daily_limit_Validation(WorklogStatus status)
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();

        // Filler worklog eats most of the day's 24h budget so the attempted update below would
        // ALSO breach rule 3 if it ever reached that check. The point of the test is which
        // failure wins, not whether the edit would otherwise be numerically valid.
        await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 20m, "filler"));

        var target = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 2m, "target"));
        target = await service.SubmitWorklogAsync(new SubmitWorklogCommand(target.Id, target.Version));
        if (status == WorklogStatus.Approved)
        {
            target = await service.ApproveWorklogAsync(new ApproveWorklogCommand(target.Id, target.Version));
        }
        Assert.Equal(status, target.Status);

        // 20 (filler) + 10 (this edit) = 30 > 24, so the daily-limit check would also fail if
        // Worklog.Update's own lifecycle check didn't reject the edit first.
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateWorklogAsync(new UpdateWorklogCommand(target.Id, target.Version, Day, 10m, "edited")));

        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
        Assert.Contains("Draft", ex.Message);

        using var freshDb = fx.CreateContext();
        var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == target.Id);
        Assert.Equal(2m, reloaded.Hours);
        Assert.Equal("target", reloaded.Description);
        Assert.Equal(Day, reloaded.WorkDate);
        Assert.Equal(target.Version, reloaded.Version);
        Assert.Equal(status, reloaded.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(25)]
    public async Task Updating_a_Draft_worklog_with_out_of_range_hours_reports_the_rule2_message_not_the_daily_limit_message(decimal invalidHours)
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();

        // Another worklog exists on the same day so, if the daily-limit check ran first (or ran
        // at all against an obviously invalid hours value), it would have its own reason to
        // reject this update too. The rule-2 message must win regardless.
        await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 20m, "filler"));
        var target = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 2m, "target"));

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateWorklogAsync(new UpdateWorklogCommand(target.Id, target.Version, Day, invalidHours, "edited")));

        Assert.Equal(DomainErrorKind.Validation, ex.Kind);
        Assert.Contains("Hours must be greater than 0", ex.Message);
        Assert.DoesNotContain("daily", ex.Message, StringComparison.OrdinalIgnoreCase);

        using var freshDb = fx.CreateContext();
        var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == target.Id);
        Assert.Equal(2m, reloaded.Hours);
        Assert.Equal("target", reloaded.Description);
        Assert.Equal(Day, reloaded.WorkDate);
        Assert.Equal(0, reloaded.Version);
    }

    /// <summary>
    /// Regression for AI-009 finding M1: <c>Worklog.Update(...)</c> mutates the change-tracked
    /// entity in memory BEFORE <c>EnsureDailyHoursWithinLimitAsync</c> can still reject the call
    /// -- nothing is committed (the transaction rolls back), but a failed Update leaves the
    /// SAME service/context's change tracker holding the rejected Hours/Description. If a later
    /// call on that same service (e.g. Submit, which only intends to flip Status) reuses the
    /// tracked instance instead of a clean load, SaveChanges can silently persist the phantom
    /// values alongside the intended change -- corrupting the row and violating rule 3 without
    /// ever going through a check that was supposed to prevent it.
    /// </summary>
    [Fact]
    public async Task A_failed_Update_does_not_leave_phantom_state_that_a_later_Submit_on_the_same_service_persists()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();

        await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 20m, "filler"));
        var target = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 1.5m, "target"));

        // 20 (filler) + 10 (this edit) = 30 > 24: rejected by the rule-3 daily-limit check, but
        // only after Worklog.Update(...) already mutated the tracked entity in memory to
        // Hours=10/Description="edited"/WorkDate=Day.
        var updateEx = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateWorklogAsync(new UpdateWorklogCommand(target.Id, target.Version, Day, 10m, "edited")));
        Assert.Equal(DomainErrorKind.Validation, updateEx.Kind);

        long persistedVersionAfterFailedUpdate;
        using (var freshDb = fx.CreateContext())
        {
            var afterFailedUpdate = await freshDb.Worklogs.SingleAsync(w => w.Id == target.Id);
            Assert.Equal(1.5m, afterFailedUpdate.Hours); // confirms the failed update was never committed
            persistedVersionAfterFailedUpdate = afterFailedUpdate.Version;
        }

        // Submit only intends to flip Status -> Submitted. On the SAME service, if the change
        // tracker still holds the phantom Hours=10/Description="edited" from the failed Update
        // above, this call would silently persist those too. A successful submit that leaves
        // the worklog at its real, original 1.5h is a fine outcome -- what must not happen is
        // the phantom state reaching the database.
        await service.SubmitWorklogAsync(new SubmitWorklogCommand(target.Id, persistedVersionAfterFailedUpdate));

        using var finalDb = fx.CreateContext();
        var reloaded = await finalDb.Worklogs.SingleAsync(w => w.Id == target.Id);
        Assert.Equal(1.5m, reloaded.Hours);
        Assert.Equal("target", reloaded.Description);
        Assert.Equal(Day, reloaded.WorkDate);

        var dayTotal = (await finalDb.Worklogs
            .Where(w => w.WorkerId == fx.WorkerId && w.WorkDate == Day)
            .Select(w => w.Hours)
            .ToListAsync()).Sum();
        Assert.True(dayTotal <= 24m, $"day total {dayTotal} exceeded the 24h limit after the phantom-state Submit");
    }
}
