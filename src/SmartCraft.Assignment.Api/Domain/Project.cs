namespace SmartCraft.Assignment.Api.Domain;

/// <summary>
/// A billable project, owning its worker assignments. Aggregate root.
/// Worklogs are not modeled as a collection here (docs/02-architecture.md):
/// they are independently addressable and have their own lifecycle/concurrency.
/// </summary>
public sealed class Project
{
    private readonly List<ProjectAssignment> _assignments = [];

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public IReadOnlyCollection<ProjectAssignment> Assignments => _assignments;

    private Project()
    {
    }

    public static Project Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException(DomainErrorKind.Validation, "Name must not be empty.");
        }

        return new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
        };
    }

    /// <summary>Assigns a worker to this project. Rejects a duplicate assignment
    /// (docs/02-architecture.md: unique (ProjectId, WorkerId)); this is a state
    /// conflict rather than bad input, so it maps to <see cref="DomainErrorKind.Conflict"/>,
    /// the same kind used elsewhere for "valid request, current state disagrees".</summary>
    public ProjectAssignment AssignWorker(Guid workerId)
    {
        if (workerId == Guid.Empty)
        {
            throw new DomainException(DomainErrorKind.Validation, "WorkerId must not be empty.");
        }

        if (HasAssignment(workerId))
        {
            throw new DomainException(DomainErrorKind.Conflict, "Worker is already assigned to this project.");
        }

        var assignment = ProjectAssignment.Create(Id, workerId);
        _assignments.Add(assignment);
        return assignment;
    }

    /// <summary>Rule 1: a worker may only log time against a project they're assigned to.</summary>
    public bool HasAssignment(Guid workerId) => _assignments.Any(a => a.WorkerId == workerId);
}
