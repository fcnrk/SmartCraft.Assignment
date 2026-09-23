using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Authorization;
using SmartCraft.Assignment.Api.Infrastructure;

namespace SmartCraft.Assignment.Api.Endpoints;

public sealed record ProjectSummaryResponse(Guid Id, string Name);

public sealed record ProjectAssignmentResponse(Guid WorkerId, string WorkerRole, decimal HourlyRate);

public sealed record ProjectResponse(Guid Id, string Name, IReadOnlyList<ProjectAssignmentResponse> Assignments);

/// <summary>Read-only (docs/03-api-transactions.md): project/worker/assignment setup comes
/// from seed data (Infrastructure/SeedData.cs), not a write API.</summary>
public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects").RequireAuthorization(Permissions.ProjectRead);

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Projects
                .Select(p => new ProjectSummaryResponse(p.Id, p.Name))
                .ToListAsync(ct)));

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (project is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new ProjectResponse(
                project.Id,
                project.Name,
                project.Assignments.Select(a => new ProjectAssignmentResponse(a.WorkerId, a.WorkerRole, a.HourlyRate)).ToList()));
        });
    }
}
