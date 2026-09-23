using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Domain;

/// <summary>Builds a Worklog already positioned at a given status, driving it
/// there only through the aggregate's own public operations.</summary>
internal static class WorklogTestHelpers
{
    public static Worklog BuildAt(
        WorklogStatus status,
        decimal hours = 8m,
        string? description = "work",
        Guid? invoiceId = null)
    {
        var worklog = Worklog.Create(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 9, 23), hours, description);

        if (status == WorklogStatus.Draft)
        {
            return worklog;
        }

        worklog.Submit();
        if (status == WorklogStatus.Submitted)
        {
            return worklog;
        }

        worklog.Approve();
        if (status == WorklogStatus.Approved)
        {
            return worklog;
        }

        worklog.MarkInvoiced(invoiceId ?? Guid.NewGuid());
        return worklog;
    }
}
