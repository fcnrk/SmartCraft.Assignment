using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// Rule 1 (docs/01-domain.md): "A worker may record time only against a project to which
/// the worker is assigned." Exercised through WorklogService against a real SQLite file so
/// the worker/project existence checks and the assignment check run against actually
/// persisted rows, not in-memory fakes.
///
/// Error-kind choices asserted here (unknown worker/project id -> NotFound, worker exists
/// but isn't assigned -> Validation) match the documented decision in AI_LOG AI-007; that
/// entry flags them as "pending review" rather than settled, so these assertions double as
/// a durable record of the current contract for the senior-reviewer to confirm or challenge.
/// </summary>
public class WorklogServiceRule1Tests
{
    private static readonly DateOnly Date = new(2026, 9, 23);

    [Fact]
    public async Task Create_for_an_assigned_worker_succeeds_and_is_persisted()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();

        var created = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Date, 4m, "desc"));

        Assert.Equal(WorklogStatus.Draft, created.Status);
        Assert.Equal(0, created.Version);

        using var freshDb = fx.CreateContext();
        var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == created.Id);
        Assert.Equal(fx.WorkerId, reloaded.WorkerId);
        Assert.Equal(fx.ProjectId, reloaded.ProjectId);
        Assert.Equal(4m, reloaded.Hours);
        Assert.Equal("desc", reloaded.Description);
    }

    [Fact]
    public async Task Create_for_a_worker_not_assigned_to_the_project_is_rejected_as_Validation_and_persists_nothing()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateWorklogAsync(new CreateWorklogCommand(fx.UnassignedWorkerId, fx.ProjectId, Date, 4m, "desc")));

        Assert.Equal(DomainErrorKind.Validation, ex.Kind);

        using var freshDb = fx.CreateContext();
        Assert.False(await freshDb.Worklogs.AnyAsync());
    }

    [Fact]
    public async Task Create_for_an_unknown_project_is_NotFound_and_persists_nothing()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, Guid.NewGuid(), Date, 4m, "desc")));

        Assert.Equal(DomainErrorKind.NotFound, ex.Kind);

        using var freshDb = fx.CreateContext();
        Assert.False(await freshDb.Worklogs.AnyAsync());
    }

    [Fact]
    public async Task Create_for_an_unknown_worker_is_NotFound_and_persists_nothing()
    {
        using var fx = new SqliteWorklogFixture();
        var service = fx.NewService();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.CreateWorklogAsync(new CreateWorklogCommand(Guid.NewGuid(), fx.ProjectId, Date, 4m, "desc")));

        Assert.Equal(DomainErrorKind.NotFound, ex.Kind);

        using var freshDb = fx.CreateContext();
        Assert.False(await freshDb.Worklogs.AnyAsync());
    }
}
