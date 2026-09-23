using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Tests.Integration;

/// <summary>
/// docs/04-concurrency-idempotency-auth.md "Scenario: competing invoice requests" and the
/// "overtime allocation" cross-project consumption note: two genuinely concurrent
/// <see cref="InvoiceService.CreateInvoiceAsync"/> calls, each on its own
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/>/connection, exactly like two API
/// instances. Iterated a handful of times per scenario (not just once) since a race that
/// passes on the first attempt is not proof of correctness.
/// </summary>
public class InvoiceServiceConcurrencyTests
{
    private readonly record struct InvoiceAttemptResult(bool Success, DomainErrorKind? FailureKind, Invoice? Invoice);

    private static async Task<InvoiceAttemptResult> AttemptAsync(Func<Task<InvoiceCreationResult>> action)
    {
        try
        {
            var result = await action();
            return new InvoiceAttemptResult(true, null, result.Invoice);
        }
        catch (DomainException ex)
        {
            return new InvoiceAttemptResult(false, ex.Kind, null);
        }
    }

    [Fact]
    public async Task Two_concurrent_invoice_attempts_for_the_same_project_let_exactly_one_succeed_and_each_worklog_is_invoiced_exactly_once()
    {
        for (var iteration = 0; iteration < 5; iteration++)
        {
            using var fx = new SqliteWorklogFixture();
            var worklogService = fx.NewService();

            var w1 = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, new DateOnly(2026, 9, 23), 3m, "w1"));
            var w1Submitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(w1.Id, w1.Version));
            await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(w1.Id, w1Submitted.Version));

            var w2 = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, new DateOnly(2026, 9, 24), 4m, "w2"));
            var w2Submitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(w2.Id, w2.Version));
            await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(w2.Id, w2Submitted.Version));

            var serviceA = fx.NewInvoiceService();
            var serviceB = fx.NewInvoiceService();
            var gate = new TaskCompletionSource();

            var taskA = Task.Run(async () =>
            {
                await gate.Task;
                return await AttemptAsync(() => serviceA.CreateInvoiceAsync(fx.ProjectId, $"key-a-{iteration}"));
            });
            var taskB = Task.Run(async () =>
            {
                await gate.Task;
                return await AttemptAsync(() => serviceB.CreateInvoiceAsync(fx.ProjectId, $"key-b-{iteration}"));
            });

            gate.SetResult();
            var results = await Task.WhenAll(taskA, taskB);

            Assert.True(
                results.Count(r => r.Success) == 1,
                $"iteration {iteration}: expected exactly one winner, got {results.Count(r => r.Success)}");
            var loser = results.Single(r => !r.Success);
            // SQLite's BEGIN IMMEDIATE fully serializes the two attempts (docs/04), so the
            // loser's own SELECT for eligible worklogs runs strictly after the winner's commit
            // and always finds none left -> Validation, not a Conflict from a claimed-out-from-
            // under-it worklog. Both are accepted as a "did not double-invoice" outcome per the
            // task brief; asserting the specific kind documents what this POC's SQLite
            // serialization actually produces.
            Assert.True(
                loser.FailureKind is DomainErrorKind.Validation or DomainErrorKind.Conflict,
                $"iteration {iteration}: unexpected failure kind {loser.FailureKind}");

            var winner = results.Single(r => r.Success).Invoice!;
            Assert.Equal(2, winner.Lines.Count);

            using var freshDb = fx.CreateContext();
            Assert.Equal(1, await freshDb.Invoices.CountAsync());

            var reloadedW1 = await freshDb.Worklogs.SingleAsync(w => w.Id == w1.Id);
            var reloadedW2 = await freshDb.Worklogs.SingleAsync(w => w.Id == w2.Id);
            Assert.Equal(WorklogStatus.Invoiced, reloadedW1.Status);
            Assert.Equal(WorklogStatus.Invoiced, reloadedW2.Status);
            Assert.Equal(winner.Id, reloadedW1.InvoiceId);
            Assert.Equal(winner.Id, reloadedW2.InvoiceId);
        }
    }

    [Fact]
    public async Task Concurrent_invoices_for_two_different_projects_sharing_a_worker_day_never_exceed_8_normal_hours_combined()
    {
        for (var iteration = 0; iteration < 5; iteration++)
        {
            using var fx = new SqliteWorklogFixture();
            var worklogService = fx.NewService();
            var day = new DateOnly(2026, 9, 23);

            // Same worker, same calendar day, two different projects: 5h + 5h = 10h total,
            // more than the worker's 8h/day normal-time ceiling across all projects.
            var alphaWork = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, day, 5m, "alpha"));
            var alphaSubmitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(alphaWork.Id, alphaWork.Version));
            await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(alphaWork.Id, alphaSubmitted.Version));

            var betaWork = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.SecondProjectId, day, 5m, "beta"));
            var betaSubmitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(betaWork.Id, betaWork.Version));
            await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(betaWork.Id, betaSubmitted.Version));

            var alphaInvoiceService = fx.NewInvoiceService();
            var betaInvoiceService = fx.NewInvoiceService();
            var gate = new TaskCompletionSource();

            var alphaTask = Task.Run(async () =>
            {
                await gate.Task;
                return await AttemptAsync(() => alphaInvoiceService.CreateInvoiceAsync(fx.ProjectId, $"alpha-key-{iteration}"));
            });
            var betaTask = Task.Run(async () =>
            {
                await gate.Task;
                return await AttemptAsync(() => betaInvoiceService.CreateInvoiceAsync(fx.SecondProjectId, $"beta-key-{iteration}"));
            });

            gate.SetResult();
            var results = await Task.WhenAll(alphaTask, betaTask);

            Assert.All(results, r => Assert.True(r.Success, $"iteration {iteration}: expected both to succeed (different projects, no shared worklog)"));

            var combinedNormalHours = results.Sum(r => r.Invoice!.Lines.Sum(l => l.NormalHours));
            var combinedOvertimeHours = results.Sum(r => r.Invoice!.Lines.Sum(l => l.OvertimeHours));

            // The core invariant docs/04's "competing invoice requests" ceiling note is about:
            // this worker's combined normal-time allocation for this day, across both
            // concurrently-created invoices, must never exceed the 8h/day ceiling -- i.e.
            // neither transaction may have read the other's "prior normal hours" as zero and
            // both allocated a full normal share. SQLite's BEGIN IMMEDIATE serialization
            // (documented as the reason this holds here, unlike the Postgres/SQL Server gap
            // the same doc section describes) means this should hold exactly, not just as an
            // upper bound.
            Assert.True(
                combinedNormalHours <= 8m,
                $"iteration {iteration}: combined normal hours {combinedNormalHours} exceeded the 8h/day ceiling");
            Assert.Equal(8m, combinedNormalHours);
            Assert.Equal(2m, combinedOvertimeHours); // 10h total - 8h normal
        }
    }

    [Fact]
    public async Task A_create_invoice_attempt_blocked_by_another_open_write_transaction_surfaces_as_Conflict()
    {
        using var fx = new SqliteWorklogFixture(busyTimeoutSeconds: 1);
        var worklogService = fx.NewService();
        var day = new DateOnly(2026, 9, 23);
        var created = await worklogService.CreateWorklogAsync(new CreateWorklogCommand(fx.WorkerId, fx.ProjectId, day, 4m, "work"));
        var submitted = await worklogService.SubmitWorklogAsync(new SubmitWorklogCommand(created.Id, created.Version));
        await worklogService.ApproveWorklogAsync(new ApproveWorklogCommand(created.Id, submitted.Version));

        using var blockerDb = fx.CreateContext();
        await using var blockerTx = await blockerDb.Database.BeginTransactionAsync(); // RESERVED only, no write needed

        var invoiceService = fx.NewInvoiceService();
        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DomainException>(() => invoiceService.CreateInvoiceAsync(fx.ProjectId, "blocked-key"));
        stopwatch.Stop();

        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900),
            $"expected the blocked attempt to wait close to the 1s busy timeout before failing, actually took {stopwatch.Elapsed}");

        await blockerTx.RollbackAsync();

        using (var freshDb = fx.CreateContext())
        {
            Assert.Equal(0, await freshDb.Invoices.CountAsync());
            var reloaded = await freshDb.Worklogs.SingleAsync(w => w.Id == created.Id);
            Assert.Equal(WorklogStatus.Approved, reloaded.Status);
        }

        var retryService = fx.NewInvoiceService();
        // Same key as the failed attempt above: a failed attempt records nothing (docs/04
        // "Idempotency"), so this reruns fresh rather than replaying a failure.
        var retried = await retryService.CreateInvoiceAsync(fx.ProjectId, "blocked-key");
        Assert.False(retried.IsReplay);
        Assert.Single(retried.Invoice.Lines);
    }
}
