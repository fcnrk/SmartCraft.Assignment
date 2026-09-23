namespace SmartCraft.Assignment.Api.Infrastructure;

/// <summary>
/// Persistence record backing invoice-creation idempotency (docs/04-concurrency-idempotency-
/// auth.md "Idempotency"). Not a domain aggregate — it has no business behavior, just a record
/// of a completed invoice-creation request keyed by the caller's <c>Idempotency-Key</c>.
///
/// Single string <c>Key</c> primary key rather than an (Operation, Key) composite: invoice
/// creation is the only idempotent operation in this POC, so a scope column would carry no
/// information today. Add an Operation column if/when a second idempotent operation exists.
///
/// <c>ProjectId</c> is the request fingerprint (the only input that varies invoice creation);
/// a replay with the same key but a different <c>ProjectId</c> is a conflict, not a replay —
/// see <see cref="Application.InvoiceService"/>.
/// </summary>
public sealed class IdempotencyRecord
{
    public string Key { get; private set; } = string.Empty;
    public Guid ProjectId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private IdempotencyRecord()
    {
    }

    public static IdempotencyRecord Create(string key, Guid projectId, Guid invoiceId, DateTimeOffset createdAt)
        => new()
        {
            Key = key,
            ProjectId = projectId,
            InvoiceId = invoiceId,
            CreatedAt = createdAt,
        };
}
