using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Api.Infrastructure;

/// <summary>
/// EF Core DbContext for the POC. Used directly (no repository/unit-of-work
/// wrapper — docs/02-architecture.md: "Do not create repository interfaces
/// solely to wrap every DbSet call").
///
/// Schema: created via <c>Database.EnsureCreated()</c>, not migrations, per
/// docs/02-architecture.md's POC scope. Callers (composition root / tests) are
/// responsible for calling it once against their connection before use.
///
/// Decimal mapping: <see cref="Worklog.Hours"/> uses EF Core's default decimal
/// mapping for the Sqlite provider, which stores decimal as TEXT via a
/// round-tripping string conversion (verified against
/// Microsoft.EntityFrameworkCore.Sqlite.Core 10.0.12's
/// SqliteDecimalTypeMapping: storeType "TEXT", no lossy double conversion) —
/// so a stored Hours value reads back bit-for-bit equal to what was written.
/// What TEXT storage does NOT give you is a numerically-correct SQL SUM()/
/// comparison: SQLite has no arbitrary-precision decimal arithmetic, so
/// aggregating decimal columns in SQL is not safe. The rule-3 daily-hours
/// check below therefore always loads a worker/date's rows and sums them in
/// C# decimal — the row count per worker/day is small, so this is cheap and
/// exact.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Worker> Workers => Set<Worker>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Worklog> Worklogs => Set<Worklog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Worker>(worker =>
        {
            worker.HasKey(w => w.Id);
            worker.Property(w => w.Name).IsRequired();
        });

        modelBuilder.Entity<Project>(project =>
        {
            project.HasKey(p => p.Id);
            project.Property(p => p.Name).IsRequired();

            // ProjectAssignment is owned by Project (docs/01-domain.md); its identity
            // is the (ProjectId, WorkerId) pair, which doubles as the required unique
            // constraint from docs/02-architecture.md.
            project.OwnsMany(p => p.Assignments, assignment =>
            {
                assignment.ToTable("ProjectAssignments");
                assignment.WithOwner().HasForeignKey(a => a.ProjectId);
                assignment.HasKey(a => new { a.ProjectId, a.WorkerId });
                assignment.HasOne<Worker>().WithMany().HasForeignKey(a => a.WorkerId).OnDelete(DeleteBehavior.Restrict);
            });
        });

        modelBuilder.Entity<Worklog>(worklog =>
        {
            worklog.HasKey(w => w.Id);
            worklog.Property(w => w.Description).IsRequired();
            worklog.Property(w => w.Status).HasConversion<string>();

            // App-managed optimistic-concurrency token (SQLite has no rowversion type).
            // EF adds `WHERE Version = @original` to the UPDATE and throws
            // DbUpdateConcurrencyException on a 0-row result; SaveChanges below is what
            // actually increments it — see the override for why.
            worklog.Property(w => w.Version).IsConcurrencyToken();

            // Rule 1/rule 3 are cross-aggregate application-layer checks (docs/01-domain.md),
            // but Worker/Project existence is still a DB-level invariant: FK, no navigation
            // property (docs/02-architecture.md: "Reference other aggregates by ID only").
            worklog.HasOne<Worker>().WithMany().HasForeignKey(w => w.WorkerId).OnDelete(DeleteBehavior.Restrict);
            worklog.HasOne<Project>().WithMany().HasForeignKey(w => w.ProjectId).OnDelete(DeleteBehavior.Restrict);

            // Supports the rule-3 per-worker/date lookup.
            worklog.HasIndex(w => new { w.WorkerId, w.WorkDate });
        });
    }

    /// <summary>
    /// The one place Worklog.Version is incremented. Centralizing it here (rather than in
    /// each domain mutator) means no mutation path — present or future — can forget to bump
    /// it: every persisted change to a Worklog row goes through SaveChanges, so every one
    /// gets exactly one increment. Keeping it out of the domain also matches
    /// CLAUDE.md's "keep cross-aggregate/database invariants in transactional application
    /// logic" — the concurrency token is a persistence concern, not a business rule.
    /// </summary>
    public override int SaveChanges()
    {
        IncrementWorklogVersions();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        IncrementWorklogVersions();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void IncrementWorklogVersions()
    {
        foreach (var entry in ChangeTracker.Entries<Worklog>())
        {
            if (entry.State == EntityState.Modified)
            {
                var versionProperty = entry.Property(w => w.Version);
                versionProperty.CurrentValue = (long)versionProperty.OriginalValue! + 1;
            }
        }
    }
}
