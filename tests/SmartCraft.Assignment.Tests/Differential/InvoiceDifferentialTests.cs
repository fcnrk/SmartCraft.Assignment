using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Infrastructure;
using SmartCraft.Assignment.Tests.Integration;
using Xunit.Abstractions;

namespace SmartCraft.Assignment.Tests.Differential;

/// <summary>
/// docs/05-testing-and-differential.md "Differential invoice tests": the same fixture goes
/// through the legacy and modern calculators, both results are normalized, and every
/// field-level difference must match an explicitly classified expectation. An unexpected
/// difference fails with readable diagnostics. An expected one that disappears also fails,
/// so a classification can't go stale silently. The comparator itself never ignores fields.
/// </summary>
public class InvoiceDifferentialTests(ITestOutputHelper output)
{
    private static readonly Guid Ann = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Ben = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly DateOnly Mon = new(2026, 9, 21);
    private static readonly DateOnly Tue = new(2026, 9, 22);

    public enum Classification { ModernRegression, IntentionalChange, LegacyDefect, NormalizationIssue }

    public sealed record Difference(Guid? WorklogId, string Field, string Legacy, string Modern)
    {
        public override string ToString() => $"Worklog: {WorklogId?.ToString() ?? "(invoice)"}  Field: {Field}  Legacy: {Legacy}  Modern: {Modern}";
    }

    public sealed record ExpectedDifference(Guid? WorklogId, string Field, Classification Classification, string Reason);

    public sealed record Fixture(
        string Name,
        WorklogBillingInput[] Worklogs,
        Dictionary<(Guid WorkerId, DateOnly WorkDate), decimal> PriorNormalHours,
        ExpectedDifference[] Expected)
    {
        public override string ToString() => Name;
    }

    private static WorklogBillingInput Wl(int n, Guid worker, DateOnly day, decimal hours, decimal rate = 100m, string role = "Developer") =>
        new(Guid.Parse($"bbbbbbbb-0000-0000-0000-{n:D12}"), worker, day, hours, role, rate);

    private static Guid WlId(int n) => Guid.Parse($"bbbbbbbb-0000-0000-0000-{n:D12}");

