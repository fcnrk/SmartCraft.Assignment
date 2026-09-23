namespace SmartCraft.Assignment.Api.Domain;

/// <summary>
/// Aggregate root representing a generated invoice for one project's approved, uninvoiced
/// worklogs (docs/02-architecture.md "Invoice aggregate"). Owns its lines; a line is a
/// historical calculation snapshot and is immutable once created (rule 17) — there is no
/// line-mutation API, and Invoice itself exposes no post-creation mutators.
/// </summary>
public sealed class Invoice
{
    private readonly List<InvoiceLine> _lines = [];

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<InvoiceLine> Lines => _lines;
    public decimal Total { get; private set; }

    private Invoice()
    {
    }

    /// <summary>
    /// Creates an invoice from already-calculated lines. Atomically claiming the underlying
    /// worklogs (rule 16) is the caller's (InvoiceService) transaction, not this factory's
    /// concern — this only builds the aggregate in memory. Requires at least one line: "no
    /// eligible worklogs" is a caller-level validation error (rule 11), not a valid empty
    /// invoice, so InvoiceService is expected to check that before calling this; this guard
    /// is a defensive backstop.
    /// </summary>
    public static Invoice Create(Guid projectId, IReadOnlyList<InvoiceLine> lines, DateTimeOffset createdAt)
    {
        if (projectId == Guid.Empty)
        {
            throw new DomainException(DomainErrorKind.Validation, "ProjectId must not be empty.");
        }

        if (lines.Count == 0)
        {
            throw new DomainException(DomainErrorKind.Validation, "An invoice must have at least one line.");
        }

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            CreatedAt = createdAt,
            Total = lines.Sum(l => l.LineTotal),
        };
        invoice._lines.AddRange(lines);
        return invoice;
    }
}
