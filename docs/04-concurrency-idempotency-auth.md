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

**POC decision (iteration 3):** `InvoiceService.CreateInvoiceAsync` opens its transaction the
same way `WorklogService` does — `Database.BeginTransactionAsync()` before any read — which
resolves to SQLite's `BEGIN IMMEDIATE` (see the daily-hours-race note above for the decompiled
verification of that translation) and takes SQLite's single write lock up front. Two concurrent
`CreateInvoiceAsync` calls against the same file are therefore fully serialized: the second
call's reads (eligible worklogs, prior-invoiced normal hours) cannot observe a state the first
call's transaction has not yet committed, so neither the same worklog nor the same worker/day's
normal-time capacity can be double-claimed. On top of that, two DB-level constraints hold
independent of SQLite's exclusivity, as defense in depth for any DB/isolation level: the
`Worklog.Version` optimistic-concurrency token (a worklog claimed/modified between load and
save fails with a mapped `Conflict`) and `InvoiceLine.WorklogId` being the primary key of the
`InvoiceLines` table (rule 9 — a second line for an already-invoiced `WorklogId` is a DB
constraint violation, also mapped to `Conflict`).

**Ceiling:** identical to the daily-hours-race note above — this is SQLite's single-writer
model, not a portable technique, and it says nothing about multiple hosts or a networked
database. The specific gap on Postgres/SQL Server at READ COMMITTED is the **cross-project
normal-hours consumption** check (docs/01-domain.md "overtime allocation"): two concurrent
invoice-creation transactions for the same worker's worklogs on two different projects, same
calendar date, could each read the same "prior normal hours consumed so far" total and both
allocate that worker's first 8 hours of the day as normal time — an under-counted-overtime race
that the `Worklog.Version` token and the `InvoiceLine.WorklogId` PK do **not** prevent (they
stop the same worklog being claimed twice, not two different worklogs each mis-computing
overtime for a shared capacity they both read as unconsumed). Production fix: the same kind of
per-(worker, date) lock/serialization the daily-hours-race note already asks for (e.g. a
`WorkerDay` row read `FOR UPDATE` as part of computing prior-normal-hours), or running the whole
invoice-creation transaction at `SERIALIZABLE` and retrying on serialization failure.

## Idempotency

Idempotency solves retries of the same logical command; it is not a replacement for concurrency control over business resources.

Invoice creation requires an `Idempotency-Key` (or equivalent explicit mechanism).

**POC decision (endpoints iteration): implemented.** `POST /api/projects/{projectId}/invoices`
requires an `Idempotency-Key` header (docs/03-api-transactions.md); `InvoiceService.
CreateInvoiceAsync(projectId, idempotencyKey, ct)` looks up an `IdempotencyRecord` by key as the
*first* read inside the same `BEGIN IMMEDIATE` transaction used for everything else in that
method (see its doc comment). Record shape: `Key` (string, primary key — no separate
Operation/scope column, since invoice creation is the only idempotent operation in this POC;
add one if a second operation needs it), `ProjectId` (the request fingerprint — the only input
that varies invoice creation), `InvoiceId`, `CreatedAt`.

- same key + same `ProjectId` -> replay: return the original invoice, no new effect (`200 OK`
  at the endpoint, vs `201 Created` for a first-time success).
- same key + a different `ProjectId` -> `409 Conflict`: the key does not describe this request.
- concurrent same-key requests: `IdempotencyRecord.Key` being the table's primary key is the
  defense-in-depth constraint (mirrors the `InvoiceLine.WorklogId` PK pattern used for rule 9).
  Under this POC's SQLite `BEGIN IMMEDIATE` serialization this constraint cannot actually be hit
  (the lookup and the insert are both inside one exclusive writer transaction — see
  `CreateInvoiceAsync`'s doc comment for the same ceiling note as everywhere else in this
  section), but the code path exists for any DB/isolation level where that exclusivity does not
  hold: `InvoiceService` catches the PK violation, distinguishes it from the unrelated
  `InvoiceLine.WorklogId` PK violation via which entity failed to insert
  (`DbUpdateException.Entries`), and — since SQLite does not partially apply a failed
  statement's transaction — rolls back its own attempt and replays the winner's now-committed
  record instead of surfacing a spurious error to the loser.
