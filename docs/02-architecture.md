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

## Persistence

EF Core is acceptable directly. Do not create repository interfaces solely to wrap every DbSet call.

Add an abstraction only where it provides a real boundary, e.g. legacy invoice generation, clock, authentication context, or behavior that genuinely needs alternative implementations.

## Database

SQLite is acceptable for evaluator convenience, but document semantic/concurrency differences from a likely production RDBMS. PostgreSQL/SQL Server is also reasonable if setup remains simple.

Correctness claims must match the chosen database's actual transaction/isolation behavior.

### POC decision (iteration 2)

- **DB: SQLite** via `Microsoft.EntityFrameworkCore.Sqlite` 10.0.12 (matching net10.0), accessed through `AppDbContext` directly (no repository/unit-of-work interfaces) — see `src/SmartCraft.Assignment.Api/Infrastructure/AppDbContext.cs`.
- **Schema: `Database.EnsureCreated()`, no migrations.** The caller (composition root or a test fixture) is responsible for calling it once against its connection; `AppDbContext` does not call it implicitly in a constructor/`OnConfiguring`, to keep "who owns schema creation" explicit rather than hidden per-instance. A real migration path (`dotnet ef migrations`) is the production equivalent, deliberately out of scope here.
- **Decimal mapping for `Worklog.Hours`:** EF Core's *default* decimal mapping for the Sqlite provider (verified by decompiling `Microsoft.EntityFrameworkCore.Sqlite.Core` 10.0.12's `SqliteDecimalTypeMapping`) stores decimal as `TEXT` via a round-tripping string conversion — not the lossy double-based storage older EF Core/SQLite combinations used. A value read back is therefore bit-for-bit equal to what was written; no custom `ValueConverter` was added. What this does **not** give you is a numerically-correct SQL `SUM()`/`<=` over that column: SQLite has no arbitrary-precision decimal arithmetic, so it would coerce the TEXT values through its REAL/INTEGER affinity rules for those operators, which is not exact. The rule-3 daily-24h check therefore always loads the (small) set of a worker/date's worklog rows and sums them as C# `decimal` in memory, instead of summing in SQL.
- **Single-writer semantics / rule 3 (daily hours):** see `docs/04-concurrency-idempotency-auth.md` → "Scenario: daily-hours race" → "POC decision (iteration 2)" for the full BEGIN IMMEDIATE / busy-timeout writeup and the production alternative (per-worker-day lock row or SERIALIZABLE + retry).
- **WAL mode:** not enabled in this iteration (no reader/writer contention to demonstrate yet without HTTP endpoints). If added, it is a per-database-file `PRAGMA journal_mode=WAL;` that lets readers avoid blocking on an in-progress `BEGIN IMMEDIATE` writer — it does not change the single-writer correctness story above, only concurrent-read throughput. Flagged here as a known follow-up once endpoints exist.
- **Optimistic concurrency:** `Worklog.Version` (long) is a Fluent-API concurrency token (`IsConcurrencyToken()`); `AppDbContext.SaveChanges[Async]` is the single place that increments it for `Modified` `Worklog` entries (see the override's doc comment for why centralizing there, rather than in each domain mutator, was chosen).
