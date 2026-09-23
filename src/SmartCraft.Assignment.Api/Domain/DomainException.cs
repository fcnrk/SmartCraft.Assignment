namespace SmartCraft.Assignment.Api.Domain;

/// <summary>
/// Distinguishes invalid-input/business-rule failures (map to HTTP 400), missing
/// resources (map to HTTP 404), and invalid lifecycle/concurrency conflicts (map
/// to HTTP 409) at the API boundary. Lifecycle conflicts and optimistic-concurrency
/// conflicts share <see cref="Conflict"/>: the API maps both to 409 and the message
/// text is what distinguishes them for the caller, so a separate kind would add a
/// distinction nothing currently consumes.
/// </summary>
public enum DomainErrorKind
{
    Validation,
    NotFound,
    Conflict,
}

/// <summary>
/// Raised by domain operations. <see cref="Kind"/> lets callers (the API layer)
/// choose the correct HTTP status without parsing exception types.
/// </summary>
public sealed class DomainException(DomainErrorKind kind, string message) : Exception(message)
{
    public DomainErrorKind Kind { get; } = kind;
}
