namespace SmartCraft.Assignment.Api.Authorization;

/// <summary>
/// Capability policies (docs/04-concurrency-idempotency-auth.md "Authorization"): one
/// authorization policy per permission string, named after the permission itself. Each caller
/// presents zero or more <c>permission</c> claims in their JWT (see Endpoints/DevTokenEndpoints
/// for how a POC token is minted); a policy requiring "worklog:create" is satisfied by a
/// "permission" claim with that exact value. Registered via a loop in Program.cs, not
/// copy-pasted per policy.
///
/// Permission checks establish *whether the caller may call this endpoint at all*. Resource
/// ownership (e.g. "only your own worklog") is a separate, per-request check made after loading
/// the resource — see WorklogEndpoints.
/// </summary>
public static class Permissions
{
    public const string ClaimType = "permission";

    public const string WorklogCreate = "worklog:create";
    public const string WorklogUpdateOwn = "worklog:update-own";
    public const string WorklogSubmitOwn = "worklog:submit-own";
    public const string WorklogApprove = "worklog:approve";
    public const string InvoiceCreate = "invoice:create";
    public const string InvoiceRead = "invoice:read";
    public const string ProjectRead = "project:read";

    public static readonly IReadOnlyList<string> All =
    [
        WorklogCreate, WorklogUpdateOwn, WorklogSubmitOwn, WorklogApprove, InvoiceCreate, InvoiceRead, ProjectRead,
    ];
}
