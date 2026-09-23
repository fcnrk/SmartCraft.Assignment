namespace SmartCraft.Assignment.Api.Domain;

/// <summary>
/// A worker's billing context within a project. Owned by <see cref="Project"/>;
/// identity is the (ProjectId, WorkerId) pair, enforced unique by that being the
/// primary key (see AppDbContext). WorkerRole/HourlyRate are the billing terms
/// snapshotted into invoice lines at invoice-creation time (docs/01-domain.md
/// rule 12) — a worker can have a different role/rate on a different project.
/// </summary>
public sealed class ProjectAssignment
{
    public Guid ProjectId { get; private set; }
    public Guid WorkerId { get; private set; }
    public string WorkerRole { get; private set; } = string.Empty;
    public decimal HourlyRate { get; private set; }

    private ProjectAssignment()
    {
    }

    internal static ProjectAssignment Create(Guid projectId, Guid workerId, string workerRole, decimal hourlyRate)
    {
        if (string.IsNullOrWhiteSpace(workerRole))
        {
            throw new DomainException(DomainErrorKind.Validation, "WorkerRole must not be empty.");
        }

        if (hourlyRate <= 0)
        {
            throw new DomainException(DomainErrorKind.Validation, "HourlyRate must be greater than 0.");
        }

        return new ProjectAssignment
        {
            ProjectId = projectId,
            WorkerId = workerId,
            WorkerRole = workerRole,
            HourlyRate = hourlyRate,
        };
    }
}
