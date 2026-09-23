# API, Commands, and Transaction Boundaries

## Minimal API surface
Exact routes may change, but preserve business intent.

### Worklogs
- `POST /api/worklogs` — create Draft worklog
- `GET /api/worklogs/{id}` — retrieve worklog
- `PUT /api/worklogs/{id}` — update Draft worklog
- `POST /api/worklogs/{id}/submit` — Draft -> Submitted
- `POST /api/worklogs/{id}/approve` — Submitted -> Approved

### Invoices
- `POST /api/projects/{projectId}/invoices` — create invoice from eligible approved worklogs
- `GET /api/invoices/{id}` — retrieve invoice

Optional only if time permits:
- project/worker setup endpoints
- worklog filtering/listing

Seed data is acceptable for project/worker/assignment setup if it keeps focus on the important behavior.

## Commands/use cases
Useful conceptual commands:
- `CreateWorklog`
- `UpdateWorklog`
- `SubmitWorklog`
- `ApproveWorklog`
- `CreateDraftInvoice`

Do not make API DTOs the domain model.

## Transaction boundaries

### Create/update worklog
The daily-hours invariant requires querying other worklogs and persisting the new value. Consider isolation/concurrency explicitly; a naive read-sum-write sequence can race.

### Submit
Load the Worklog, validate current state/authorization, transition, save with optimistic concurrency.

### Approve
Load the Worklog, validate authorization and current state, transition, save with optimistic concurrency.

### Create invoice
This is the most important transaction:
1. establish/claim idempotency execution
2. load project and billing assignments
3. load eligible approved/uninvoiced worklogs
4. calculate invoice
5. persist invoice and lines
6. mark selected worklogs as invoiced/associated with invoice
7. complete idempotency record
8. commit atomically

No observer should be able to see an invoice successfully created while its worklogs remain available for another invoice.

## HTTP behavior
Use standard semantics where practical:
- `201 Created` for resource creation
- `200 OK`/`204 No Content` for successful updates/actions as chosen consistently
- `400 Bad Request` for malformed/invalid input
- `401 Unauthorized` for unauthenticated requests
- `403 Forbidden` for authenticated identities lacking permission
- `404 Not Found` where appropriate
- `409 Conflict` for lifecycle/concurrency/idempotency conflicts where the request is valid but conflicts with current state

Return useful ProblemDetails-style errors rather than raw exceptions.

## Optimistic concurrency contract
Clients updating mutable resources should provide a version/ETag/concurrency value. Do not silently overwrite a newer version.

The exact HTTP representation (ETag/If-Match vs version in DTO) can be chosen for scope, but document the trade-off.

### POC decision (endpoints iteration)

**Version in the request body** (`ExpectedVersion` on `UpdateWorklogRequest`/`TransitionRequest`),
not `If-Match`/ETag headers. Every `WorklogResponse` also returns the current `Version`, so a
client always has the value to send back. Trade-off: this is less HTTP-idiomatic than
`If-Match` (which would let a generic HTTP cache/proxy participate, and maps naturally to `412
Precondition Failed` instead of `409`), but it keeps the concurrency contract visible in the
same JSON body as the rest of the request/response instead of split across headers, and avoids
introducing `412` as a second "stale version" status code alongside the `409 Conflict` already
used for lifecycle conflicts and idempotency-key conflicts — one status code for "valid request,
current state disagrees" is simpler to document and to test. `WorklogService` already threw
`DomainErrorKind.Conflict` for a version mismatch before any endpoint existed; this is the
option that requires no new mapping.

## Idempotency-Key header

`POST /api/projects/{projectId}/invoices` requires an `Idempotency-Key` header. Missing, blank,
or longer than 200 characters -> `400 Bad Request`, checked at the endpoint before the request
reaches `InvoiceService` (docs/04 "Idempotency" has the full replay/conflict contract). First
successful creation for a key -> `201 Created`; a replay of an already-succeeded key -> `200 OK`
with the same invoice body (not `201` again, since nothing new was created).
