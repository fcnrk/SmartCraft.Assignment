namespace SmartCraft.Assignment.Api.Domain;

/// <summary>
/// A historical snapshot of one worklog's billing calculation, owned by <see cref="Invoice"/>.
/// Every field is a snapshot of inputs/results at invoice-creation time (docs/01-domain.md
/// rule 17), so later ProjectAssignment rate changes cannot rewrite history. Immutable: no
/// public mutators — lines are only ever created once, by <see cref="Invoice.Create"/>.
/// </summary>
public sealed class InvoiceLine
{
    public Guid WorklogId { get; private set; }
    public Guid WorkerId { get; private set; }
    public DateOnly WorkDate { get; private set; }
    public string WorkerRole { get; private set; } = string.Empty;
    public decimal HourlyRate { get; private set; }
    public decimal NormalHours { get; private set; }
    public decimal OvertimeHours { get; private set; }
    public decimal OvertimeMultiplier { get; private set; }
    public decimal NormalAmount { get; private set; }
    public decimal OvertimeAmount { get; private set; }
    public decimal LineTotal { get; private set; }

    private InvoiceLine()
    {
    }

    /// <summary>Values are expected to already be calculated/validated (rounded amounts,
    /// consistent LineTotal) by an <see cref="IInvoiceCalculator"/> — this factory just
    /// snapshots them; it does not re-derive or re-round anything.</summary>
    internal static InvoiceLine Create(
        Guid worklogId,
        Guid workerId,
        DateOnly workDate,
        string workerRole,
        decimal hourlyRate,
        decimal normalHours,
        decimal overtimeHours,
        decimal overtimeMultiplier,
        decimal normalAmount,
        decimal overtimeAmount,
        decimal lineTotal)
        => new()
        {
            WorklogId = worklogId,
            WorkerId = workerId,
            WorkDate = workDate,
            WorkerRole = workerRole,
            HourlyRate = hourlyRate,
            NormalHours = normalHours,
            OvertimeHours = overtimeHours,
            OvertimeMultiplier = overtimeMultiplier,
            NormalAmount = normalAmount,
            OvertimeAmount = overtimeAmount,
            LineTotal = lineTotal,
        };
}
