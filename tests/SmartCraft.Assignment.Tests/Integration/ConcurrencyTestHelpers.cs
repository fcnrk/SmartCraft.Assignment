using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>Runs a WorklogService call and reports success/failure instead of letting a
/// DomainException propagate. Used by concurrency tests where several simultaneous attempts
/// race and exactly one is expected to win — the test needs to assert on all of them, not
/// just catch the first exception.</summary>
internal static class ConcurrencyTestHelpers
{
    internal readonly record struct AttemptResult(bool Success, DomainErrorKind? FailureKind, Worklog? Worklog);

    internal static async Task<AttemptResult> AttemptAsync(Func<Task<Worklog>> action)
    {
        try
        {
            var worklog = await action();
            return new AttemptResult(true, null, worklog);
        }
        catch (DomainException ex)
        {
            return new AttemptResult(false, ex.Kind, null);
        }
    }
}
