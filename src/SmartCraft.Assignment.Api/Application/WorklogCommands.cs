namespace SmartCraft.Assignment.Api.Application;

/// <summary>Command inputs for WorklogService. Plain records, not the Worklog entity
/// (docs/03-api-transactions.md: "Do not make API DTOs the domain model").</summary>
public sealed record CreateWorklogCommand(Guid WorkerId, Guid ProjectId, DateOnly WorkDate, decimal Hours, string? Description);

/// <summary>ExpectedVersion is the optimistic-concurrency contract: the caller states
/// which version it last read, and the command fails with a Conflict if that's stale.</summary>
public sealed record UpdateWorklogCommand(Guid WorklogId, long ExpectedVersion, DateOnly WorkDate, decimal Hours, string? Description);

public sealed record SubmitWorklogCommand(Guid WorklogId, long ExpectedVersion);

public sealed record ApproveWorklogCommand(Guid WorklogId, long ExpectedVersion);
