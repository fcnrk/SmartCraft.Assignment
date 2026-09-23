# Concurrency, Idempotency, and Authorization

## Concurrency is a correctness requirement

Assume multiple users and multiple API instances. Do not rely on process-local locks.

### Scenario: lost worklog update

Two clients read Draft Worklog v3, both edit it, and both save. The second must not silently overwrite the first. Use optimistic concurrency and surface a conflict.

### Scenario: lifecycle race

Two actors attempt transitions against the same Worklog. State validation plus optimistic concurrency must prevent impossible/silent transitions.

### Scenario: daily-hours race

Current total is 20 hours. Two concurrent requests each add 3 hours. Both can read 20 and independently decide 23 is valid, resulting in 26.

A normal transaction at an insufficient isolation level does not automatically solve this. Implement a defensible strategy for the selected DB or document the POC limitation and production solution (e.g. stronger isolation, locking/serialization by worker-day, database-specific constraint/model).

**POC decision (iteration 2):** `WorklogService.CreateWorklogAsync`/`UpdateWorklogAsync` open the write transaction with EF Core's parameterless `Database.BeginTransactionAsync()` *before* reading existing worklogs for the worker/date. Verified by decompiling Microsoft.Data.Sqlite 10.0.12: that call resolves to `SqliteConnection.BeginTransaction(IsolationLevel.Unspecified)`, which normalizes to `Serializable` with `deferred=false`, and `SqliteTransaction`'s constructor then issues `"BEGIN IMMEDIATE;"` (confirmed against the decompiled IL, not assumed) — i.e. this is already EF Core's default against Sqlite, no explicit isolation-level/deferred argument is needed. `BEGIN IMMEDIATE` acquires SQLite's single database-wide write lock immediately, so the read-sum-write sequence for the daily-hours check is fully serialized against every other writer; a second writer blocks (does not fail immediately) for up to the connection's busy timeout, and only then gets `SQLITE_BUSY`/`SQLITE_LOCKED`, which the service catches (whether raised directly or wrapped in `DbUpdateException`, and whether raised while acquiring the lock, mid-transaction, or at commit) and maps to an explicit, retryable `DomainErrorKind.Conflict` rather than an opaque exception — see `WorklogService.WithBusyMappingAsync`, the one shared guard all four write operations (Create/Update/Submit/Approve) route through. This was exercised with two concurrent `WorklogService` instances on separate `SqliteConnection`s against the same file (20h existing + two concurrent +3h requests): the daily total after both requests never exceeded 24 — one request succeeded, the other was rejected with the documented 24h-limit `Validation` error, and no data race was observed across repeated runs.

Note: `Database.EnsureCreated()` (see docs/02-architecture.md, "Database" → "WAL mode") turns on WAL journal mode as a side effect the first time a database file is created. WAL only changes concurrent-*read* throughput (readers no longer block behind an in-progress writer); it does not weaken this section's correctness story — `BEGIN IMMEDIATE` still takes SQLite's single write lock and still fully serializes every writer, WAL or not.

**Correction:** the busy timeout above is *not* `sqlite3_busy_timeout` — Microsoft.Data.Sqlite 10.0.12 never calls `sqlite3_busy_timeout`/registers a native busy handler at all (verified: no such call anywhere in the decompiled library). It is a managed retry loop inside `SqliteCommand`/`SqliteDataReader`: on `SQLITE_BUSY`/`SQLITE_LOCKED` it calls `Thread.Sleep(150)` and retries until the command's `CommandTimeout` elapses, where `CommandTimeout` defaults to the connection's `DefaultTimeout` (the `Default Timeout` connection-string keyword, 30s by default). Because the wait is `Thread.Sleep`, it blocks the calling thread even through the async APIs and does not observe a `CancellationToken` — worth knowing for thread-pool sizing under load, not just latency.

**Ceiling:** this is SQLite's single-writer model doing the work, not a portable technique — and even within SQLite it only serializes writers that share the same local database file from processes on one host; it says nothing about multiple hosts or a networked database. On PostgreSQL/SQL Server at the default READ COMMITTED isolation level, this exact code (read existing hours, sum, decide, write) would race the same way the scenario above describes — a normal transaction is not sufficient there either. Production approach: lock a per-(worker, date) row, e.g. an upsert into a `WorkerDay(WorkerId, WorkDate, TotalHours)` table read with `SELECT ... FOR UPDATE`, or a transactionally-maintained running total with a `CHECK(TotalHours <= 24)` constraint, or run the whole check+write at `SERIALIZABLE` and retry on serialization failure. See `WorklogService.EnsureDailyHoursWithinLimitAsync` for the in-code version of this note.

### Scenario: competing invoice requests

Two different invoice commands select the same approved worklogs. At most one invoice may claim a given worklog. Use transaction/concurrency/database constraints so both cannot succeed.

## Idempotency

Idempotency solves retries of the same logical command; it is not a replacement for concurrency control over business resources.

Invoice creation requires an `Idempotency-Key` (or equivalent explicit mechanism).

Persist enough information to distinguish:

- same key + same logical request -> return/replay original successful result
- same key + materially different request -> conflict
- concurrent same-key requests -> only one executes the business effect

Suggested record:

- key
- operation/scope
- request fingerprint/hash
- status
- resulting resource ID/response metadata
- timestamps if useful

Back this with a database uniqueness constraint. Avoid unsafe `if (!Exists(key)) Insert(...)` check-then-act logic.

## Business uniqueness

Independently ensure a Worklog can be consumed by at most one invoice. Idempotency key uniqueness alone cannot prevent two different requests from invoicing the same worklogs.

## Authentication

Use a lightweight POC-friendly JWT bearer setup. Do not spend time on implementing identity/password management.

Identity should expose a stable subject/worker mapping where required.

## Authorization

Prefer capability policies such as:

- `worklog:create`
- `worklog:update-own`
- `worklog:submit-own`
- `worklog:approve`
- `invoice:create`
- `invoice:read`

Example intent:

- workers create/update/submit their own worklogs
- privileged approvers approve submitted worklogs
- billing users generate/read invoices

Authentication establishes who the caller is. Authorization establishes whether the caller may request an action. Domain rules still determine whether the action is valid in the current business state.

Do not rely only on endpoint authorization if ownership/resource-specific authorization must also be checked after loading the resource.
