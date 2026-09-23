# SmartCraft.Assignment: Legacy Invoicing Modernization POC

A small .NET 10 backend that shows how to modernize a legacy project, time-reporting and invoicing system safely. The pieces:
- Worklogs follow an explicit lifecycle: `Draft → Submitted → Approved → Invoiced`.
- A modern C# invoice calculator replaces a (simulated) legacy stored procedure behind one seam.
- A differential harness compares the two calculators and classifies every difference.

The design baseline is in `docs/01`–`docs/06`. Every AI-assisted step and how it was verified is in `AI_LOG.md`.

## How to run

Requires the .NET 10 SDK (or just Docker).

```
dotnet build
dotnet test                                                   # 128 tests: unit, SQLite integration/concurrency, API, differential
dotnet test --filter "FullyQualifiedName~Differential"        # legacy vs modern only
dotnet run --project src/SmartCraft.Assignment.Api            # http://localhost:5164/swagger
```

Docker:

```
docker compose up --build                                     # http://localhost:8080/swagger, SQLite in a named volume at /data
# or
docker build -t smartcraft-assignment .
docker run --rm -p 8080:8080 smartcraft-assignment
```

On startup the app creates the schema (`EnsureCreated`) and seeds the data below if the database is empty. No other setup is needed.

## API and auth usage

