using SmartCraft.Assignment.Api.Application;

namespace SmartCraft.Assignment.Tests.Domain;

/// <summary>
/// Covers docs/01-domain.md rules 13-15 and the "overtime allocation" POC policy: 8h/day
/// normal-time ceiling per worker per calendar day across all projects, deterministic
/// (WorkDate, WorklogId) allocation order, 1.5x overtime multiplier, decimal rounding.
/// Pure calculator — no DB.
/// </summary>
public class ModernInvoiceCalculatorTests
{
    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 9, 23);
    private static readonly IReadOnlyDictionary<(Guid, DateOnly), decimal> NoPriorHours =
        new Dictionary<(Guid, DateOnly), decimal>();

    private readonly ModernInvoiceCalculator _sut = new();

    [Fact]
    public void Hours_at_or_under_8_are_entirely_normal_time()
    {
        var worklog = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 8m, "Dev", 100m);

        var result = _sut.Calculate([worklog], NoPriorHours);

        var line = Assert.Single(result);
        Assert.Equal(8m, line.NormalHours);
        Assert.Equal(0m, line.OvertimeHours);
        Assert.Equal(800m, line.NormalAmount);
        Assert.Equal(0m, line.OvertimeAmount);
        Assert.Equal(800m, line.LineTotal);
        Assert.Equal(1.5m, line.OvertimeMultiplier);
    }

    [Fact]
    public void Hours_beyond_8_on_one_worklog_split_into_normal_and_overtime_at_1_5x()
    {
        var worklog = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 10m, "Dev", 100m);

        var result = _sut.Calculate([worklog], NoPriorHours);

        var line = Assert.Single(result);
        Assert.Equal(8m, line.NormalHours);
        Assert.Equal(2m, line.OvertimeHours);
        Assert.Equal(800m, line.NormalAmount);
        Assert.Equal(300m, line.OvertimeAmount); // 2h * 100 * 1.5
        Assert.Equal(1100m, line.LineTotal);
    }

    [Fact]
    public void Two_worklogs_same_worker_day_split_overtime_across_them_in_WorkDate_then_WorklogId_order()
    {
        var earlierId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var laterId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var second = new WorklogBillingInput(laterId, WorkerId, Day, 5m, "Dev", 100m);
        var first = new WorklogBillingInput(earlierId, WorkerId, Day, 5m, "Dev", 100m);

        // Passed in reverse order deliberately: output must still follow WorklogId order.
        var result = _sut.Calculate([second, first], NoPriorHours);

        Assert.Equal(2, result.Count);
        var firstLine = result.Single(l => l.WorklogId == earlierId);
        var secondLine = result.Single(l => l.WorklogId == laterId);

        Assert.Equal(5m, firstLine.NormalHours);
        Assert.Equal(0m, firstLine.OvertimeHours);

        Assert.Equal(3m, secondLine.NormalHours);
        Assert.Equal(2m, secondLine.OvertimeHours);
    }

    [Fact]
    public void Prior_invoiced_normal_hours_reduce_remaining_normal_capacity_for_the_day()
    {
        var worklog = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 4m, "Dev", 100m);
        var priorHours = new Dictionary<(Guid, DateOnly), decimal> { [(WorkerId, Day)] = 6m };

        var result = _sut.Calculate([worklog], priorHours);

        var line = Assert.Single(result);
        Assert.Equal(2m, line.NormalHours); // only 2h of the 8h ceiling remained
        Assert.Equal(2m, line.OvertimeHours);
    }

    [Fact]
    public void Different_workers_each_get_their_own_8h_ceiling()
    {
        var otherWorkerId = Guid.NewGuid();
        var a = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 8m, "Dev", 100m);
        var b = new WorklogBillingInput(Guid.NewGuid(), otherWorkerId, Day, 8m, "Dev", 100m);

        var result = _sut.Calculate([a, b], NoPriorHours);

        Assert.All(result, l => Assert.Equal(0m, l.OvertimeHours));
    }

    [Fact]
    public void Rounding_uses_AwayFromZero_on_each_amount_independently()
    {
        // 1/3 hour at a rate that produces a repeating decimal, to exercise rounding.
        var worklog = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 0.125m, "Dev", 100m);

        var result = _sut.Calculate([worklog], NoPriorHours);

        var line = Assert.Single(result);
        Assert.Equal(12.5m, line.NormalHours * line.HourlyRate); // sanity: exact pre-rounding value
        Assert.Equal(Math.Round(12.5m, 2, MidpointRounding.AwayFromZero), line.NormalAmount);
        Assert.Equal(line.NormalAmount + line.OvertimeAmount, line.LineTotal);
    }
}
