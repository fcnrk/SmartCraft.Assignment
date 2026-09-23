using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Api.Infrastructure;

/// <summary>
/// Fixed, documented seed ids so a Swagger/curl user can obtain a dev token for a known worker
/// and immediately exercise the API (docs/03-api-transactions.md, README.md). Seeded once, only
/// if the database is empty ("if empty" check: no Projects yet) — safe to call on every
/// startup.
/// </summary>
public static class SeedData
{
    public static readonly Guid WorkerAId = Guid.Parse("11111111-1111-1111-1111-111111111111"); // Alice, Developer on Project Phoenix
    public static readonly Guid WorkerBId = Guid.Parse("22222222-2222-2222-2222-222222222222"); // Bob, Tester on Project Phoenix
    public static readonly Guid ApproverId = Guid.Parse("33333333-3333-3333-3333-333333333333"); // Carol, approver/billing — no project assignment needed
    public static readonly Guid ProjectId = Guid.Parse("44444444-4444-4444-4444-444444444444"); // Project Phoenix

    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.Projects.AnyAsync(ct))
        {
            return;
        }

        db.Workers.AddRange(
            Worker.Create("Alice (Developer)", WorkerAId),
            Worker.Create("Bob (Tester)", WorkerBId),
            Worker.Create("Carol (Approver / Billing)", ApproverId));

        var project = Project.Create("Project Phoenix", ProjectId);
        project.AssignWorker(WorkerAId, "Developer", 100m);
        project.AssignWorker(WorkerBId, "Tester", 80m);
        db.Projects.Add(project);

        await db.SaveChangesAsync(ct);
    }
}
