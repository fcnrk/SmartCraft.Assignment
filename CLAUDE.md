# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# Legacy Modernization POC

## Mission

Build a small .NET 10 backend POC that demonstrates safe incremental modernization of a legacy project/time-reporting/invoicing system.

The legacy system persists projects, workers and worklogs in a relational database. Invoice generation is assumed to be implemented by a long-lived stored procedure containing accumulated business rules. The POC introduces a modern C# invoice implementation while preserving a seam to execute/represent the legacy implementation and compare behavior.

The exercise prioritizes engineering judgment over feature count: explicit domain rules, transactional correctness, concurrency, idempotency, authentication/authorization, automated tests, differential verification, and disciplined AI-assisted development.

## Repository state and commands

`SmartCraft.Assignment.slnx` (net10.0) references `src/SmartCraft.Assignment.Api/` and `tests/SmartCraft.Assignment.Tests/` (xUnit).

- `Domain/`: Worklog, Worker, Project, ProjectAssignment, Invoice, InvoiceLine.
- `Application/`:
  - `WorklogService` and `InvoiceService`;
  - the `IInvoiceCalculator` seam, implemented by `ModernInvoiceCalculator`.
- `Infrastructure/`:
  - `AppDbContext`, `IdempotencyRecord` and `SeedData`;
  - `LegacyInvoiceCalculator`: a simulated stored-procedure adapter, used by tests only.
- `Authorization/`: capability permissions and policies.
- `Endpoints/`: Minimal APIs, `DomainExceptionHandler`, and `/dev/token` (Development only).
- Tests:
  - `Domain/`: unit tests;
  - `Integration/`: real SQLite files, concurrency tests;
  - `Api/`: WebApplicationFactory;
  - `Differential/`: legacy vs modern.

DB: SQLite via EF Core, schema via `EnsureCreated` (no migrations), seeded on startup — see `docs/02-architecture.md` Database section. Auth: JWT bearer with one policy per permission. Swagger at `/swagger` in Development. See `README.md` for API usage.

- Build: `dotnet build`
- All tests: `dotnet test`
- Single test: `dotnet test --filter "FullyQualifiedName~<TestName>"`
- Run API: `dotnet run --project src/SmartCraft.Assignment.Api`
- Differential tests only: `dotnet test --filter "FullyQualifiedName~Differential"`
- Docker: `docker compose up --build` (or `docker build -t smartcraft-assignment .` then `docker run --rm -p 8080:8080 smartcraft-assignment`)

## Scope discipline

Prefer the smallest complete implementation that demonstrates the important decisions. Do not add architectural layers, libraries, infrastructure, messaging, microservices, or abstractions without a concrete reason.

If an important production concern cannot be implemented within the timebox, document the limitation and the production approach instead of hiding it behind a superficial implementation.

## Read these first

Before implementation, read:

- `docs/01-domain.md`
- `docs/02-architecture.md`
- `docs/03-api-transactions.md`
- `docs/04-concurrency-idempotency-auth.md`
- `docs/05-testing-and-differential.md`
- `docs/06-implementation-plan.md`

Treat these as the current design baseline. If implementation reveals a contradiction or a better design, call it out and update the relevant document rather than silently diverging.

## Core workflow

Worklog lifecycle:

`Draft -> Submitted -> Approved -> Invoiced`

Invoice modernization workflow:

`Approved worklogs -> Legacy invoice generator + Modern invoice generator -> normalized snapshots -> differential comparison`

The legacy implementation is a compatibility oracle, not an unquestionable source of truth. Differences must be surfaced and classified, never silently normalized away.

## Engineering principles

