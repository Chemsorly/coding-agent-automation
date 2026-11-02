using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E browser tests that verify the Run page renders the correct diagnosis card and pod-log-tail
/// card after the reconciliation loop processes a failed K8s Job.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "JobController")]
[Collection(E2ECollection.Name)]
public sealed class JobFailureRunPageTests : E2ETestBase
{
    public JobFailureRunPageTests(E2EFixture fixture) : base(fixture) { }

    // ═══════════════════════════════════════════════════════════════════════
    // J3 — OOM diagnosis and pod log on the Run page
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An OOMKilled job reconciled to Failed/ExitCodeFailure shows "The agent ran out of memory"
    /// on the Run page with verdict "Configuration" and the pod log tail.
    /// </summary>
    [Fact]
    public async Task WhenAnOomKilledJobIsReconciled_ShouldShowTheDiagnosisAndPodLogOnTheRunPage()
    {
        // ── Arrange ───────────────────────────────────────────────────────────────────
        var (id, jobName) = await JobReconciliationTestData.SeedRunningItemWithJobAsync(
            Fixture, "jc-oom-page-1");

        var podName = $"{jobName}-abc12";
        Fixture.K8sClient.AddTerminatedPod(jobName, podName,
            exitCode: 137, reason: "OOMKilled",
            logTail: "Starting agent\nKilled process 42");
        await Fixture.K8sClient.SimulateWorkItemJobFailedAsync(jobName,
            reason: "BackoffLimitExceeded",
            message: "Job has reached the specified backoff limit");

        // ── Act: reconcile ────────────────────────────────────────────────────────────
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // ── Wait: DB row Failed AND run summary written to HistoryService ─────────────
        // FailRunWithLabelAsync writes the run summary (FailureCategory, PodLogTail) before
        // the HTTP response returns, so by the time ReconcileOnceAsync returns both are done.
        // Poll defensively to avoid a race if the API processes the post asynchronously.
        await WaitUntilAsync(
            async () =>
            {
                await using var db = Fixture.DbContextFactory.CreateDbContext();
                var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
                if (item?.Status != WorkItemStatus.Failed)
                    return false;
                var runs = await Fixture.HistoryService.GetRunHistoryAsync();
                return runs.Any(r => r.RunId == id.ToString());
            },
            timeout: TimeSpan.FromSeconds(15));

        // ── Navigate to Run page ──────────────────────────────────────────────────────
        await Page.GotoCockpitPageAsync($"{BaseUrl}/runs/{id}");
        await Page.WaitForSelectorAsync(
            "[data-testid='run-page'][data-initialized='true']",
            new() { Timeout = 15_000 });

        // ── Assert: diagnosis card ────────────────────────────────────────────────────
        // RunDiagnoser Rule 2: ExitCodeFailure + FailureReason contains "OOMKilled"
        // → verdict Configuration, title "The agent ran out of memory".
        await Assertions.Expect(Page.Locator("[data-testid=diagnosis-title]"))
            .ToHaveTextAsync("The agent ran out of memory");
        await Assertions.Expect(Page.Locator("[data-testid=diagnosis-verdict]"))
            .ToHaveTextAsync("Configuration");

        // ── Assert: pod log tail card ─────────────────────────────────────────────────
        await Assertions.Expect(Page.Locator("[data-testid=pod-log-tail-card]"))
            .ToContainTextAsync("Killed process 42");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // J9 — Deadline diagnosis on the Run page
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A deadline-exceeded job reconciled to Failed/Timeout shows "The run hit the agent timeout"
    /// on the Run page. No pod log tail card is rendered (DeadlineExceeded returns no pod log).
    /// </summary>
    [Fact]
    public async Task WhenTheJobHitsItsDeadline_ShouldShowTheTimeoutDiagnosis()
    {
        // ── Arrange ───────────────────────────────────────────────────────────────────
        var (id, jobName) = await JobReconciliationTestData.SeedRunningItemWithJobAsync(
            Fixture, "jc-deadline-page-1");

        await Fixture.K8sClient.SimulateWorkItemJobFailedAsync(jobName,
            reason: "DeadlineExceeded",
            message: "Job was active longer than specified deadline");

        // ── Act: reconcile ────────────────────────────────────────────────────────────
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // ── Wait: DB row Failed AND run summary written to HistoryService ─────────────
        await WaitUntilAsync(
            async () =>
            {
                await using var db = Fixture.DbContextFactory.CreateDbContext();
                var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
                if (item?.Status != WorkItemStatus.Failed)
                    return false;
                var runs = await Fixture.HistoryService.GetRunHistoryAsync();
                return runs.Any(r => r.RunId == id.ToString());
            },
            timeout: TimeSpan.FromSeconds(15));

        // ── Navigate to Run page ──────────────────────────────────────────────────────
        await Page.GotoCockpitPageAsync($"{BaseUrl}/runs/{id}");
        await Page.WaitForSelectorAsync(
            "[data-testid='run-page'][data-initialized='true']",
            new() { Timeout = 15_000 });

        // ── Assert: diagnosis title ───────────────────────────────────────────────────
        // RunDiagnoser Rule 4: Timeout → title "The run hit the agent timeout".
        await Assertions.Expect(Page.Locator("[data-testid=diagnosis-title]"))
            .ToHaveTextAsync("The run hit the agent timeout");

        // ── Assert: no pod log tail card ──────────────────────────────────────────────
        // DeadlineExceeded returns podLogTail = null, so the card is not rendered.
        Assert.Equal(0, await Page.Locator("[data-testid=pod-log-tail-card]").CountAsync());
    }
}
