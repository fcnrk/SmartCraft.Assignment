using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;
using SmartCraft.Assignment.Api.Infrastructure;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// One real SQLite <b>file</b> database per instance (docs/05-testing-and-differential.md:
/// "Avoid using a fake provider to 'prove' transaction/concurrency behavior it does not
/// implement" — not EF InMemory, not a single shared in-memory connection). Every call to
/// <see cref="CreateContext"/>/<see cref="NewService"/> opens its own
/// <see cref="AppDbContext"/>/<see cref="SqliteConnection"/> against the same file, exactly
/// like separate API instances would, so genuinely concurrent tests race real connections.
///
/// Seeds two workers assigned to a project (plus a second project both are assigned to) and
/// one unassigned worker, so most rule-1/rule-3 tests don't need to seed anything themselves.
/// </summary>
internal sealed class SqliteWorklogFixture : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public Guid WorkerId { get; }
    public Guid OtherWorkerId { get; }
    public Guid UnassignedWorkerId { get; }
    public Guid ProjectId { get; }
    public Guid SecondProjectId { get; }

    public SqliteWorklogFixture(int busyTimeoutSeconds = 30)
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"smartcraft-test-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath};Foreign Keys=True;Default Timeout={busyTimeoutSeconds}";

        using var db = CreateContext();
        db.Database.EnsureCreated();

        var worker = Worker.Create("Worker A");
        var otherWorker = Worker.Create("Worker B");
        var unassignedWorker = Worker.Create("Unassigned Worker");
        db.Workers.AddRange(worker, otherWorker, unassignedWorker);

        var project = Project.Create("Project Alpha");
        project.AssignWorker(worker.Id, "Developer", 100m);
        project.AssignWorker(otherWorker.Id, "Tester", 80m);
        db.Projects.Add(project);

        var secondProject = Project.Create("Project Beta");
        secondProject.AssignWorker(worker.Id, "Developer", 120m);
        db.Projects.Add(secondProject);

        db.SaveChanges();

        WorkerId = worker.Id;
        OtherWorkerId = otherWorker.Id;
        UnassignedWorkerId = unassignedWorker.Id;
        ProjectId = project.Id;
        SecondProjectId = secondProject.Id;
    }

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new AppDbContext(options);
    }

    public WorklogService NewService() => new(CreateContext());

    public InvoiceService NewInvoiceService(TimeProvider? timeProvider = null) =>
        new(CreateContext(), new ModernInvoiceCalculator(), timeProvider ?? TimeProvider.System);

    public void Dispose()
    {
        // Release pooled connections before deleting the file, otherwise the delete can
        // fail/no-op on Windows while a pooled handle is still open.
        SqliteConnection.ClearAllPools();

        try
        {
            File.Delete(_dbPath);
            File.Delete(_dbPath + "-journal");
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover temp file doesn't affect test correctness.
        }
    }
}
