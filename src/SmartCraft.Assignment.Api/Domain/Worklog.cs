namespace SmartCraft.Assignment.Api.Domain;

public enum WorklogStatus
{
    Draft,
    Submitted,
    Approved,
    Invoiced,
}

/// <summary>
/// A worker's reported work against a project for one calendar date.
/// Aggregate root; lifecycle: Draft -> Submitted -> Approved -> Invoiced.
///
/// Rule 1 (worker must be assigned to the project) and rule 3 (daily 24h
/// total across a worker's worklogs) are cross-aggregate/cross-row checks
/// enforced by the application layer, not here.
/// </summary>
public sealed class Worklog
{
    public Guid Id { get; private set; }
    public Guid WorkerId { get; private set; }
    public Guid ProjectId { get; private set; }
    public DateOnly WorkDate { get; private set; }
    public decimal Hours { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public WorklogStatus Status { get; private set; }
    public Guid? InvoiceId { get; private set; }

    /// <summary>
    /// Optimistic-concurrency token. Persistence (AppDbContext.SaveChanges) increments this by
    /// exactly one for every persisted mutation of this row; the domain never sets it beyond the
    /// initial 0 at creation. See AppDbContext for why the increment lives there rather than here.
    /// </summary>
    public long Version { get; private set; }

    private Worklog()
    {
    }

    public static Worklog Create(Guid workerId, Guid projectId, DateOnly workDate, decimal hours, string? description)
    {
        if (workerId == Guid.Empty)
        {
            throw new DomainException(DomainErrorKind.Validation, "WorkerId must not be empty.");
        }

        if (projectId == Guid.Empty)
        {
            throw new DomainException(DomainErrorKind.Validation, "ProjectId must not be empty.");
        }

        ValidateHours(hours);

        return new Worklog
        {
            Id = Guid.NewGuid(),
            WorkerId = workerId,
            ProjectId = projectId,
            WorkDate = workDate,
            Hours = hours,
            Description = description ?? string.Empty,
            Status = WorklogStatus.Draft,
            InvoiceId = null,
            Version = 0,
        };
    }

    /// <summary>Rule 4/8: only a Draft worklog's content may change.</summary>
    public void Update(DateOnly workDate, decimal hours, string? description)
    {
        EnsureStatus(WorklogStatus.Draft, "Only Draft worklogs can be edited.");
        ValidateHours(hours);

        WorkDate = workDate;
        Hours = hours;
        Description = description ?? string.Empty;
    }

    /// <summary>Rule 5: only a Draft worklog can be submitted.</summary>
    public void Submit()
    {
        EnsureStatus(WorklogStatus.Draft, "Only Draft worklogs can be submitted.");
        Status = WorklogStatus.Submitted;
    }

    /// <summary>Rule 6: only a Submitted worklog can be approved.</summary>
    public void Approve()
    {
        EnsureStatus(WorklogStatus.Submitted, "Only Submitted worklogs can be approved.");
        Status = WorklogStatus.Approved;
    }

    /// <summary>
    /// Rule 7: only an Approved worklog can be invoiced. Rule 9 (at most one
    /// invoice) falls out of Approved -> Invoiced being a one-way transition.
    /// </summary>
    public void MarkInvoiced(Guid invoiceId)
    {
        if (invoiceId == Guid.Empty)
        {
            throw new DomainException(DomainErrorKind.Validation, "InvoiceId must not be empty.");
        }

        EnsureStatus(WorklogStatus.Approved, "Only Approved worklogs can be invoiced.");
        Status = WorklogStatus.Invoiced;
        InvoiceId = invoiceId;
    }

    private void EnsureStatus(WorklogStatus required, string message)
    {
        if (Status != required)
        {
            throw new DomainException(DomainErrorKind.Conflict, message);
        }
    }

    /// <summary>Rule 2: 0 &lt; Hours &lt;= 24.</summary>
    private static void ValidateHours(decimal hours)
    {
        if (hours <= 0 || hours > 24)
        {
            throw new DomainException(DomainErrorKind.Validation, "Hours must be greater than 0 and no greater than 24.");
        }
    }
}
