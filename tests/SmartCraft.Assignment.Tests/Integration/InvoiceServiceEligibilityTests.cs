using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// Gaps not covered by <c>InvoiceServiceTests</c>: eligibility filtering by lifecycle status
/// beyond Draft/other-project (rule 11), project existence, invoice re-reading via
/// GetInvoiceAsync from a fresh context, and rule 17 ("later assignment-rate changes cannot
/// rewrite historical invoices").
/// </summary>
public class InvoiceServiceEligibilityTests
{
    private static readonly DateOnly Day = new(2026, 9, 23);

    [Fact]
    public async Task Submitted_but_not_yet_approved_worklogs_are_not_eligible()
    {
        using var fx = new SqliteWorklogFixture();
        var worklogService = fx.NewService();

        var submittedOnly = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 3m, "submitted"));
        await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(submittedOnly.Id, submittedOnly.Version));

        var invoiceService = fx.NewInvoiceService();
        var ex = await Assert.ThrowsAsync<DomainException>(() => invoiceService.CreateInvoiceAsync(fx.ProjectId, "key-1"));
        Assert.Equal(DomainErrorKind.Validation, ex.Kind);

        using var freshDb = fx.CreateContext();
        var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == submittedOnly.Id);
        Assert.Equal(WorklogStatus.Submitted, reloaded.Status);
        Assert.Null(reloaded.InvoiceId);
    }

    [Fact]
    public async Task Creating_an_invoice_for_a_nonexistent_project_fails_NotFound()
    {
        using var fx = new SqliteWorklogFixture();
        var invoiceService = fx.NewInvoiceService();

        var ex = await Assert.ThrowsAsync<DomainException>(() => invoiceService.CreateInvoiceAsync(Guid.NewGuid(), "key-1"));

        Assert.Equal(DomainErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Second_invoice_attempt_with_nothing_new_persists_no_additional_invoice_row()
    {
        using var fx = new SqliteWorklogFixture();
        var worklogService = fx.NewService();
        var created = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 8m, "work"));
        var submitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version));
        await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(created.Id, submitted.Version));

        var invoiceService = fx.NewInvoiceService();
        await invoiceService.CreateInvoiceAsync(fx.ProjectId, "key-1");

        // A different key: a genuinely new client request, not a retry, so this must hit the
        // business rule (nothing left to invoice) rather than replay the first invoice.
        await Assert.ThrowsAsync<DomainException>(() => invoiceService.CreateInvoiceAsync(fx.ProjectId, "key-2"));

        using var freshDb = fx.CreateContext();
        Assert.Equal(1, await freshDb.Invoices.CountAsync());
    }

    [Fact]
    public async Task Created_invoice_is_reloadable_via_GetInvoiceAsync_from_a_fresh_context_with_equal_values()
    {
        using var fx = new SqliteWorklogFixture();
        var worklogService = fx.NewService();
        var created = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 9m, "work"));
        var submitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version));
        await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(created.Id, submitted.Version));

        var invoiceService = fx.NewInvoiceService();
        var original = (await invoiceService.CreateInvoiceAsync(fx.ProjectId, "key-1")).Invoice;

        // Reload through a brand-new InvoiceService/AppDbContext/connection, like a separate
        // request hitting a different API instance.
        var reloadingService = fx.NewInvoiceService();
        var reloaded = await reloadingService.GetInvoiceAsync(original.Id);

        Assert.Equal(original.Id, reloaded.Id);
        Assert.Equal(original.ProjectId, reloaded.ProjectId);
        Assert.Equal(original.Total, reloaded.Total);
        Assert.Equal(original.Lines.Count, reloaded.Lines.Count);

        var originalLine = Assert.Single(original.Lines);
        var reloadedLine = Assert.Single(reloaded.Lines);
        Assert.Equal(originalLine.WorklogId, reloadedLine.WorklogId);
        Assert.Equal(originalLine.WorkerId, reloadedLine.WorkerId);
        Assert.Equal(originalLine.WorkDate, reloadedLine.WorkDate);
        Assert.Equal(originalLine.WorkerRole, reloadedLine.WorkerRole);
        Assert.Equal(originalLine.HourlyRate, reloadedLine.HourlyRate);
        Assert.Equal(originalLine.NormalHours, reloadedLine.NormalHours);
        Assert.Equal(originalLine.OvertimeHours, reloadedLine.OvertimeHours);
        Assert.Equal(originalLine.OvertimeMultiplier, reloadedLine.OvertimeMultiplier);
        Assert.Equal(originalLine.NormalAmount, reloadedLine.NormalAmount);
        Assert.Equal(originalLine.OvertimeAmount, reloadedLine.OvertimeAmount);
        Assert.Equal(originalLine.LineTotal, reloadedLine.LineTotal);
    }

    [Fact]
    public async Task Invoice_line_snapshot_is_unaffected_by_a_later_change_to_the_assignments_hourly_rate()
    {
        using var fx = new SqliteWorklogFixture();
        var worklogService = fx.NewService();
        var created = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 5m, "work"));
        var submitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version));
        await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(created.Id, submitted.Version));

        var invoiceService = fx.NewInvoiceService();
        var invoice = (await invoiceService.CreateInvoiceAsync(fx.ProjectId, "key-1")).Invoice;
        var originalRate = Assert.Single(invoice.Lines).HourlyRate;
        Assert.Equal(100m, originalRate);

        // Rule 17: rates/calculation inputs are snapshotted so later assignment-rate changes
        // cannot rewrite historical invoices. ProjectAssignment exposes no rate-change API
        // (docs/01-domain.md: it's set once via Project.AssignWorker), so mutating it here goes
        // directly through raw SQL against the owned ProjectAssignments table -- deliberately
        // bypassing the domain model to simulate "the current, mutable rate changed" without
        // needing a rate-change use case that doesn't exist yet.
        using (var mutateDb = fx.CreateContext())
        {
            var rowsChanged = await mutateDb.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE ProjectAssignments SET HourlyRate = 999 WHERE ProjectId = {fx.ProjectId} AND WorkerId = {fx.WorkerId}");
            Assert.Equal(1, rowsChanged);
        }

        var reloadingService = fx.NewInvoiceService();
        var reloaded = await reloadingService.GetInvoiceAsync(invoice.Id);
        var reloadedLine = Assert.Single(reloaded.Lines);

        Assert.Equal(originalRate, reloadedLine.HourlyRate);
        Assert.Equal(100m, reloadedLine.HourlyRate);
        Assert.NotEqual(999m, reloadedLine.HourlyRate);
    }
}