| Endpoint | Permission |
|---|---|
| `GET /api/projects`, `GET /api/projects/{id}` | `project:read` |
| `POST /api/worklogs` (creates the worklog for the caller's own worker id) | `worklog:create` |
| `GET /api/worklogs/{id}` | any authenticated caller |
| `PUT /api/worklogs/{id}` (own Draft only) | `worklog:update-own` |
| `POST /api/worklogs/{id}/submit` (own only) | `worklog:submit-own` |
| `POST /api/worklogs/{id}/approve` (not your own) | `worklog:approve` |
| `POST /api/projects/{projectId}/invoices` + `Idempotency-Key` header | `invoice:create` |
| `GET /api/invoices/{id}` | `invoice:read` |

**Tokens.** There is no identity provider. `POST /dev/token` exists only in Development and mints a JWT for any worker id and permission set. In Swagger, click **Authorize** and paste the token.

```
curl -s -X POST http://localhost:5164/dev/token -H "Content-Type: application/json" \
  -d '{"workerId":"11111111-1111-1111-1111-111111111111","permissions":["worklog:create","worklog:update-own","worklog:submit-own","project:read"]}'
```

**Seed data** (`Infrastructure/SeedData.cs`):

| | Id |
|---|---|
| Alice: Developer on Phoenix, 100/h | `11111111-1111-1111-1111-111111111111` |
| Bob: Tester on Phoenix, 80/h | `22222222-2222-2222-2222-222222222222` |
| Carol: approver / billing | `33333333-3333-3333-3333-333333333333` |
| Project Phoenix | `44444444-4444-4444-4444-444444444444` |

**Example flow:**
1. As Alice, create a 10h worklog.
2. As Alice, submit it with `{"expectedVersion": <version>}`.
3. As Carol (`worklog:approve`, `invoice:create`, `invoice:read`), approve it.
4. As Carol, `POST .../invoices` with `Idempotency-Key: run-1`. You get **201** and a total of 1100.00 (8h × 100 plus 2h × 100 × 1.5).
5. Send the same request again. You get **200** with the same invoice.

Errors are ProblemDetails:
- **400**: validation, or a missing key.
- **401 / 403**: no token, a missing permission, or not the owner.
- **404**: not found.
- **409**: a stale version, an invalid transition, a key reused for a different project, or a database-busy timeout.

## Key decisions and trade-offs

- **Domain rules live on the aggregates.** `Worklog` exposes `Update`/`Submit`/`Approve`/`MarkInvoiced`, with no status setter. Rules that span aggregates run in application services inside one transaction: the 24h/day limit, the project assignment check, and invoice claiming.
- **SQLite with `BEGIN IMMEDIATE`.** Every write transaction takes SQLite's single write lock before it reads, so read-check-write races are serialized. This covers the daily-hours limit and the cross-project overtime ceiling. The database also enforces:
  - a `Worklog.Version` optimistic concurrency token;
  - `InvoiceLine.WorklogId` as a primary key, so a worklog can be invoiced at most once;
  - the `IdempotencyRecord.Key` primary key.
- **Optimistic concurrency uses a version in the request body, not ETag/If-Match.** This keeps every mutating endpoint the same shape (docs/03). A stale version gets 409.
- **Idempotency.** The key is looked up inside the invoice transaction, and the record is committed atomically with the invoice.
  - Same key, same project: replays the original invoice.
  - Same key, different project: 409.
  - Concurrent same-key requests: exactly one invoice.
  - A failed attempt stores nothing.
  - Idempotency is kept separate from business uniqueness: the `WorklogId` primary key prevents double invoicing whatever key is used.
- **Money.** `decimal` throughout. Each amount is rounded to 2 decimals, half away from zero. Rate, role and multiplier are snapshotted on each invoice line, so later rate changes cannot rewrite history.
- **Overtime policy.** The 8h/day normal-time ceiling applies per worker across all projects, on a "first-invoiced consumes normal time" basis. Within a batch, worklogs are ordered by (date, id). This is a documented assumption; a real migration would recover the rule from legacy (docs/01).
- **The invoice calculator is a seam (`IInvoiceCalculator`).** `InvoiceService` validates whichever calculator it is given before persisting anything. There must be exactly one line per eligible worklog, hours must add up, inputs must match, and totals must be consistent. A violation is a 500 with nothing written.
- **Legacy adapter plus differential harness.** `LegacyInvoiceCalculator` stands in for the stored-procedure adapter; its body is simulated. The harness in `tests/.../Differential/` compares normalized results field by field and prints `Worklog / Field / Legacy / Modern`. Each fixture must list its expected differences with a classification (`ModernRegression`, `IntentionalChange`, `LegacyDefect`, `NormalizationIssue`). The test fails on any unlisted difference, and also when a listed one disappears. Nothing is filtered out inside the comparator.
- **Auth.** JWT bearer, with one policy per capability (`worklog:approve`, …) instead of role checks. Ownership is checked after the resource is loaded. Create derives the worker from the token's `sub` claim, so it can't be spoofed.
- **Kept lean on purpose.** No repositories, MediatR, migrations or messaging. EF Core is used directly.

## Known limitations

- **The legacy behavior is simulated.** Passing differential fixtures prove the harness works, not that the modern calculator matches the real stored procedure. The one classified difference is a deliberately simulated legacy defect: the ceiling applies within the batch only.
- **The concurrency story is specific to SQLite.** `BEGIN IMMEDIATE` serializes writers on one file on one host. Postgres or SQL Server at READ COMMITTED would need a per-(worker, date) lock or `SERIALIZABLE` with retry (documented in docs/04, not implemented). Busy waits are a synchronous 150 ms retry loop inside Microsoft.Data.Sqlite.
- **Invoicing order affects billing.** Under "first-invoiced consumes normal time", a worker's split between normal and overtime hours across projects depends on which invoice runs first. That is a domain decision still open with the business (reviewer finding I2).
- **Auth is POC-only.** `/dev/token` issues any token without credentials, and the JWT key lives only in `appsettings.Development.json`. Idempotency keys are global rather than scoped per caller. Any authenticated caller can read any worklog.
- **The container runs as root in Development**, so that Swagger and `/dev/token` are available.
- **No migrations** (`EnsureCreated`), no pagination or listing of worklogs, and no invoice status or numbering.
- **Decimal scale in JSON is not normalized.** For example `100.0` versus `100.00`, a side effect of SQLite TEXT decimal round-trips. Values are exact, but a client that needs 2-decimal display should format them.
- **Open minor review items:** rate scale and upper bound are not validated (M3, M5), and the query for prior invoice lines is not bounded by date (M2).

## AI usage and verification

AI (Claude Code) did the implementation. The main session acted as orchestrator over three agents: `developer`, `test-writer` and `senior-reviewer`. Their definitions are in `.claude/agents/`. `AI_LOG.md` is an append-only log of each interaction: the prompt, the output, what the human accepted or rejected, and the actual verification results, including failures.

How AI output was verified, not just trusted:
- **Build and test gates** after every step: 0 warnings, all tests green.
- **Tests from a separate agent.** Tests were derived from the docs by a separate test-writer agent, not copied from the implementation. The exception is AI-020/021: the agent hit a spend limit and the orchestrator wrote those tests itself, which the log records.
- **Mutation checks.** Code was deliberately broken to prove the tests catch it: rounding mode, a removed transaction, skipped output validation, a skipped idempotency lookup, and legacy and modern calculator changes. Results are in the log, including one weak probabilistic race test.
- **Real SQLite files and concurrent connections** for the concurrency claims, never an in-memory fake. Claims about library behavior (BEGIN IMMEDIATE, busy handling, foreign keys) were checked by decompiling the actual package versions.
- **Reviewer findings were checked against the code before acting.** Some were fixed: I1 (calculator output validation) and M1 (constraint codes). Some are only documented: I2, M2, M3 and M5.
- **Bugs AI introduced and tests caught are logged**, for example ProblemDetails being sent with the wrong content type (AI-021).

## What I would do next

1. **Settle the overtime-allocation policy** (I2) with domain experts or legacy evidence, then encode it in fixtures.
2. **Connect a real legacy database.** Implement the adapter as an `EXEC` of the procedure with a table-valued parameter. Keep the fixtures and add recorded production samples. Run it in **shadow mode**: execute both calculators on live invoice requests and log the classified differences before cutting over.
3. **Move to Postgres or SQL Server** with migrations. Add a `WorkerDay` row locked `FOR UPDATE` (or `SERIALIZABLE` with retry) for the daily-hours and overtime-ceiling races, and re-run the concurrency tests against it.
4. **Production auth:** an external IdP, keys from a secret store, idempotency keys scoped per caller, and a `worklog:read` permission with an ownership or approver check.
5. **Validate rate scale and bounds** (M3/M5), return explicitly formatted money in the API, and add worklog listing and filtering.
6. **Harden the container:** a non-root user, a Production profile, and health checks.
