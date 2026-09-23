namespace SmartCraft.Assignment.Api.Domain;

/// <summary>
/// A worker's billing context within a project. Owned by <see cref="Project"/>;
/// identity is the (ProjectId, WorkerId) pair, enforced unique by that being the
/// primary key (see AppDbContext). Role/hourly rate are left out here — they
/// arrive with invoicing (docs/06-implementation-plan.md Phase 3) and would
/// otherwise be speculative for what this iteration needs (rule 1 enforcement).
/// </summary>
public sealed class ProjectAssignment
{
    public Guid ProjectId { get; private set; }
    public Guid WorkerId { get; private set; }

    private ProjectAssignment()
    {
    }

    internal static ProjectAssignment Create(Guid projectId, Guid workerId)
        => new()
        {
            ProjectId = projectId,
            WorkerId = workerId,
        };
}
