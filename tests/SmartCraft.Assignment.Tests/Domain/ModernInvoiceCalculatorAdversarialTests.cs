using SmartCraft.Assignment.Api.Application;

namespace SmartCraft.Assignment.Tests.Domain;

/// <summary>
/// Boundary/adversarial coverage for <see cref="ModernInvoiceCalculator"/> beyond
/// <c>ModernInvoiceCalculatorTests</c> (docs/01-domain.md rules 13-15, "overtime allocation"
/// POC decision): exact hour boundaries, many-worklog/multi-day allocation, prior-consumption
/// edge cases, rounding midpoints that distinguish AwayFromZero from banker's rounding, decimal
/// (never double) summation, and allocation-order independence from input order.
/// </summary>
public class ModernInvoiceCalculatorAdversarialTests
{
    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 9, 23);
    private static readonly IReadOnlyDictionary<(Guid, DateOnly), decimal> NoPriorHours =
        new Dictionary<(Guid, DateOnly), decimal>();

    private readonly ModernInvoiceCalculator _sut = new();

    [Fact]
    public void Exactly_8_hours_is_zero_overtime_and_8_01_hours_is_a_single_cent_of_overtime()
    {
        var atBoundary = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 8m, "Dev", 100m);
        var justOver = new WorklogBillingInput(Guid.NewGuid(), Guid.NewGuid(), Day, 8.01m, "Dev", 100m);

        var atResult = Assert.Single(_sut.Calculate([atBoundary], NoPriorHours));
        Assert.Equal(8m, atResult.NormalHours);
        Assert.Equal(0m, atResult.OvertimeHours);

        var overResult = Assert.Single(_sut.Calculate([justOver], NoPriorHours));
        Assert.Equal(8m, overResult.NormalHours);
        Assert.Equal(0.01m, overResult.OvertimeHours);
    }

    [Fact]
    public void Many_small_worklogs_the_same_day_consume_normal_capacity_in_allocation_order_then_overflow_to_overtime()
    {
        // 5 worklogs x 2h = 10h total for one worker/day: first 4 (8h) entirely normal,
        // the 5th entirely overtime. Ids assigned in allocation order so the intent is explicit.
        var ids = Enumerable.Range(1, 5)
            .Select(i => Guid.Parse($"00000000-0000-0000-0000-00000000000{i}"))
            .ToList();
        var worklogs = ids.Select(id => new WorklogBillingInput(id, WorkerId, Day, 2m, "Dev", 100m)).ToList();

        var result = _sut.Calculate(worklogs, NoPriorHours);

        for (var i = 0; i < 4; i++)
        {
            var line = result.Single(l => l.WorklogId == ids[i]);
            Assert.Equal(2m, line.NormalHours);
            Assert.Equal(0m, line.OvertimeHours);
        }

        var last = result.Single(l => l.WorklogId == ids[4]);
        Assert.Equal(0m, last.NormalHours);
        Assert.Equal(2m, last.OvertimeHours);
    }

    [Fact]
    public void A_24_hour_worklog_splits_into_8_normal_and_16_overtime()
    {
        var worklog = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 24m, "Dev", 10m);

        var line = Assert.Single(_sut.Calculate([worklog], NoPriorHours));

        Assert.Equal(8m, line.NormalHours);
        Assert.Equal(16m, line.OvertimeHours);
        Assert.Equal(80m, line.NormalAmount); // 8 * 10
        Assert.Equal(240m, line.OvertimeAmount); // 16 * 10 * 1.5
    }

    [Fact]
    public void Prior_normal_hours_already_at_the_8h_ceiling_makes_the_whole_worklog_overtime()
    {
        var worklog = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 3m, "Dev", 100m);
        var priorHours = new Dictionary<(Guid, DateOnly), decimal> { [(WorkerId, Day)] = 8m };

        var line = Assert.Single(_sut.Calculate([worklog], priorHours));

        Assert.Equal(0m, line.NormalHours);
        Assert.Equal(3m, line.OvertimeHours);
    }

    [Fact]
    public void Prior_normal_hours_beyond_the_8h_ceiling_clamp_remaining_capacity_to_zero_not_negative()
    {
        // Defensive: the calculator itself never produces >8 prior-normal-hours for a
        // worker/day, but the input dictionary is caller-supplied data, not something the
        // calculator controls — it must not let remaining capacity go negative (which would
        // otherwise let a worklog "borrow back" hours as normal on a later, unrelated call).
        var worklog = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 2m, "Dev", 100m);
        var priorHours = new Dictionary<(Guid, DateOnly), decimal> { [(WorkerId, Day)] = 10m };

        var line = Assert.Single(_sut.Calculate([worklog], priorHours));

        Assert.Equal(0m, line.NormalHours);
        Assert.Equal(2m, line.OvertimeHours);
    }

    [Fact]
    public void Fractional_prior_normal_hours_reduce_remaining_capacity_by_the_exact_fraction()
    {
        var worklog = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 1m, "Dev", 100m);
        var priorHours = new Dictionary<(Guid, DateOnly), decimal> { [(WorkerId, Day)] = 7.5m };

        var line = Assert.Single(_sut.Calculate([worklog], priorHours));

        Assert.Equal(0.5m, line.NormalHours);
        Assert.Equal(0.5m, line.OvertimeHours);
    }

    [Fact]
    public void Different_calendar_days_for_the_same_worker_each_get_their_own_independent_8h_ceiling()
    {
        var day1 = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 8m, "Dev", 100m);
        var day2 = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day.AddDays(1), 8m, "Dev", 100m);

        var result = _sut.Calculate([day1, day2], NoPriorHours);

        Assert.All(result, l => Assert.Equal(0m, l.OvertimeHours));
        Assert.All(result, l => Assert.Equal(8m, l.NormalHours));
    }

    [Fact]
    public void Normal_amount_midpoint_rounds_away_from_zero_not_to_even()
    {
        // 1h * 0.125 rate = 0.125, exactly midway between 0.12 and 0.13. AwayFromZero -> 0.13.
        // MidpointRounding.ToEven would give 0.12 (2 is even) -- this is the case that actually
        // distinguishes the two policies, unlike a midpoint that happens to coincide either way.
        var worklog = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 1m, "Dev", 0.125m);

        var line = Assert.Single(_sut.Calculate([worklog], NoPriorHours));

        Assert.Equal(0.13m, line.NormalAmount);
        Assert.NotEqual(0.12m, line.NormalAmount); // what ToEven would have produced
    }

    [Fact]
    public void Overtime_amount_midpoint_after_the_1_5x_multiplier_rounds_away_from_zero_not_to_even()
    {
        // 8.75h at rate 1: 8h normal (exact, 8.00), 0.75h overtime * 1 * 1.5 = 1.125, exactly
        // midway between 1.12 and 1.13. AwayFromZero -> 1.13; ToEven would give 1.12.
        var worklog = new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 8.75m, "Dev", 1m);

        var line = Assert.Single(_sut.Calculate([worklog], NoPriorHours));

        Assert.Equal(8.00m, line.NormalAmount);
        Assert.Equal(1.13m, line.OvertimeAmount);
        Assert.NotEqual(1.12m, line.OvertimeAmount); // what ToEven would have produced
    }

    [Fact]
    public void Sum_of_amounts_that_are_lossy_in_double_arithmetic_is_exact_under_decimal()
    {
        // 0.1 + 0.2 + 0.3 is the canonical case where IEEE-754 double summation does not equal
        // 0.6 exactly (it yields 0.6000000000000001). If any internal step used double instead
        // of decimal, this sum would not compare equal to 0.6m.
        var worklogs = new[]
        {
            new WorklogBillingInput(Guid.NewGuid(), WorkerId, Day, 0.1m, "Dev", 1m),
            new WorklogBillingInput(Guid.NewGuid(), Guid.NewGuid(), Day, 0.2m, "Dev", 1m),
            new WorklogBillingInput(Guid.NewGuid(), Guid.NewGuid(), Day, 0.3m, "Dev", 1m),
        };

        var result = _sut.Calculate(worklogs, NoPriorHours);

        var total = result.Sum(l => l.LineTotal);
        Assert.Equal(0.6m, total);
    }

    [Fact]
    public void Totals_are_the_sum_of_each_lines_already_rounded_amount_not_a_single_rounding_of_the_raw_aggregate()
    {
        // Three worklogs, each hitting a rounding midpoint (rule 15: "each amount rounded
        // independently, not the pre-rounded hourly components"). The sum of the three
        // per-line rounded amounts (0.42) must differ from both the raw unrounded sum (0.405)
        // and from rounding that raw sum once at the end (0.41) -- proving rounding happens
        // per line, not on the aggregate.
        var worklogs = new[]
        {
            new WorklogBillingInput(Guid.NewGuid(), Guid.NewGuid(), Day, 1m, "Dev", 0.125m), // raw 0.125 -> 0.13
            new WorklogBillingInput(Guid.NewGuid(), Guid.NewGuid(), Day, 1m, "Dev", 0.135m), // raw 0.135 -> 0.14
            new WorklogBillingInput(Guid.NewGuid(), Guid.NewGuid(), Day, 1m, "Dev", 0.145m), // raw 0.145 -> 0.15
        };

        var result = _sut.Calculate(worklogs, NoPriorHours);

        var rawUnroundedSum = worklogs.Sum(w => w.Hours * w.HourlyRate);
        var actualTotal = result.Sum(l => l.LineTotal);

        Assert.Equal(0.405m, rawUnroundedSum);
        Assert.Equal(0.42m, actualTotal); // 0.13 + 0.14 + 0.15
        Assert.NotEqual(rawUnroundedSum, actualTotal);
        Assert.NotEqual(Math.Round(rawUnroundedSum, 2, MidpointRounding.AwayFromZero), actualTotal); // 0.41 != 0.42
    }

    [Fact]
    public void Result_is_independent_of_the_order_worklogs_are_passed_in()
    {
        var ids = Enumerable.Range(1, 6)
            .Select(i => Guid.Parse($"00000000-0000-0000-0000-0000000000{i:D2}"))
            .ToList();
        var worklogs = ids
            .Select((id, i) => new WorklogBillingInput(id, WorkerId, Day, (i % 3) + 1m, "Dev", 50m))
            .ToList();

        var random = new Random(12345);
        var shuffled = worklogs.OrderBy(_ => random.Next()).ToList();
        Assert.NotEqual(worklogs.Select(w => w.WorklogId), shuffled.Select(w => w.WorklogId)); // sanity: actually shuffled

        var baseline = _sut.Calculate(worklogs, NoPriorHours).ToDictionary(l => l.WorklogId);
        var fromShuffled = _sut.Calculate(shuffled, NoPriorHours).ToDictionary(l => l.WorklogId);

        Assert.Equal(baseline.Keys.OrderBy(k => k), fromShuffled.Keys.OrderBy(k => k));
        foreach (var id in baseline.Keys)
        {
            Assert.Equal(baseline[id], fromShuffled[id]);
        }
    }
}
