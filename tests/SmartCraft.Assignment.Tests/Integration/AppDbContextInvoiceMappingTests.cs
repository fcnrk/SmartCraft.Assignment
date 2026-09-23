using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// Verifies the EF Core mapping itself enforces rule 9 ("a worklog can be associated with at
/// most one invoice") as a DB constraint — the task's explicit "DB UNIQUE index on
/// InvoiceLine.WorklogId" requirement — independent of InvoiceService's application-level
/// Status==Approved filter. Inspects model metadata rather than triggering the constraint at
/// runtime: InvoiceLine's factory is intentionally internal (same encapsulation as
/// ProjectAssignment.Create), so only Invoice/InvoiceService can construct one, and no public
/// path exists to insert a duplicate WorklogId without going through InvoiceService's own
/// eligibility filter.
/// </summary>
public class AppDbContextInvoiceMappingTests
{
    [Fact]
    public void InvoiceLine_WorklogId_is_the_entitys_primary_key()
    {
        using var fx = new SqliteWorklogFixture();
        using var db = fx.CreateContext();

        var invoiceLineType = db.Model.FindEntityType(typeof(InvoiceLine));
        Assert.NotNull(invoiceLineType);

        var primaryKey = invoiceLineType!.FindPrimaryKey();
        Assert.NotNull(primaryKey);
        Assert.Equal([nameof(InvoiceLine.WorklogId)], primaryKey!.Properties.Select(p => p.Name));
    }
}
