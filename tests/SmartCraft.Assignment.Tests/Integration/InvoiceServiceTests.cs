using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// docs/03-api-transactions.md "Create invoice" and docs/04 "competing invoice requests":
/// InvoiceService.CreateInvoiceAsync's transactional claim of approved worklogs, and the
/// rule-9 double-invoicing guard.
/// </summary>
public class InvoiceServiceTests
{
    private static readonly DateOnly Day = new(2026, 9, 23);

    [Fact]
    public async Task Creating_an_invoice_snapshots_role_rate_and_marks_worklogs_Invoiced()
    {
        using var fx = new SqliteWorklogFixture();
        var worklogService = fx.NewService();
        var created = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 8m, "work"));
        var submitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version));
        await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(created.Id, submitted.Version));

        var invoiceService = fx.NewInvoiceService();
        var invoice = await invoiceService.CreateInvoiceAsync(fx.ProjectId);

        var line = Assert.Single(invoice.Lines);
        Assert.Equal(created.Id, line.WorklogId);
        Assert.Equal("Developer", line.WorkerRole);
        Assert.Equal(100m, line.HourlyRate);
        Assert.Equal(8m, line.NormalHours);
        Assert.Equal(0m, line.OvertimeHours);
        Assert.Equal(800m, invoice.Total);

        using var freshDb = fx.CreateContext();
        var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == created.Id);
        Assert.Equal(WorklogStatus.Invoiced, reloaded.Status);
        Assert.Equal(invoice.Id, reloaded.InvoiceId);
    }

    [Fact]
    public async Task Creating_an_invoice_with_no_eligible_worklogs_fails_validation()
    {
        using var fx = new SqliteWorklogFixture();
        var invoiceService = fx.NewInvoiceService();

        var ex = await Assert.ThrowsAsync<DomainException>(() => invoiceService.CreateInvoiceAsync(fx.ProjectId));

        Assert.Equal(DomainErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Only_Approved_uninvoiced_worklogs_for_the_requested_project_are_claimed()
    {
        using var fx = new SqliteWorklogFixture();
        var worklogService = fx.NewService();

        var draft = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 2m, "draft"));

        var approved = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, new DateOnly(2026, 9, 24), 3m, "approved"));
        var approvedSubmitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(approved.Id, approved.Version));
        await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(approved.Id, approvedSubmitted.Version));

        var otherProjectApproved = await worklogService.CreateWorklogAsync(
            new CreateWorklogCommand(fx.WorkerId, fx.SecondProjectId, new DateOnly(2026, 9, 25), 4m, "other project"));
        var otherSubmitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(otherProjectApproved.Id, otherProjectApproved.Version));
        await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(otherProjectApproved.Id, otherSubmitted.Version));

        var invoiceService = fx.NewInvoiceService();
        var invoice = await invoiceService.CreateInvoiceAsync(fx.ProjectId);

        var line = Assert.Single(invoice.Lines);
        Assert.Equal(approved.Id, line.WorklogId);

        using var freshDb = fx.CreateContext();
        var reloadedDraft = await freshDb.Worklogs.SingleAsync(w => w.Id == draft.Id);
        Assert.Equal(WorklogStatus.Draft, reloadedDraft.Status);
        var reloadedOther = await freshDb.Worklogs.SingleAsync(w => w.Id == otherProjectApproved.Id);
        Assert.Equal(WorklogStatus.Approved, reloadedOther.Status);
    }

    [Fact]
    public async Task A_second_invoice_attempt_after_all_worklogs_are_already_invoiced_fails_validation_not_double_invoicing()
    {
        using var fx = new SqliteWorklogFixture();
        var worklogService = fx.NewService();
        var created = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 8m, "work"));
        var submitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version));
        await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(created.Id, submitted.Version));

        var invoiceService = fx.NewInvoiceService();
        await invoiceService.CreateInvoiceAsync(fx.ProjectId);

        var ex = await Assert.ThrowsAsync<DomainException>(() => invoiceService.CreateInvoiceAsync(fx.ProjectId));
        Assert.Equal(DomainErrorKind.Validation, ex.Kind);

        using var freshDb = fx.CreateContext();
        // Rule 9: still exactly one invoice line for this worklog.
        var lineCount = await freshDb.Invoices.SelectMany(i => i.Lines).CountAsync(l => l.WorklogId == created.Id);
        Assert.Equal(1, lineCount);
    }

    [Fact]
    public async Task Normal_hours_already_invoiced_on_another_project_reduce_this_invoices_normal_capacity_for_the_same_day()
    {
        using var fx = new SqliteWorklogFixture();
        var worklogService = fx.NewService();

        // 6h approved+invoiced on Project Alpha for this worker/day first.
        var alphaWork = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, Day, 6m, "alpha"));
        var alphaSubmitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(alphaWork.Id, alphaWork.Version));
        await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(alphaWork.Id, alphaSubmitted.Version));
        var invoiceService = fx.NewInvoiceService();
        await invoiceService.CreateInvoiceAsync(fx.ProjectId);

        // Then 4h approved on Project Beta for the same worker/day: only 2h of normal
        // capacity remain for that day, across projects.
        var betaWork = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.SecondProjectId, Day, 4m, "beta"));
        var betaSubmitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(betaWork.Id, betaWork.Version));
        await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(betaWork.Id, betaSubmitted.Version));

        var betaInvoice = await invoiceService.CreateInvoiceAsync(fx.SecondProjectId);

        var line = Assert.Single(betaInvoice.Lines);
        Assert.Equal(2m, line.NormalHours);
        Assert.Equal(2m, line.OvertimeHours);
    }
}