- **a failed attempt records nothing** (e.g. "no eligible worklogs" `Validation`): the
  `IdempotencyRecord` is only added to the same `SaveChanges`/commit as a successful invoice, so
  a retry with the same key after a failure reruns the request fresh rather than replaying the
  failure. This is a deliberate, documented choice, not an oversight — "idempotent" here means
  "a repeated *successful* request has no additional effect", not "every response, including
  failures, is cached forever".

Reviewer finding M1 (constraint-code breadth): the PK/unique-violation check narrowed from the
broad `SQLITE_CONSTRAINT` (19) primary code to the specific extended codes
`SQLITE_CONSTRAINT_PRIMARYKEY` (1555) / `SQLITE_CONSTRAINT_UNIQUE` (2067), so an unrelated FK or
NOT NULL violation is not misclassified as a `Conflict`.

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

### POC decision (endpoints iteration)

JWT bearer via `Microsoft.AspNetCore.Authentication.JwtBearer`, symmetric (HMAC-SHA256) key
from configuration (`Jwt:Key`/`Jwt:Issuer`/`Jwt:Audience`) — no default key, a missing one fails
startup fast rather than silently accepting an insecure default. The real (dev) key lives only
in `appsettings.Development.json`; `appsettings.json` (base/Production) has none, so this POC
only meaningfully runs in Development (see `README.md` "Known limitations" — there is no token
issuance path for any other environment). Token claims: `sub` = WorkerId (Guid), zero or more
`permission` claims. `JwtBearerOptions.MapInboundClaims = false` keeps claim types exactly as
issued ("sub", "permission") instead of ASP.NET's default remap to long `ClaimTypes` URIs.

Since there is no real identity provider, `POST /dev/token` (Development-only, mapped only when
`ASPNETCORE_ENVIRONMENT=Development`; not present otherwise) mints a signed token for any
`{ workerId, permissions[] }` the caller supplies, no credentials required — an explicit,
documented POC stand-in, not a security control.

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

### POC decision (endpoints iteration)

One ASP.NET Core authorization policy per permission string above, named after the permission
itself, plus `project:read` (not in the original list — added for the read-only project
endpoints; docs/03 "Optional only if time permits" already anticipated project/worker setup
endpoints). Registered via a loop over `Permissions.All` (`Authorization/Permissions.cs`), not
copy-pasted per policy — each policy is `RequireClaim("permission", <the permission string>)`.

Resource ownership (docs/04 "Do not rely only on endpoint authorization..."):

- **Create**: `WorkerId` is derived directly from the caller's `sub` claim
  (`ClaimsPrincipalExtensions.GetWorkerId`), not read from the request body — bypass-proof by
  construction, no ownership check needed because there is no attacker-controlled field to
  check.
- **Update/Submit**: the worklog is loaded first, then `worklog.WorkerId == caller` is checked
  ("check after loading") — `403 Forbidden` otherwise. `WorkerId` is immutable once a worklog
  exists (no reassignment operation), so there is no meaningful race between this pre-check load
  and `WorklogService`'s own load inside its transaction.
- **Approve**: `worklog:approve` is a capability any approver holds; ownership is inverted here
  — the endpoint loads the worklog and returns `403 Forbidden` if `worklog.WorkerId == caller`
  (a worklog may not be approved by the worker who logged it).

No `worklog:read` permission exists in the list above; `GET /api/worklogs/{id}` only requires
*some* valid, authenticated identity (`RequireAuthorization()` with no policy) — a narrower
per-worklog read policy was judged out of scope for this POC's read-mostly-non-sensitive data.
