# Architecture and Aggregate Boundaries

## Goal

Use a small architecture that makes business behavior and migration boundaries obvious without turning the exercise into an architecture-framework demonstration.

## Suggested solution shape

```text
src/
  SmartCraft.Assignment.Api/
    Domain/
    Application/
    Infrastructure/
    Endpoints/ (or Controllers/)
tests/
  SmartCraft.Assignment.Tests/
```

Two projects are sufficient unless a concrete need justifies more.

## Aggregate roots

### Project aggregate

Owns project metadata and project assignments/billing terms.

Do not model all historical worklogs as a collection inside Project. They are independently addressable, numerous, and have their own lifecycle/concurrency requirements.

### Worker aggregate

Small independent aggregate representing worker identity/domain metadata. Project-specific billing belongs to ProjectAssignment.

### Worklog aggregate

Each Worklog is an independent aggregate root with explicit lifecycle transitions and optimistic concurrency.

### Invoice aggregate

Owns its invoice lines and historical calculation snapshot. Invoice lines are not independently modified outside Invoice.

## References

Prefer IDs across aggregate boundaries:

```text
Worker      Project
   \          /
    \ IDs    / IDs
      Worklog
         |
         | consumed by
         v
       Invoice
```

Do not create large bidirectional object graphs simply because EF Core supports navigation properties.

## Application orchestration

Cross-aggregate operations belong in an application command/service/handler layer.

Invoice generation conceptually:

```text
CreateDraftInvoice
       |
       v
Load project + approved uninvoiced worklogs + assignments
       |
       v
Invoice generator
       |
       v
Create Invoice
       |
       +--> persist invoice
       +--> mark worklogs invoiced
       +--> complete idempotency record

             ONE TRANSACTION
```

The important decision is the transactional boundary, not whether the orchestration type is called a handler, service, or use case.

## Legacy modernization seam

Define a behavioral contract around invoice calculation/generation, e.g. conceptually:

```text
                 Invoice generation contract
                         ^
              +----------+----------+
              |                     |
      Legacy implementation    Modern C# implementation
      stored procedure/adapter domain calculation
```

Keep the legacy stored-procedure adapter in Infrastructure. The modern implementation should not inherit stored-procedure structure merely to make comparison easy.

The seam exists so equivalent business input can be sent through old and new implementations and normalized for comparison.

### POC decision (iteration 3)

The seam is `Application/IInvoiceCalculator.cs`: a pure, DB-free contract —
`Calculate(IReadOnlyList<WorklogBillingInput>, priorNormalHoursByWorkerDate) -> IReadOnlyList<CalculatedInvoiceLine>`.
`ModernInvoiceCalculator` (also in `Application/`) is the only implementation that exists this
iteration. It is intentionally an interface with one implementation today — normally a
speculative abstraction — because the second implementation and the differential harness that
compares the two are explicitly planned, not hypothetical, follow-up work
(`docs/05-testing-and-differential.md`, `docs/06-implementation-plan.md` Phase 4): a legacy
stored-procedure adapter belongs in `Infrastructure/`, implementing the same
`IInvoiceCalculator` interface, taking the same input records, and producing the same
`CalculatedInvoiceLine` output shape so a comparator can run both against identical input and
diff the results field-by-field. Not built this iteration to keep scope tight.

## Persistence

EF Core is acceptable directly. Do not create repository interfaces solely to wrap every DbSet call.

Add an abstraction only where it provides a real boundary, e.g. legacy invoice generation, clock, authentication context, or behavior that genuinely needs alternative implementations.

## Database

SQLite is acceptable for evaluator convenience, but document semantic/concurrency differences from a likely production RDBMS. PostgreSQL/SQL Server is also reasonable if setup remains simple.

Correctness claims must match the chosen database's actual transaction/isolation behavior.

### POC decision (iteration 2)

- **DB: SQLite** via `Microsoft.EntityFrameworkCore.Sqlite` 10.0.12 (matching net10.0), accessed through `AppDbContext` directly (no repository/unit-of-work interfaces) — see `src/SmartCraft.Assignment.Api/Infrastructure/AppDbContext.cs`.
- **Schema: `Database.EnsureCreated()`, no migrations.** The caller (composition root or a test fixture) is responsible for calling it once against its connection; `AppDbContext` does not call it implicitly in a constructor/`OnConfiguring`, to keep "who owns schema creation" explicit rather than hidden per-instance. A real migration path (`dotnet ef migrations`) is the production equivalent, deliberately out of scope here.
- **Decimal mapping for `Worklog.Hours`:** EF Core's *default* decimal mapping for the Sqlite provider (verified by decompiling `Microsoft.EntityFrameworkCore.Sqlite.Core` 10.0.12's `SqliteDecimalTypeMapping`) stores decimal as `TEXT` via a round-tripping string conversion — not the lossy double-based storage older EF Core/SQLite combinations used. A value read back is **value-equal** to what was written (e.g. `1.50m == 1.5m`), but **not bit-for-bit**: the write-side format (`"{0:0.0###########################}"`) drops trailing zeros, so `1.50m` is stored as `"1.5"` and reads back with scale 1, not 2. No custom `ValueConverter` was added. What this does **not** give you is a numerically-correct SQL `SUM()`/`<=` over that column: SQLite has no arbitrary-precision decimal arithmetic, so it would coerce the TEXT values through its REAL/INTEGER affinity rules for those operators, which is not exact. The rule-3 daily-24h check therefore always loads the (small) set of a worker/date's worklog rows and sums them as C# `decimal` in memory, instead of summing in SQL. Follow-up for the invoicing iteration: don't rely on a persisted decimal's rendered scale for a monetary snapshot's display — format explicitly.
- **Single-writer semantics / rule 3 (daily hours):** see `docs/04-concurrency-idempotency-auth.md` → "Scenario: daily-hours race" → "POC decision (iteration 2)" for the full BEGIN IMMEDIATE / busy-timeout writeup and the production alternative (per-worker-day lock row or SERIALIZABLE + retry).
- **WAL mode:** already enabled, as a side effect of `Database.EnsureCreated()` rather than an explicit decision. Corrected: this was previously (and incorrectly) documented as "not enabled". Verified by decompiling `Microsoft.EntityFrameworkCore.Sqlite.Core` 10.0.12's `SqliteDatabaseCreator.Create()` (which `EnsureCreated()` calls the first time a database file doesn't exist yet) — it unconditionally runs `PRAGMA journal_mode = 'wal';` right after opening the connection, before creating any tables — and confirmed empirically (`EnsureCreated()` against a fresh file, then `PRAGMA journal_mode;` on a fresh connection to the same file returns `wal`). It lets readers avoid blocking on an in-progress `BEGIN IMMEDIATE` writer, which is a concurrent-read throughput improvement, not a correctness change: it does **not** weaken the single-writer story above — `BEGIN IMMEDIATE` still acquires SQLite's one write lock and still fully serializes writers under WAL, so the rule-3 daily-hours check is unaffected. (Aside: this also means `WorklogService`'s doc comments claiming a writer blocks "acquiring the EXCLUSIVE lock at commit" were imprecise — under WAL, commit does not take a whole-database EXCLUSIVE lock; corrected there too.)
- **Optimistic concurrency:** `Worklog.Version` (long) is a Fluent-API concurrency token (`IsConcurrencyToken()`); `AppDbContext.SaveChanges[Async]` is the single place that increments it for `Modified` `Worklog` entries (see the override's doc comment for why centralizing there, rather than in each domain mutator, was chosen).
