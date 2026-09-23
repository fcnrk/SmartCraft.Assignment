# SmartCraft.Assignment

A small .NET 10 POC demonstrating safe incremental modernization of a legacy project/time-
reporting/invoicing system — see `CLAUDE.md` and `docs/` for the full design brief and
decisions. This file is the short "how do I run it" entry point.

## Run locally

```
dotnet build
dotnet test
dotnet run --project src/SmartCraft.Assignment.Api
```

The API listens on the port printed at startup (see
`src/SmartCraft.Assignment.Api/Properties/launchSettings.json`), with Swagger UI at
`/swagger`. Schema is created via `EnsureCreated()` and seed data is inserted automatically on
first run (see "Seed data" below) — no separate setup step.

## Run with Docker

```
docker build -t smartcraft-assignment .
docker run --rm -p 8080:8080 smartcraft-assignment
```

Swagger UI: http://localhost:8080/swagger. The container runs with
`ASPNETCORE_ENVIRONMENT=Development` so Swagger and `POST /dev/token` are available — this is a
POC-only posture, see "Known limitations". The SQLite file lives at `/data/smartcraft.db`
inside the container; `docker-compose.yml` mounts a named volume there so data survives a
container restart:

```
docker compose up --build
```

## Getting a token

There is no real identity provider. `POST /dev/token` (Development only) mints a signed JWT for
any worker id/permission set you ask for:

```
curl -s -X POST http://localhost:5164/dev/token \
  -H "Content-Type: application/json" \
  -d '{"workerId":"11111111-1111-1111-1111-111111111111","permissions":["worklog:create","worklog:update-own","worklog:submit-own","project:read"]}'
```

Use the returned `access_token` as `Authorization: Bearer <token>`. Permission strings: see
`Authorization/Permissions.cs` — `worklog:create`, `worklog:update-own`, `worklog:submit-own`,
`worklog:approve`, `invoice:create`, `invoice:read`, `project:read`.

## Seed data

Fixed, documented ids (`Infrastructure/SeedData.cs`), seeded once at startup if the database is
empty:

| Role | WorkerId |
|---|---|
| Alice — Developer, assigned to Project Phoenix at 100/hr | `11111111-1111-1111-1111-111111111111` |
| Bob — Tester, assigned to Project Phoenix at 80/hr | `22222222-2222-2222-2222-222222222222` |
| Carol — approver/billing (no project assignment needed) | `33333333-3333-3333-3333-333333333333` |
| Project Phoenix | `44444444-4444-4444-4444-444444444444` |

## Example flow

1. `POST /dev/token` for Alice with `worklog:create,worklog:update-own,worklog:submit-own`.
2. `POST /api/worklogs` as Alice — create a Draft worklog on Project Phoenix.
3. `POST /api/worklogs/{id}/submit` as Alice.
4. `POST /dev/token` for Carol with `worklog:approve,invoice:create,invoice:read`.
5. `POST /api/worklogs/{id}/approve` as Carol (approving your own worklog is `403 Forbidden` —
   Alice cannot approve her own worklog).
6. `POST /api/projects/{projectId}/invoices` as Carol, with an `Idempotency-Key` header. Repeat
   the exact same request (same key) — you get `200 OK` with the same invoice id back, not a
   second invoice.
7. `GET /api/invoices/{id}` as Carol.

## Known limitations

- **Auth is POC-only.** `POST /dev/token` mints tokens for any worker/permission combination
  with no credential check — an explicit stand-in for an identity provider, not a security
  control (docs/04-concurrency-idempotency-auth.md "Authentication"). It only exists in
  `ASPNETCORE_ENVIRONMENT=Development`, which is also the only environment with a configured
  `Jwt:Key` — this POC has no token issuance path for any other environment.
- **SQLite single-writer concurrency is not a portable technique.** The daily-hours and
  invoice-creation correctness stories both rely on SQLite's `BEGIN IMMEDIATE` fully serializing
  writers on one file (docs/04, "daily-hours race" and "competing invoice requests"). On
  Postgres/SQL Server at READ COMMITTED, the same code would need an explicit per-(worker,date)
  lock or `SERIALIZABLE` + retry — documented in-place, not implemented, to keep scope tight.
- **No legacy stored-procedure adapter / differential harness yet.** `IInvoiceCalculator` is the
  seam (`docs/02-architecture.md` "Legacy modernization seam"); only the modern implementation
  exists. Phase 4 of `docs/06-implementation-plan.md`.
- **No migrations.** Schema is created via `Database.EnsureCreated()`, not `dotnet ef
  migrations` — acceptable for a POC, not for production schema evolution.
- **No `worklog:read` permission.** `GET /api/worklogs/{id}` only requires *some* authenticated
  identity, not a specific capability — see docs/04 "Authorization" POC decision.
- **Idempotency records nothing on a failed attempt.** A retry with the same key after a failed
  request (e.g. "no eligible worklogs") runs fresh rather than replaying the failure — documented
  in docs/04 "Idempotency", not an oversight.
- **Docker image runs as root** (the aspnet base image's default non-root `app` user was not
  configured for the `/data` volume in this POC) and uses `ASPNETCORE_ENVIRONMENT=Development`
  — both acceptable for a local/POC container, not for a production image.

## AI usage

See `AI_LOG.md` for the full, append-only log of AI-assisted work, decisions, and verification
across the exercise.