    public static TheoryData<Fixture> Fixtures => new()
    {
        new("under 8h, two workers", [Wl(1, Ann, Mon, 6m), Wl(2, Ben, Mon, 7.5m, 80m, "Tester")], new(), []),
        new("exactly 8h boundary", [Wl(1, Ann, Mon, 8m)], new(), []),
        new("overtime split across worklogs same day", [Wl(1, Ann, Mon, 5m), Wl(2, Ann, Mon, 4m), Wl(3, Ann, Mon, 2m)], new(), []),
        new("same worker, separate days", [Wl(1, Ann, Mon, 10m), Wl(2, Ann, Tue, 3m)], new(), []),
        new("fractional rate rounding (half away from zero)", [Wl(1, Ann, Mon, 0.5m, 20.25m), Wl(2, Ann, Mon, 9m, 20.25m)], new(), []),
        new("normal time already consumed by an earlier invoice",
            [Wl(1, Ann, Mon, 4m)],
            new() { [(Ann, Mon)] = 6m },
            [
                new(WlId(1), "NormalHours", Classification.LegacyDefect, "Rule 13: 8h/day ceiling spans invoices; legacy only counts the current batch."),
                new(WlId(1), "OvertimeHours", Classification.LegacyDefect, "Same root cause."),
                new(WlId(1), "NormalAmount", Classification.LegacyDefect, "Same root cause."),
                new(WlId(1), "OvertimeAmount", Classification.LegacyDefect, "Same root cause."),
                new(WlId(1), "LineTotal", Classification.LegacyDefect, "Same root cause."),
                new(null, "Total", Classification.LegacyDefect, "Same root cause."),
            ]),
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Legacy_and_modern_invoices_differ_only_where_classified(Fixture fixture)
    {
        var legacy = new LegacyInvoiceCalculator().Calculate(fixture.Worklogs, fixture.PriorNormalHours);
        var modern = new ModernInvoiceCalculator().Calculate(fixture.Worklogs, fixture.PriorNormalHours);

        var differences = Compare(legacy, modern);
        foreach (var d in differences) output.WriteLine(d.ToString());

        var expected = fixture.Expected.Select(e => (e.WorklogId, e.Field)).ToHashSet();
        var actual = differences.Select(d => (d.WorklogId, d.Field)).ToHashSet();

        var unexpected = differences.Where(d => !expected.Contains((d.WorklogId, d.Field))).ToList();
        Assert.True(unexpected.Count == 0, "Unclassified invoice differences:\n" + string.Join("\n", unexpected));
        var vanished = fixture.Expected.Where(e => !actual.Contains((e.WorklogId, e.Field))).ToList();
        Assert.True(vanished.Count == 0, "Classified differences no longer occur (re-check classification):\n" + string.Join("\n", vanished));
    }

    [Fact]
    public void Comparator_reports_path_and_values_for_a_mismatch()
    {
        var line = new ModernInvoiceCalculator().Calculate([Wl(1, Ann, Mon, 10m)], new Dictionary<(Guid, DateOnly), decimal>())[0];

        var differences = Compare([line with { OvertimeHours = 0m }], [line]);

        Assert.Contains(differences, d => d is { Field: "OvertimeHours", Legacy: "0", Modern: "2" } && d.WorklogId == WlId(1));
        Assert.Contains("Field: OvertimeHours  Legacy: 0  Modern: 2", string.Join("\n", differences));
    }

    [Fact]
    public async Task Legacy_adapter_plugs_into_InvoiceService_through_the_same_seam()
    {
        using var fx = new SqliteWorklogFixture();
        var worklogs = fx.NewService();
        var created = await worklogs.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Mon, 9m, "work"));
        var submitted = await worklogs.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version));
        await worklogs.ApproveWorklogAsync(new ApproveWorklogCommand(created.Id, submitted.Version));

        var service = new InvoiceService(fx.CreateContext(), new LegacyInvoiceCalculator(), TimeProvider.System);
        var invoice = (await service.CreateInvoiceAsync(fx.ProjectId, "legacy-1")).Invoice;

        Assert.Equal(950m, invoice.Total); // 8 x 100 + 1 x 150, passes InvoiceService's output validation
    }

    /// <summary>Normalized comparison (docs/05 "Normalized comparison model"): lines keyed by
    /// WorklogId (consumed-worklog sets compared too), every business field compared by decimal
    /// value, so 1.5 and 1.50 are equal while any real value change is reported. There are no
    /// generated ids or timestamps at this seam, so there is nothing else to normalize away.</summary>
    internal static List<Difference> Compare(IReadOnlyList<CalculatedInvoiceLine> legacy, IReadOnlyList<CalculatedInvoiceLine> modern)
    {
        var differences = new List<Difference>();
        var legacyById = legacy.ToDictionary(l => l.WorklogId);
        var modernById = modern.ToDictionary(l => l.WorklogId);

        foreach (var id in legacyById.Keys.Union(modernById.Keys).Order())
        {
            if (!legacyById.TryGetValue(id, out var l) || !modernById.TryGetValue(id, out var m))
            {
                differences.Add(new(id, "ConsumedWorklog", legacyById.ContainsKey(id) ? "present" : "missing", modernById.ContainsKey(id) ? "present" : "missing"));
                continue;
            }

            void Check<T>(string field, T legacyValue, T modernValue)
            {
                if (!EqualityComparer<T>.Default.Equals(legacyValue, modernValue))
                    differences.Add(new(id, field, $"{legacyValue}", $"{modernValue}"));
            }

            Check("WorkerId", l.WorkerId, m.WorkerId);
            Check("WorkDate", l.WorkDate, m.WorkDate);
            Check("WorkerRole", l.WorkerRole, m.WorkerRole);
            Check("HourlyRate", l.HourlyRate, m.HourlyRate);
            Check("NormalHours", l.NormalHours, m.NormalHours);
            Check("OvertimeHours", l.OvertimeHours, m.OvertimeHours);
            Check("OvertimeMultiplier", l.OvertimeMultiplier, m.OvertimeMultiplier);
            Check("NormalAmount", l.NormalAmount, m.NormalAmount);
            Check("OvertimeAmount", l.OvertimeAmount, m.OvertimeAmount);
            Check("LineTotal", l.LineTotal, m.LineTotal);
        }

        var legacyTotal = legacy.Sum(l => l.LineTotal);
        var modernTotal = modern.Sum(l => l.LineTotal);
        if (legacyTotal != modernTotal) differences.Add(new(null, "Total", $"{legacyTotal}", $"{modernTotal}"));

        return differences;
    }
}
