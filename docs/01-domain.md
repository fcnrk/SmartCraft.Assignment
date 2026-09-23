# Domain Model and Rules

## Domain purpose
The POC models project-based work reporting and invoice drafting. Workers record time against projects; submitted work is approved; approved work can then be consumed by invoice generation.

## Core concepts

### Worker
Represents a person who performs work.

Suggested data:
- `WorkerId`
- `Name`
- active/inactive flag only if needed

Do not put a global billing rate on Worker. Billing terms are contextual to a project assignment.

### Project
Represents a billable project and owns its worker assignments/billing terms.

Suggested data:
- `ProjectId`
- `Name`
- `Status`
- project assignments

### ProjectAssignment
Represents a worker's billing context within a project.

Suggested data:
- `WorkerId`
- `WorkerRole`
- `HourlyRate`

A worker can have different roles/rates on different projects.

### Worklog
Aggregate root representing a worker's reported work.

Suggested data:
- `WorklogId`
- `WorkerId`
- `ProjectId`
- `WorkDate`
- `Hours`
- `Description`
- `Status`
- `InvoiceId?`
- concurrency/version token

Lifecycle:

`Draft -> Submitted -> Approved -> Invoiced`

Expose explicit behavior such as:
- `Update(...)`
- `Submit()`
- `Approve()`
- `MarkInvoiced(invoiceId)`

Do not expose a generic public `SetStatus` operation.

### Invoice
Aggregate root representing a historical invoice draft/result generated from approved worklogs for one project.

Suggested data:
- `InvoiceId`
- `ProjectId`
- `Status`
- `CreatedAt`
- invoice lines
- totals
- concurrency/version token if useful

### InvoiceLine
A historical snapshot of billing calculation inputs/results. Suggested fields:
- `WorklogId`
- `WorkerId`
- role
- normal hours
- overtime hours
- hourly rate
- overtime multiplier
- normal amount
- overtime amount
- line total

Rates/calculation inputs are snapshotted so later assignment-rate changes cannot rewrite historical invoices.

## Rules
1. A worker may record time only against a project to which the worker is assigned.
2. Worklog hours must be greater than zero and no greater than 24.
3. Total recorded hours for a worker on one calendar date must not exceed 24.
4. Only Draft worklogs can be edited.
5. Only Draft worklogs can be submitted.
6. Only Submitted worklogs can be approved.
7. Only Approved worklogs can be invoiced.
8. Submitted, Approved and Invoiced work content is immutable.
9. A worklog can be associated with at most one invoice.
10. An invoice belongs to exactly one project.
11. Only approved, uninvoiced worklogs for that project may be selected for invoice generation.
12. Billing rate comes from the worker's assignment to the project.
13. First 8 hours worked by a worker on a calendar day are normal time.
14. Hours beyond 8 are overtime at a default 1.5 multiplier.
15. Monetary arithmetic uses decimal semantics and an explicit rounding policy.
16. Invoice creation and claiming/marking its worklogs must be atomic.
17. Historical invoice calculation data must not depend on mutable current rates.

## Important legacy-discovery assumption: overtime allocation
If a worker records time across multiple worklogs or projects on the same day, which specific hours become overtime is a domain policy that cannot safely be invented during a real migration.

For the POC, choose and document a deterministic policy (for example chronological worklog ordering with a stable tie-breaker). Differential tests should make this policy visible. In a real migration, recover and verify the rule from legacy behavior/domain experts.

### POC decision (iteration 3)

- **Scope of the 8h/day ceiling: per worker, per calendar day, ACROSS ALL PROJECTS**, not per
  project. A worker's total normal-time capacity for a date is 8 hours regardless of how many
  projects that time is split across.
- **Consumption policy: "first-invoiced consumes normal time".** Remaining normal capacity for
  a (worker, date) at the moment an invoice is generated = 8 − sum(NormalHours already
  snapshotted on existing `InvoiceLine`s for that worker/date, any project). Draft, Submitted,
  or Approved-but-not-yet-invoiced worklogs on other projects do **not** reserve normal time —
  only worklogs that have actually been invoiced (and therefore have a snapshotted
  `InvoiceLine`) count against the ceiling. This means the split for a given worklog can depend
  on invoice-creation order across projects, which is the explicit trade-off of this policy —
  see `docs/04-concurrency-idempotency-auth.md` "Scenario: competing invoice requests" for the
  concurrency-correctness ceiling this implies.
- **Deterministic allocation order within one invoice run:** worklogs for a given (worker,
  date) are allocated in `(WorkDate, Worklog.Id)` order — `WorkDate` first (irrelevant within
  one worker/date group, but keeps the whole batch's processing order stable/explainable), then
  `Worklog.Id` as a stable tie-breaker. ponytail: `Worklog.Id` is an arbitrary-but-stable
  tie-breaker, not legacy's real chronological order — `Worklog` has no creation timestamp
  today. A real migration must recover and verify legacy's actual ordering rule before this can
  be trusted as behaviorally equivalent; this POC's differential harness (future work, see
  `docs/05-testing-and-differential.md`) is where that gap would surface as a mismatch.
- **A single worklog can split into normal and overtime hours on the same line** (rule 14): the
  remaining-normal-capacity check runs per worklog in the order above, consuming from the day's
  remaining capacity until it is exhausted, then the rest of that worklog's hours become
  overtime.
- **Multiplier:** 1.5, a constant, snapshotted onto every `InvoiceLine.OvertimeMultiplier` (not
  read live at invoice-view time) so it stays historically accurate even if the constant later
  becomes configurable.
- **Rounding (rule 15):** `NormalAmount = round(NormalHours * HourlyRate, 2,
  MidpointRounding.AwayFromZero)`, `OvertimeAmount = round(OvertimeHours * HourlyRate * 1.5, 2,
  MidpointRounding.AwayFromZero)` — each amount rounded independently, not the pre-rounded
  hourly components — then `LineTotal = NormalAmount + OvertimeAmount` (sum of the two already-
  rounded amounts) and `Invoice.Total = sum(LineTotal)`. `decimal` throughout; `double` is never
  used for money.
- **Implementation seam:** this policy lives in `Application/ModernInvoiceCalculator.cs`, a pure
  function (no DB access) implementing `IInvoiceCalculator` — see
  `docs/02-architecture.md` "Legacy modernization seam".

## Entity vs cross-aggregate invariants
A Worklog can enforce `0 < Hours <= 24` itself.

It cannot independently enforce `sum(worker/date hours) <= 24`; that requires database state and belongs in transactional application logic.

Likewise, invoice generation coordinates Project assignments, Worklogs, Invoice creation, idempotency, and persistence. Do not force this orchestration into one entity merely to claim that all rules are 'in the domain'.
