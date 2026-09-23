using System.Security.Claims;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Authorization;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Api.Endpoints;

public sealed record CreateWorklogRequest(Guid ProjectId, DateOnly WorkDate, decimal Hours, string? Description);

public sealed record UpdateWorklogRequest(long ExpectedVersion, DateOnly WorkDate, decimal Hours, string? Description);

/// <summary>Body for submit/approve: the optimistic-concurrency contract
/// (docs/03-api-transactions.md "Optimistic concurrency contract") is a version in the request
/// body rather than an ETag/If-Match header, to keep the same shape across every mutating
/// worklog endpoint.</summary>
public sealed record TransitionRequest(long ExpectedVersion);

public sealed record WorklogResponse(
    Guid Id, Guid WorkerId, Guid ProjectId, DateOnly WorkDate, decimal Hours, string Description, string Status, Guid? InvoiceId, long Version)
{
    public static WorklogResponse From(Worklog w) =>
        new(w.Id, w.WorkerId, w.ProjectId, w.WorkDate, w.Hours, w.Description, w.Status.ToString(), w.InvoiceId, w.Version);
}

/// <summary>
/// docs/03-api-transactions.md "Worklogs" + docs/04 "Authorization": create/update/submit are
/// scoped to the caller's own worker id. Create derives WorkerId directly from the "sub" claim
/// (bypass-proof — no request field to spoof). Update/submit load the worklog first and check
/// WorkerId == caller after the fact (docs/04: "check after loading"), which is why they read
/// the worklog once here for the ownership check and again inside WorklogService's own
/// transaction — WorkerId is immutable once a worklog exists, so there is no race between the
/// two reads that matters.
/// </summary>
public static class WorklogEndpoints
{
    public static void MapWorklogEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/worklogs");

        group.MapPost("/", async (CreateWorklogRequest request, WorklogService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var command = new CreateWorklogCommand(user.GetWorkerId(), request.ProjectId, request.WorkDate, request.Hours, request.Description);
            var worklog = await service.CreateWorklogAsync(command, ct);
            return Results.Created($"/api/worklogs/{worklog.Id}", WorklogResponse.From(worklog));
        }).RequireAuthorization(Permissions.WorklogCreate);

        // No dedicated "worklog:read" permission exists in docs/04's policy list; any
        // authenticated caller may read a worklog by id.
        group.MapGet("/{id:guid}", async (Guid id, WorklogService service, CancellationToken ct) =>
            Results.Ok(WorklogResponse.From(await service.GetWorklogAsync(id, ct))))
            .RequireAuthorization();

        group.MapPut("/{id:guid}", async (Guid id, UpdateWorklogRequest request, WorklogService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var existing = await service.GetWorklogAsync(id, ct);
            if (ForbidIfNotOwner(existing, user) is { } forbidden)
            {
                return forbidden;
            }

            var command = new UpdateWorklogCommand(id, request.ExpectedVersion, request.WorkDate, request.Hours, request.Description);
            var worklog = await service.UpdateWorklogAsync(command, ct);
            return Results.Ok(WorklogResponse.From(worklog));
        }).RequireAuthorization(Permissions.WorklogUpdateOwn);

        group.MapPost("/{id:guid}/submit", async (Guid id, TransitionRequest request, WorklogService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var existing = await service.GetWorklogAsync(id, ct);
            if (ForbidIfNotOwner(existing, user) is { } forbidden)
            {
                return forbidden;
            }

            var worklog = await service.SubmitWorklogAsync(new SubmitWorklogCommand(id, request.ExpectedVersion), ct);
            return Results.Ok(WorklogResponse.From(worklog));
        }).RequireAuthorization(Permissions.WorklogSubmitOwn);

        group.MapPost("/{id:guid}/approve", async (Guid id, TransitionRequest request, WorklogService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var existing = await service.GetWorklogAsync(id, ct);
            if (existing.WorkerId == user.GetWorkerId())
            {
                return Forbidden("A worklog cannot be approved by its own worker.");
            }

            var worklog = await service.ApproveWorklogAsync(new ApproveWorklogCommand(id, request.ExpectedVersion), ct);
            return Results.Ok(WorklogResponse.From(worklog));
        }).RequireAuthorization(Permissions.WorklogApprove);
    }

    private static IResult? ForbidIfNotOwner(Worklog worklog, ClaimsPrincipal user) =>
        worklog.WorkerId == user.GetWorkerId() ? null : Forbidden("You may only act on your own worklogs.");

    private static IResult Forbidden(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: detail);
}
