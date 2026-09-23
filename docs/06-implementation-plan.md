# Implementation Plan and Timebox

## Goal

Deliver a small, runnable, explainable service within roughly 3–4 hours. The order below intentionally puts business behavior and verification before optional polish.

## Phase 1 — Skeleton and domain

- create .NET 10 solution/API/test project
- configure persistence
- implement core domain types and lifecycle rules
- seed or otherwise establish Worker, Project and ProjectAssignment data
- establish basic error handling
- start `AI_LOG.md` immediately

## Phase 2 — Worklog flow

- create/get/update Worklog
- submit
- approve
- validation
- optimistic concurrency
- core unit tests
- authorization policies for worker/approver behavior

## Phase 3 — Invoice generation

- modern invoice calculation
- overtime/rate rules
- invoice snapshot lines
- transactional claiming of approved Worklogs
- invoice retrieval
- idempotency mechanism
- unit/integration tests for invoice behavior

## Phase 4 — Legacy seam + differential verification

- define invoice-generation seam
- legacy stored-procedure adapter shape (real or simulated depending available environment)
- normalized invoice snapshot/comparator
- representative differential fixtures
- readable mismatch diagnostics

## Phase 5 — Concurrency/adversarial verification + docs

- exercise lost update
- exercise competing invoice attempts
- exercise idempotency retry/race where feasible
- ask AI for adversarial review/test gaps
- fix high-value findings
- full build/test
- README: run instructions, decisions, trade-offs, known limitations, AI usage and verification
- review `AI_LOG.md` for accuracy

## Priority order if time runs short

1. runnable API and persistence
2. explicit Worklog lifecycle/domain rules
3. modern invoice generation
4. good unit tests
5. transaction preventing double invoicing
6. idempotency
7. differential-test seam/harness
8. optimistic concurrency integration test
9. auth/authz
10. additional polish

Auth is required by our intended POC design, but if implementation time becomes critical, prefer a small correct JWT/policy setup over a sophisticated identity solution.

## Deliberately out of scope

- customer/accounting domain
- VAT/tax engine
- payments
- invoice numbering/legal issuance
- payroll
- messaging/event bus
- distributed locks
- microservices
- elaborate CQRS/MediatR architecture
- generic repository/unit-of-work wrappers
- production identity provider
- observability stack
