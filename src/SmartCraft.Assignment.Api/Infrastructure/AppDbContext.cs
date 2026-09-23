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
/// so a stored Hours value reads back value-equal to what was written, but
/// NOT bit-for-bit: the write-side format string (verified by decompiling
/// SqliteDecimalTypeMapping) is <c>"{0:0.0###########################}"</c>
/// — one mandatory decimal digit, then up to 27 optional (`#`) ones, which
/// drops trailing zeros. E.g. 1.50m is formatted as "1.5" and reads back as
/// 1.5m (scale 1, not 2): decimal.Equals still holds (1.50m == 1.5m), but
/// the original scale/trailing zeros are not preserved. Relevant to future
/// invoice snapshots: don't rely on a persisted decimal's rendered
/// scale/trailing zeros for display — format explicitly wherever a fixed
/// number of decimal places matters.
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
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

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
                assignment.Property(a => a.WorkerRole).IsRequired();
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

        modelBuilder.Entity<Invoice>(invoice =>
        {
            invoice.HasKey(i => i.Id);
            invoice.HasOne<Project>().WithMany().HasForeignKey(i => i.ProjectId).OnDelete(DeleteBehavior.Restrict);

            // InvoiceLine is owned by Invoice (docs/02-architecture.md "Invoice aggregate").
            // Its key is WorklogId itself, not a generated id or a (InvoiceId, WorklogId)
            // composite: rule 9 ("a worklog can be associated with at most one invoice")
            // means WorklogId is already globally unique across every invoice line that will
            // ever exist, so making it the primary key gives the task's required "DB UNIQUE
            // index on InvoiceLine.WorklogId" for free — a PK is a unique index, enforced by
            // SQLite regardless of the Worklog.Status check in InvoiceService. It also
            // doubles as InvoiceLine's FK to Worklog below (a shared-key 1-to-(zero-or-)one
            // relationship), which is exactly what rule 9 describes.
            invoice.OwnsMany(i => i.Lines, line =>
            {
                line.ToTable("InvoiceLines");
                line.WithOwner().HasForeignKey("InvoiceId");
                line.HasKey(l => l.WorklogId);
                line.Property(l => l.WorkerRole).IsRequired();
                line.HasOne<Worklog>().WithMany().HasForeignKey(l => l.WorklogId).OnDelete(DeleteBehavior.Restrict);
            });
        });

        // Idempotency-Key uniqueness (docs/04 "Idempotency"): Key as primary key gives the
        // DB-level uniqueness constraint that InvoiceService's defense-in-depth race handling
        // relies on for free — same reasoning as InvoiceLine.WorklogId above.
        modelBuilder.Entity<IdempotencyRecord>(record =>
        {
            record.HasKey(r => r.Key);
            record.Property(r => r.Key).HasMaxLength(200);
            record.HasOne<Invoice>().WithMany().HasForeignKey(r => r.InvoiceId).OnDelete(DeleteBehavior.Restrict);
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
