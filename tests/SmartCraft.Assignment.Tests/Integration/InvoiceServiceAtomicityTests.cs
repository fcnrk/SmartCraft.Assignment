using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// Rule 16 ("invoice creation and claiming/marking its worklogs must be atomic"): if persisting
/// the new invoice fails partway through, nothing from that attempt survives -- no invoice row,
/// no invoice lines, and no worklog left marked Invoiced.
///
/// Reproduction strategy: SQLite's BEGIN IMMEDIATE (docs/04) fully serializes writers on one
/// file, so a genuine cross-connection race landing exactly between CreateInvoiceAsync's own
/// SELECT and its SaveChanges cannot be produced from a second connection -- it would simply
/// block until the first transaction finishes. Instead this reproduces the "InvoiceLine.WorklogId
/// is a primary key" defense-in-depth path InvoiceService's own doc comments call out explicitly:
/// invoice worklog W2 for real once (a genuine InvoiceLine row for W2 now exists), then use raw
/// SQL to reset *only* W2's Worklogs row back to Approved/InvoiceId=null -- simulating the
/// otherwise-impossible-on-SQLite race window where a second invoice attempt again considers W2
/// "eligible" while its historical InvoiceLine still exists. A second CreateInvoiceAsync call
/// (now selecting both W1 and the tampered-back-to-eligible W2) must then fail atomically: the
/// InvoiceLine insert for W2 collides with the still-present original row (unique-constraint
/// violation -> mapped Conflict), and the failure must take the *entire* attempt down with it,
/// including W1, which had nothing wrong with it on its own.
/// </summary>
public class InvoiceServiceAtomicityTests
{
    [Fact]
    public async Task A_worklog_unique_constraint_failure_during_save_rolls_back_the_whole_invoice_including_unrelated_worklogs()
    {
        using var fx = new SqliteWorklogFixture();
        var worklogService = fx.NewService();

        // W2 first, invoiced for real, alone.
        var w2 = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, new DateOnly(2026, 9, 23), 2m, "w2"));
        var w2Submitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(w2.Id, w2.Version));
        await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(w2.Id, w2Submitted.Version));

        var invoiceService = fx.NewInvoiceService();
        var firstInvoice = (await invoiceService.CreateInvoiceAsync(fx.ProjectId, "key-1")).Invoice;
        Assert.Single(firstInvoice.Lines);

        // W1: a second, unrelated, otherwise-perfectly-valid Approved worklog.
        var w1 = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, new DateOnly(2026, 9, 24), 3m, "w1"));
        var w1Submitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(w1.Id, w1.Version));
        await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(w1.Id, w1Submitted.Version));

        // Simulate the race window: reset W2 back to Approved/uninvoiced while its original
        // InvoiceLine row is left in place, untouched.
        using (var tamperDb = fx.CreateContext())
        {
            var rowsChanged = await tamperDb.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Worklogs SET Status = 'Approved', InvoiceId = NULL WHERE Id = {w2.Id}");
            Assert.Equal(1, rowsChanged);
        }

        var secondAttemptService = fx.NewInvoiceService();
        var ex = await Assert.ThrowsAsync<DomainException>(() => secondAttemptService.CreateInvoiceAsync(fx.ProjectId, "key-2"));
        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);

        using var freshDb = fx.CreateContext();

        // Still exactly the original invoice; no second invoice/lines persisted.
        var survivingInvoice = await freshDb.Invoices.SingleAsync();
        Assert.Equal(firstInvoice.Id, survivingInvoice.Id);
        var survivingLine = Assert.Single(survivingInvoice.Lines);
        Assert.Equal(w2.Id, survivingLine.WorklogId);

        // W1 must NOT have been left marked Invoiced by the rolled-back attempt: atomicity
        // means the whole batch fails together, not just the specific colliding worklog.
        var reloadedW1 = await freshDb.Worklogs.SingleAsync(w => w.Id == w1.Id);
        Assert.Equal(WorklogStatus.Approved, reloadedW1.Status);
        Assert.Null(reloadedW1.InvoiceId);

        // W2 (tampered) is unchanged by the failed attempt -- still Approved/uninvoiced, exactly
        // as the pre-attempt tamper left it, not re-marked Invoiced by the rolled-back write.
        var reloadedW2 = await freshDb.Worklogs.SingleAsync(w => w.Id == w2.Id);
        Assert.Equal(WorklogStatus.Approved, reloadedW2.Status);
        Assert.Null(reloadedW2.InvoiceId);
    }
}