- Preserve business knowledge, not legacy implementation structure.
- Keep domain invariants close to the domain model.
- Keep cross-aggregate/database invariants in transactional application logic.
- Make state transitions explicit domain operations; do not expose generic status setters.
- Use decimal/money-safe arithmetic; never floating point for monetary values.
- Historical invoices snapshot billing inputs so later rate changes cannot mutate history.
- Assume multiple API instances and concurrent users. In-process locks are not correctness mechanisms.
- Let database constraints and transaction semantics participate in correctness.
- Prefer optimistic concurrency for lost-update protection where appropriate.
- Treat idempotency and business-resource concurrency as separate problems.
- EF Core may be used directly when a repository abstraction adds no value.
- Design authorization around capabilities/actions rather than scattered role-name checks.

## AI-assisted development policy

AI is part of the engineering workflow, not an authority. Use it for implementation acceleration, code review, edge-case discovery, test generation, concurrency review, differential-test design, refactoring and documentation.

Do not accept AI output merely because it compiles or looks plausible. Verify meaningful changes with deterministic evidence where possible: build, unit tests, integration tests, differential tests, static analysis, and manual inspection.

When reviewing code, actively search for correctness failures, especially:

- race conditions and lost updates
- incorrect transaction boundaries
- check-then-act idempotency races
- duplicate invoice creation
- worklogs being invoiced twice
- daily-hours races
- monetary precision/rounding errors
- overtime allocation errors
- invalid lifecycle transitions
- authorization bypasses
- accidental differences from legacy invoice behavior

## Development Agents

This project uses three focused agents:

- `developer` — implements scoped production changes.
- `test-writer` — independently derives and implements verification and adversarial tests.
- `senior-reviewer` — performs read-only senior-level review, with particular attention to correctness, concurrency, transactions, and idempotency.

The main Claude session acts as the orchestrator.

For significant features, prefer:

    Developer
        ↓
    Test Writer + Senior Reviewer
        ↓
    Human evaluation
        ↓
    Developer fixes
        ↓
    Final verification

Agents must not treat another agent's output as authoritative.

Tests should derive expected behavior from documented requirements rather than simply reproducing implementation behavior.

Reviewer findings must be validated against the actual code before changes are made.

All meaningful agent activity must be recorded in `AI_LOG.md` (repo root) according to the project's AI logging rules.

## Mandatory AI development log

Maintain `AI_LOG.md` (repo root) throughout the entire exercise. This is mandatory and should be committed with the solution.

For every meaningful AI interaction that affects analysis, design, implementation, testing, review, debugging, or documentation, append a log entry containing:

1. timestamp or sequence number
2. purpose/context
3. the user/developer prompt verbatim when reasonably short; otherwise a concise faithful prompt summary (do not record secrets)
4. a concise summary of the AI output/recommendation/action
5. files created or changed
6. verification performed and the actual result
7. what was accepted, rejected, or modified by the human and why
8. discovered issues, assumptions, or follow-up work

Do not rewrite previous entries to make the process look cleaner. The log is append-only except for correcting factual mistakes, which should be explicitly noted.

If a prompt produces no code change but materially affects a design decision, record it.

If AI-generated code fails tests or contains a flaw, record that fact and the correction. Failed approaches are useful evidence of verification.

Before finishing any meaningful task, ask: **Has this AI interaction been logged, and has its output been verified?**

Use `AI_LOG.md` as the canonical format/template.

## Definition of done for each implementation step

A step is not complete merely when code exists. At minimum:

- code compiles
- relevant tests pass
- important failure paths are considered
- concurrency/transaction implications are considered when applicable
- docs are updated if behavior/design changed
- AI_LOG entry exists for meaningful AI-assisted work

## Final verification

Before declaring the exercise complete:

1. restore/build from a clean state
2. run the full automated test suite
3. run representative API smoke tests
4. run differential invoice tests
5. inspect authentication/authorization behavior
6. inspect idempotency behavior, including concurrent/same-key scenarios if implemented
7. inspect optimistic concurrency behavior
8. summarize known limitations honestly in README
9. ensure `AI_LOG.md` accurately reflects AI usage and verification
