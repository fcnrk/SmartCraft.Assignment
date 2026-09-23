# Testing and Differential Verification Strategy

## Philosophy

Tests should demonstrate business correctness and provide deterministic feedback for AI-assisted development. Prioritize meaningful behavioral tests over raw coverage percentage.

## Unit tests

Cover domain behavior including:

- hours > 0 and <= 24
- Draft can be updated
- Submitted/Approved/Invoiced cannot be edited
- Draft -> Submitted
- Submitted -> Approved
- Approved -> Invoiced
- invalid lifecycle transitions fail
- normal/overtime split around 8-hour boundary
- worker/project rate selection
- overtime multiplier
- decimal/rounding behavior
- invoice totals
- invoice snapshots billing inputs

Ask AI to generate adversarial/boundary scenarios, then review them for relevance rather than accepting all generated tests.

## Integration tests

Prioritize behavior requiring a real persistence boundary:

- create/retrieve/update API behavior
- authn/authz behavior
- optimistic concurrency conflict
- daily-hours invariant
- invoice creation transaction
- two attempts to invoice the same worklogs
- same idempotency key repeated
- same idempotency key with different payload
- concurrent same-key attempts if practical

Use a database/testing strategy that actually exercises the semantics being claimed. Avoid using a fake provider to 'prove' transaction/concurrency behavior it does not implement.

## Differential invoice tests

The modernization-specific test harness runs equivalent inputs through legacy and modern invoice generators.

Conceptual flow:

```text
Fixture/input
  |--------------------> Legacy generator ----> normalized snapshot
  |
  +--------------------> Modern generator ----> normalized snapshot
                                              |
                                              v
                                           compare
```

The legacy side may be a realistic adapter/fake/stub of the stored procedure for this POC if an actual legacy DB is unavailable. Be explicit about this limitation: the architecture demonstrates how a real stored procedure would be plugged in, but fabricated legacy behavior is not evidence of real compatibility.

## Normalized comparison model

Compare business-significant fields, e.g.:

- project
- consumed Worklog IDs
- worker
- role if business-significant
- normal hours
- overtime hours
- hourly rate
- overtime multiplier
- line amounts
- total

Ignore implementation noise such as newly generated IDs/timestamps unless legacy compatibility requires them.

## Useful diagnostics

Do not reduce differential verification to a boolean. A mismatch should identify paths/values, for example:

```text
Invoice mismatch
Worklog: WL-42
Field: OvertimeHours
Legacy: 2.0
Modern: 0.0
```

## Difference classification

A differential mismatch is evidence requiring investigation, not automatic proof that modern code is wrong.

Classify differences as:

1. modern regression
2. intentional behavior change
3. legacy defect
4. normalization/test-data issue

Never silently change the comparator to make a failing difference disappear.

## POC decision (iteration 4)

- **Harness:** `tests/SmartCraft.Assignment.Tests/Differential/InvoiceDifferentialTests.cs`.
  Each fixture runs the same `WorklogBillingInput`s and prior-consumed normal hours through
  `LegacyInvoiceCalculator` and `ModernInvoiceCalculator`.
  - Normalization matches lines by WorklogId and compares every business field plus the
    invoice total by decimal value, so `1.5` and `1.50` are equal.
  - A worklog consumed by only one side is reported as `ConsumedWorklog`.
- **Classification is data, not a comparator filter.** Each fixture lists its expected
  differences as `(WorklogId, Field, Classification, Reason)`.
  - The test fails on any unlisted difference, and prints `Worklog / Field / Legacy / Modern`.
  - It also fails when a listed difference stops occurring, so a classification can't go stale.
- **Fixtures:**
  - under 8h;
  - exactly 8h;
  - overtime split across worklogs on the same day;
  - separate days;
  - a half-cent midpoint (0.5h × 20.25 = 10.125 → 10.13);
  - normal time already consumed by an earlier invoice.
- **The one classified difference is `LegacyDefect`.** The simulated procedure applies the
  8h/day ceiling only within the current batch. The modern calculator applies rule 13 across
  invoices.
- **Limitation:** the legacy behavior is fabricated, so passing fixtures prove nothing about
  real compatibility. With a real procedure, the adapter body becomes an `EXEC`, and the
  fixtures and comparator stay the same. The planned next step is a shadow mode that runs both
  calculators on real invoice requests and logs the classified diffs. It is not built.

## AI verification loop

1. human defines/clarifies rule
2. AI proposes implementation/tests
3. human reviews
4. build/tests execute
5. AI may analyze failures
6. human decides correction
7. rerun deterministic verification
8. log the interaction and outcome in `AI_LOG.md`

Use AI as a reviewer at least once with an explicit request to find concurrency, transaction, authorization, idempotency and money-calculation defects rather than style issues.
