using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Domain;

/// <summary>
/// Covers the Draft -> Submitted -> Approved -> Invoiced lifecycle (docs/01-domain.md
/// rules 5, 6, 7, 8, 9) including every invalid transition and content/status
/// immutability on failure. Error kind is asserted throughout: lifecycle
/// violations must be DomainErrorKind.Conflict (maps to 409), not Validation.
/// </summary>
public class WorklogLifecycleTests
{
    public enum Operation
    {
        Update,
        Submit,
        Approve,
        MarkInvoiced,
    }

    // status, operation, shouldSucceed
    [Theory]
    [InlineData(WorklogStatus.Draft, Operation.Update, true)]
    [InlineData(WorklogStatus.Draft, Operation.Submit, true)]
    [InlineData(WorklogStatus.Draft, Operation.Approve, false)]
    [InlineData(WorklogStatus.Draft, Operation.MarkInvoiced, false)]
    [InlineData(WorklogStatus.Submitted, Operation.Update, false)]
    [InlineData(WorklogStatus.Submitted, Operation.Submit, false)]
    [InlineData(WorklogStatus.Submitted, Operation.Approve, true)]
    [InlineData(WorklogStatus.Submitted, Operation.MarkInvoiced, false)]
    [InlineData(WorklogStatus.Approved, Operation.Update, false)]
    [InlineData(WorklogStatus.Approved, Operation.Submit, false)]
    [InlineData(WorklogStatus.Approved, Operation.Approve, false)]
    [InlineData(WorklogStatus.Approved, Operation.MarkInvoiced, true)]
    [InlineData(WorklogStatus.Invoiced, Operation.Update, false)]
    [InlineData(WorklogStatus.Invoiced, Operation.Submit, false)]
    [InlineData(WorklogStatus.Invoiced, Operation.Approve, false)]
    [InlineData(WorklogStatus.Invoiced, Operation.MarkInvoiced, false)]
    public void Operation_from_status_has_expected_outcome(WorklogStatus status, Operation operation, bool shouldSucceed)
    {
        var worklog = WorklogTestHelpers.BuildAt(status, hours: 8m, description: "original");
        var invoiceId = Guid.NewGuid();

        void Act()
        {
            switch (operation)
            {
                case Operation.Update:
                    worklog.Update(new DateOnly(2026, 9, 24), 5m, "changed");
                    break;
                case Operation.Submit:
                    worklog.Submit();
                    break;
                case Operation.Approve:
                    worklog.Approve();
                    break;
                case Operation.MarkInvoiced:
                    worklog.MarkInvoiced(invoiceId);
                    break;
            }
        }

        if (shouldSucceed)
        {
            Act();

            var expectedStatus = operation switch
            {
                Operation.Update => status,
                Operation.Submit => WorklogStatus.Submitted,
                Operation.Approve => WorklogStatus.Approved,
                Operation.MarkInvoiced => WorklogStatus.Invoiced,
                _ => throw new InvalidOperationException(),
            };
            Assert.Equal(expectedStatus, worklog.Status);
        }
        else
        {
            var ex = Assert.Throws<DomainException>(Act);

            Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
            Assert.Equal(status, worklog.Status);
            Assert.Equal(8m, worklog.Hours);
            Assert.Equal("original", worklog.Description);
        }
    }

    [Fact]
    public void Full_lifecycle_Draft_to_Submitted_to_Approved_to_Invoiced_succeeds()
    {
        var worklog = Worklog.Create(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 9, 23), 8m, "work");
        var invoiceId = Guid.NewGuid();

        Assert.Equal(WorklogStatus.Draft, worklog.Status);

        worklog.Submit();
        Assert.Equal(WorklogStatus.Submitted, worklog.Status);

        worklog.Approve();
        Assert.Equal(WorklogStatus.Approved, worklog.Status);

        worklog.MarkInvoiced(invoiceId);
        Assert.Equal(WorklogStatus.Invoiced, worklog.Status);
        Assert.Equal(invoiceId, worklog.InvoiceId);
    }

    // ---- MarkInvoiced specifics (rule 7, 9) ----

    [Fact]
    public void MarkInvoiced_rejects_empty_invoiceId()
    {
        var worklog = WorklogTestHelpers.BuildAt(WorklogStatus.Approved);

        var ex = Assert.Throws<DomainException>(() => worklog.MarkInvoiced(Guid.Empty));

        Assert.Equal(DomainErrorKind.Validation, ex.Kind);
        Assert.Equal(WorklogStatus.Approved, worklog.Status);
        Assert.Null(worklog.InvoiceId);
    }

    [Fact]
    public void MarkInvoiced_second_call_fails_and_keeps_original_invoiceId()
    {
        var firstInvoiceId = Guid.NewGuid();
        var worklog = WorklogTestHelpers.BuildAt(WorklogStatus.Invoiced, invoiceId: firstInvoiceId);

        var ex = Assert.Throws<DomainException>(() => worklog.MarkInvoiced(Guid.NewGuid()));

        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
        Assert.Equal(WorklogStatus.Invoiced, worklog.Status);
        Assert.Equal(firstInvoiceId, worklog.InvoiceId);
    }
}
