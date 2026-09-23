namespace SmartCraft.Assignment.Api.Application;

/// <summary>
/// One approved worklog's billing inputs for invoice calculation: the worklog itself plus
/// the worker's role/rate from their ProjectAssignment on this project (docs/01-domain.md
/// rule 12 — rate comes from the assignment, not a global worker rate).
/// </summary>
public sealed record WorklogBillingInput(
    Guid WorklogId,
    Guid WorkerId,
    DateOnly WorkDate,
    decimal Hours,
    string WorkerRole,
    decimal HourlyRate);

/// <summary>
/// One calculated invoice line. The shape maps 1:1 onto <see cref="Domain.InvoiceLine"/>'s
/// snapshot fields, so a legacy adapter implementing <see cref="IInvoiceCalculator"/> against
/// the same input can be compared line-for-line by the differential harness
/// (docs/05-testing-and-differential.md) — not implemented yet, see that document.
/// </summary>
public sealed record CalculatedInvoiceLine(
    Guid WorklogId,
    Guid WorkerId,
    DateOnly WorkDate,
    string WorkerRole,
    decimal HourlyRate,
    decimal NormalHours,
    decimal OvertimeHours,
    decimal OvertimeMultiplier,
    decimal NormalAmount,
    decimal OvertimeAmount,
    decimal LineTotal);

/// <summary>
/// Legacy modernization seam (docs/02-architecture.md "Legacy modernization seam"): a pure,
/// DB-free contract for invoice-line calculation. Two implementations are the reason this is
/// an interface rather than a concrete method: <see cref="ModernInvoiceCalculator"/> now, and
/// a legacy stored-procedure adapter (Infrastructure — not implemented this iteration; the
/// differential-comparison harness that would exercise it is future work per
/// docs/05-testing-and-differential.md and docs/06-implementation-plan.md Phase 4) later, so
/// equivalent input can be run through both and normalized/compared.
/// </summary>
public interface IInvoiceCalculator
{
    /// <param name="worklogs">Eligible worklogs to calculate, each with its role/rate.</param>
    /// <param name="priorNormalHoursByWorkerDate">Normal hours already snapshotted on other,
    /// previously-created invoice lines for a (WorkerId, WorkDate) pair, across all projects —
    /// docs/01-domain.md "overtime allocation": the 8h/day normal-time ceiling is per worker
    /// per calendar day across all projects, and invoicing follows a
    /// "first-invoiced-consumes-normal-time" policy. A pair absent from this dictionary has
    /// consumed zero normal hours so far.</param>
    IReadOnlyList<CalculatedInvoiceLine> Calculate(
        IReadOnlyList<WorklogBillingInput> worklogs,
        IReadOnlyDictionary<(Guid WorkerId, DateOnly WorkDate), decimal> priorNormalHoursByWorkerDate);
}
