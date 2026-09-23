using SmartCraft.Assignment.Api.Application;

namespace SmartCraft.Assignment.Api.Infrastructure;

/// <summary>
/// Legacy side of the modernization seam (docs/02-architecture.md "Legacy modernization seam",
/// docs/05-testing-and-differential.md). In production this adapter would execute the legacy
/// stored procedure, e.g. <c>EXEC dbo.usp_CalculateInvoiceLines @Worklogs</c> (a table-valued
/// parameter built from <see cref="WorklogBillingInput"/>), and map its result set 1:1 onto
/// <see cref="CalculatedInvoiceLine"/>. No legacy database exists for this POC, so the
/// procedure body is SIMULATED below: a row-by-row port of what the procedure is assumed to
/// do. Fabricated legacy behavior is not evidence of real compatibility — it demonstrates
/// where a real procedure plugs in and how differences surface.
///
/// Simulated legacy quirk (deliberate, so the harness has a real difference to classify):
/// the procedure only tracks the 8h/day normal-time ceiling within the current batch. It
/// ignores normal hours already consumed by earlier invoices (<c>priorNormalHoursByWorkerDate</c>),
/// so a worker/day split across two invoice runs can be billed as more than 8 normal hours.
///
/// Not registered in DI: production invoices use <see cref="ModernInvoiceCalculator"/>; this
/// runs only in the differential harness (tests/.../Differential).
/// </summary>
public sealed class LegacyInvoiceCalculator : IInvoiceCalculator
{
    public IReadOnlyList<CalculatedInvoiceLine> Calculate(
        IReadOnlyList<WorklogBillingInput> worklogs,
        IReadOnlyDictionary<(Guid WorkerId, DateOnly WorkDate), decimal> priorNormalHoursByWorkerDate)
    {
        // DECLARE cur CURSOR FOR SELECT ... ORDER BY WorkerId, WorkDate, WorklogId
        var dayTotals = new Dictionary<(Guid, DateOnly), decimal>(); // #DayTotals temp table
        var result = new List<CalculatedInvoiceLine>();

        foreach (var w in worklogs.OrderBy(w => w.WorkerId).ThenBy(w => w.WorkDate).ThenBy(w => w.WorklogId))
        {
            var before = dayTotals.GetValueOrDefault((w.WorkerId, w.WorkDate)); // quirk: never seeded from prior invoices
            var after = before + w.Hours;
            dayTotals[(w.WorkerId, w.WorkDate)] = after;

            // IF @after <= 8 ... ELSE IF @before >= 8 ... ELSE (split)
            var overtime = after <= 8m ? 0m : before >= 8m ? w.Hours : after - 8m;
            var normal = w.Hours - overtime;

            // ROUND(x, 2) on DECIMAL rounds half away from zero.
            var normalAmount = Math.Round(normal * w.HourlyRate, 2, MidpointRounding.AwayFromZero);
            var overtimeAmount = Math.Round(overtime * w.HourlyRate * 1.5m, 2, MidpointRounding.AwayFromZero);

            result.Add(new CalculatedInvoiceLine(
                w.WorklogId, w.WorkerId, w.WorkDate, w.WorkerRole, w.HourlyRate,
                normal, overtime, 1.5m, normalAmount, overtimeAmount, normalAmount + overtimeAmount));
        }

        return result;
    }
}
