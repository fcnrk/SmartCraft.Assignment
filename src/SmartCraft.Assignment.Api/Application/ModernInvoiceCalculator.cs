namespace SmartCraft.Assignment.Api.Application;

/// <summary>
/// Modern C# invoice-line calculation (docs/01-domain.md rules 13-15, "overtime allocation").
/// Pure: no DB access, no side effects, no ordering/consumption state outside its own call —
/// see <see cref="IInvoiceCalculator"/> for why this is behind an interface.
/// </summary>
public sealed class ModernInvoiceCalculator : IInvoiceCalculator
{
    private const decimal NormalCapacityPerDay = 8m;
    private const decimal OvertimeRateMultiplier = 1.5m;

    public IReadOnlyList<CalculatedInvoiceLine> Calculate(
        IReadOnlyList<WorklogBillingInput> worklogs,
        IReadOnlyDictionary<(Guid WorkerId, DateOnly WorkDate), decimal> priorNormalHoursByWorkerDate)
    {
        var lines = new List<CalculatedInvoiceLine>(worklogs.Count);
        var remainingNormalCapacity = new Dictionary<(Guid WorkerId, DateOnly WorkDate), decimal>();

        // POC policy decision (docs/01-domain.md "overtime allocation"): deterministic
        // allocation order is (WorkDate, WorklogId). ponytail: WorklogId is an arbitrary-but-
        // stable tie-breaker, not legacy's real chronological order — Worklog has no creation
        // timestamp today. A real migration must recover/verify legacy's actual ordering rule
        // before this can be trusted as behaviorally equivalent.
        foreach (var worklog in worklogs.OrderBy(w => w.WorkDate).ThenBy(w => w.WorklogId))
        {
            var key = (worklog.WorkerId, worklog.WorkDate);
            if (!remainingNormalCapacity.TryGetValue(key, out var remaining))
            {
                var priorNormalHours = priorNormalHoursByWorkerDate.GetValueOrDefault(key);
                remaining = Math.Max(NormalCapacityPerDay - priorNormalHours, 0m);
            }

            var normalHours = Math.Min(worklog.Hours, remaining);
            var overtimeHours = worklog.Hours - normalHours;
            remainingNormalCapacity[key] = remaining - normalHours;

            // Rounding policy (docs/01-domain.md rule 15): each amount rounded independently,
            // AwayFromZero, then summed — decimal throughout, never double.
            var normalAmount = Math.Round(normalHours * worklog.HourlyRate, 2, MidpointRounding.AwayFromZero);
            var overtimeAmount = Math.Round(overtimeHours * worklog.HourlyRate * OvertimeRateMultiplier, 2, MidpointRounding.AwayFromZero);

            lines.Add(new CalculatedInvoiceLine(
                worklog.WorklogId,
                worklog.WorkerId,
                worklog.WorkDate,
                worklog.WorkerRole,
                worklog.HourlyRate,
                normalHours,
                overtimeHours,
                OvertimeRateMultiplier,
                normalAmount,
                overtimeAmount,
                normalAmount + overtimeAmount));
        }

        return lines;
    }
}
