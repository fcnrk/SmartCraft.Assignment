using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// docs/04 "Idempotency" (replay / conflicting reuse / concurrent same key / failures record
/// nothing) and reviewer finding I1 (calculator output is validated before it is accepted).
/// </summary>
public class InvoiceServiceIdempotencyAndValidationTests
{
    private static readonly DateOnly Day = new(2026, 9, 23);

    private static async Task<Worklog> ApprovedWorklogAsync(SqliteWorklogFixture fx, Guid projectId, decimal hours = 8m)
    {
        var service = fx.NewService();
        var created = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, projectId, Day, hours, "work"));
        var submitted = await service.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version));
        return await service.ApproveWorklogAsync(new ApproveWorklogCommand(created.Id, submitted.Version));
    }

    [Fact]
    public async Task Same_key_and_project_replays_the_original_invoice_without_a_new_effect()
    {
        using var fx = new SqliteWorklogFixture();
        await ApprovedWorklogAsync(fx, fx.ProjectId);

        var first = await fx.NewInvoiceService().CreateInvoiceAsync(fx.ProjectId, "key-1");
        await ApprovedWorklogAsync(fx, fx.ProjectId, 2m); // new eligible work arrives before the retry
        var retry = await fx.NewInvoiceService().CreateInvoiceAsync(fx.ProjectId, "key-1");

        Assert.False(first.IsReplay);
        Assert.True(retry.IsReplay);
        Assert.Equal(first.Invoice.Id, retry.Invoice.Id);
        using var db = fx.CreateContext();
        Assert.Equal(1, await db.Invoices.CountAsync());
        Assert.Equal(1, await db.Worklogs.CountAsync(w => w.Status == WorklogStatus.Approved));
    }

    [Fact]
    public async Task Same_key_for_a_different_project_is_a_conflict()
    {
        using var fx = new SqliteWorklogFixture();
        await ApprovedWorklogAsync(fx, fx.ProjectId, 4m);
        await ApprovedWorklogAsync(fx, fx.SecondProjectId, 4m);
        await fx.NewInvoiceService().CreateInvoiceAsync(fx.ProjectId, "key-1");

        var ex = await Assert.ThrowsAsync<DomainException>(() => fx.NewInvoiceService().CreateInvoiceAsync(fx.SecondProjectId, "key-1"));

        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
        using var db = fx.CreateContext();
        Assert.Equal(1, await db.Invoices.CountAsync());
    }

    [Fact]
    public async Task Concurrent_same_key_requests_create_exactly_one_invoice()
    {
        using var fx = new SqliteWorklogFixture();
        await ApprovedWorklogAsync(fx, fx.ProjectId);

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => fx.NewInvoiceService().CreateInvoiceAsync(fx.ProjectId, "same-key"))));

        Assert.Single(results.Select(r => r.Invoice.Id).Distinct());
        Assert.Single(results, r => !r.IsReplay);
        using var db = fx.CreateContext();
        Assert.Equal(1, await db.Invoices.CountAsync());
        Assert.Equal(1, await db.IdempotencyRecords.CountAsync());
    }

    [Fact]
    public async Task A_failed_attempt_records_nothing_so_a_retry_with_the_same_key_runs_fresh()
    {
        using var fx = new SqliteWorklogFixture();

        await Assert.ThrowsAsync<DomainException>(() => fx.NewInvoiceService().CreateInvoiceAsync(fx.ProjectId, "key-1"));
        await ApprovedWorklogAsync(fx, fx.ProjectId);
        var retry = await fx.NewInvoiceService().CreateInvoiceAsync(fx.ProjectId, "key-1");

        Assert.False(retry.IsReplay);
        Assert.Single(retry.Invoice.Lines);
    }

    public static TheoryData<string> BadCalculatorOutputs => new() { "drop", "duplicate", "unknown", "hours", "total", "rate" };

    [Theory]
    [MemberData(nameof(BadCalculatorOutputs))]
    public async Task Inconsistent_calculator_output_is_rejected_and_nothing_is_persisted(string corruption)
    {
        using var fx = new SqliteWorklogFixture();
        await ApprovedWorklogAsync(fx, fx.ProjectId, 4m);
        var service = fx.NewService();
        var second = await service.CreateWorklogAsync(new CreateWorklogCommand(fx.OtherWorkerId, fx.ProjectId, Day, 3m, "work"));
        var submitted = await service.SubmitWorklogAsync(new SubmitWorklogCommand(second.Id, second.Version));
        await service.ApproveWorklogAsync(new ApproveWorklogCommand(second.Id, submitted.Version));

        var invoiceService = new InvoiceService(fx.CreateContext(), new CorruptingCalculator(corruption), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => invoiceService.CreateInvoiceAsync(fx.ProjectId, "key-1"));

        using var db = fx.CreateContext();
        Assert.Equal(0, await db.Invoices.CountAsync());
        Assert.Equal(0, await db.IdempotencyRecords.CountAsync());
        Assert.Equal(2, await db.Worklogs.CountAsync(w => w.Status == WorklogStatus.Approved && w.InvoiceId == null));
    }

    /// <summary>Stands in for a faulty legacy adapter: real calculation, then one corruption.</summary>
    private sealed class CorruptingCalculator(string corruption) : IInvoiceCalculator
    {
        public IReadOnlyList<CalculatedInvoiceLine> Calculate(
            IReadOnlyList<WorklogBillingInput> worklogs,
            IReadOnlyDictionary<(Guid WorkerId, DateOnly WorkDate), decimal> priorNormalHoursByWorkerDate)
        {
            var lines = new ModernInvoiceCalculator().Calculate(worklogs, priorNormalHoursByWorkerDate).ToList();
            var l = lines[0];
            switch (corruption)
            {
                case "drop": lines.RemoveAt(0); break;
                case "duplicate": lines[1] = l; break;
                case "unknown": lines[0] = l with { WorklogId = Guid.NewGuid() }; break;
                case "hours": lines[0] = l with { NormalHours = l.NormalHours - 1m }; break;
                case "total": lines[0] = l with { LineTotal = l.LineTotal + 0.01m }; break;
                case "rate": lines[0] = l with { HourlyRate = l.HourlyRate + 1m }; break;
            }
            return lines;
        }
    }
}
