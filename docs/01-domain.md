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

## Entity vs cross-aggregate invariants
A Worklog can enforce `0 < Hours <= 24` itself.

It cannot independently enforce `sum(worker/date hours) <= 24`; that requires database state and belongs in transactional application logic.

Likewise, invoice generation coordinates Project assignments, Worklogs, Invoice creation, idempotency, and persistence. Do not force this orchestration into one entity merely to claim that all rules are 'in the domain'.
