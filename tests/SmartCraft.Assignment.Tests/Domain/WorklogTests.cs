using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Domain;

/// <summary>
/// Covers docs/01-domain.md rules 2 (hours bounds), 4/8 (Draft-only editing,
/// content immutability) and Create's own input checks. Rules 1 and 3 are
/// explicitly out of scope for the domain-only Worklog aggregate.
/// </summary>
public class WorklogTests
{
    private static readonly Guid WorkerId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly DateOnly WorkDate = new(2026, 9, 23);

    private static Worklog CreateDraft(decimal hours = 8m, string? description = "work")
        => Worklog.Create(WorkerId, ProjectId, WorkDate, hours, description);

    // ---- Create ----

    [Fact]
    public void Create_starts_in_Draft_with_no_invoice()
    {
        var worklog = CreateDraft();

        Assert.Equal(WorklogStatus.Draft, worklog.Status);
        Assert.Null(worklog.InvoiceId);
        Assert.NotEqual(Guid.Empty, worklog.Id);
    }

    [Fact]
    public void Create_rejects_empty_WorkerId()
    {
        var ex = Assert.Throws<DomainException>(
            () => Worklog.Create(Guid.Empty, ProjectId, WorkDate, 8m, "work"));

        Assert.Equal(DomainErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public void Create_rejects_empty_ProjectId()
    {
        var ex = Assert.Throws<DomainException>(
            () => Worklog.Create(WorkerId, Guid.Empty, WorkDate, 8m, "work"));

        Assert.Equal(DomainErrorKind.Validation, ex.Kind);
    }

    // ---- Hours boundaries (rule 2: 0 < Hours <= 24) on Create ----

    [Theory]
    [InlineData(0.01)]
    [InlineData(8)]
    [InlineData(24)]
    public void Create_accepts_hours_within_bounds(decimal hours)
    {
        var worklog = CreateDraft(hours);

        Assert.Equal(hours, worklog.Hours);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(24.01)]
    public void Create_rejects_hours_out_of_bounds(decimal hours)
    {
        var ex = Assert.Throws<DomainException>(() => CreateDraft(hours));

        Assert.Equal(DomainErrorKind.Validation, ex.Kind);
    }

    // ---- Update (rule 4: Draft-only) ----

    [Fact]
    public void Update_on_Draft_changes_content()
    {
        var worklog = CreateDraft();
        var newDate = new DateOnly(2026, 9, 24);

        worklog.Update(newDate, 5m, "revised");

        Assert.Equal(newDate, worklog.WorkDate);
        Assert.Equal(5m, worklog.Hours);
        Assert.Equal("revised", worklog.Description);
        Assert.Equal(WorklogStatus.Draft, worklog.Status);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(24)]
    public void Update_accepts_hours_within_bounds(decimal hours)
    {
        var worklog = CreateDraft();

        worklog.Update(WorkDate, hours, "ok");

        Assert.Equal(hours, worklog.Hours);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(24.01)]
    public void Update_rejects_hours_out_of_bounds_and_leaves_state_unchanged(decimal badHours)
    {
        var worklog = CreateDraft(hours: 8m, description: "original");

        var ex = Assert.Throws<DomainException>(() => worklog.Update(WorkDate, badHours, "changed"));

        Assert.Equal(DomainErrorKind.Validation, ex.Kind);
        Assert.Equal(8m, worklog.Hours);
        Assert.Equal("original", worklog.Description);
        Assert.Equal(WorklogStatus.Draft, worklog.Status);
    }

    [Theory]
    [InlineData(WorklogStatus.Submitted)]
    [InlineData(WorklogStatus.Approved)]
    [InlineData(WorklogStatus.Invoiced)]
    public void Update_on_non_Draft_status_fails_and_leaves_state_unchanged(WorklogStatus status)
    {
        var worklog = WorklogTestHelpers.BuildAt(status, hours: 8m, description: "original");
        var newDate = new DateOnly(2026, 9, 24);

        var ex = Assert.Throws<DomainException>(() => worklog.Update(newDate, 5m, "changed"));

        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
        Assert.Equal(8m, worklog.Hours);
        Assert.Equal("original", worklog.Description);
        Assert.Equal(WorkDate, worklog.WorkDate);
        Assert.Equal(status, worklog.Status);
    }
}
