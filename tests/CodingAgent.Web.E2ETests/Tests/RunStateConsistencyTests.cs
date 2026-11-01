using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Verifies that <see cref="RunOutcomeDisplay"/> is applied consistently across every page that
/// shows run state: Overview, Work, Runs, the Run detail page, and Insights.
///
/// An earlier Insights fix (#2962) was silently reverted by a later merge (#2959) while CI
/// stayed green, because no browser-level test guarded the classification. This test seeds one
/// run of each outcome kind and asserts the same <see cref="RunOutcomeDisplay.Label"/> string
/// on every surface — so any future regression that changes or removes the shared classifier
/// will produce a clear test failure rather than silent data drift.
///
/// Acceptance criteria (issue #3090):
///   1. The test fails on a build without <c>RunOutcomeDisplay</c> (e.g. <c>9706d24</c>).
///   2. Page objects gain small helpers (Overview tiles, Runs badges) instead of inline selectors.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "RunStateConsistency")]
[Collection(E2ECollection.Name)]
public sealed class RunStateConsistencyTests : E2ETestBase
{
    public RunStateConsistencyTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunOutcomeDisplay_IsAppliedConsistentlyAcrossAllPages()
    {
        // ── Seed ──────────────────────────────────────────────────────────────
        //
        // Terminal runs: added to HistoryService so they appear in run history.
        // Running run: added to BOTH HistoryService (for GetRunAsync) and RunService
        //   (for the active-run merge path used by Overview and the Runs list).
        // Work items: written directly to the DB so the Work and Overview pages see them.

        var now = DateTimeOffset.UtcNow;

        // Issue identifiers used as unique row keys throughout the test.
        const string runningIssue = "rsc-running";
        const string conflictIssue = "rsc-conflict";
        const string completedIssue = "rsc-completed";
        const string mergedIssue = "rsc-merged";
        const string closedIssue = "rsc-closed";
        const string failedIssue = "rsc-failed";
        const string cancelledIssue = "rsc-cancelled";
        const string pendingIssue = "rsc-pending";

        // RunIds — need stable GUIDs so RunDetailPage can navigate to /runs/{id}.
        var runningRunId = Guid.NewGuid();
        var conflictRunId = Guid.NewGuid();
        var completedRunId = Guid.NewGuid();
        var mergedRunId = Guid.NewGuid();
        var closedRunId = Guid.NewGuid();
        var failedRunId = Guid.NewGuid();
        var cancelledRunId = Guid.NewGuid();

        // ── Seed terminal runs into history ───────────────────────────────────
        PipelineRunSummary MakeSummary(Guid id, string issueId, PipelineStep step) => new()
        {
            RunId = id.ToString(),
            IssueIdentifier = issueId,
            IssueTitle = $"RSC {issueId}",
            FinalStep = step,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = now,
        };

        await Fixture.HistoryService.AddRunSummaryAsync(MakeSummary(conflictRunId, conflictIssue, PipelineStep.ConflictRestart));
        await Fixture.HistoryService.AddRunSummaryAsync(MakeSummary(completedRunId, completedIssue, PipelineStep.Completed));
        await Fixture.HistoryService.AddRunSummaryAsync(MakeSummary(mergedRunId, mergedIssue, PipelineStep.PrMerged));
        await Fixture.HistoryService.AddRunSummaryAsync(MakeSummary(closedRunId, closedIssue, PipelineStep.PrClosed));
        await Fixture.HistoryService.AddRunSummaryAsync(MakeSummary(failedRunId, failedIssue, PipelineStep.Failed));
        await Fixture.HistoryService.AddRunSummaryAsync(MakeSummary(cancelledRunId, cancelledIssue, PipelineStep.Cancelled));

        // ── Seed the Running run into HistoryService AND RunService ───────────
        // HistoryService: required so RunHistory.GetRunAsync(runningRunId) succeeds on the RunPage.
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = runningRunId.ToString(),
            IssueIdentifier = runningIssue,
            IssueTitle = "RSC Running Issue",
            FinalStep = PipelineStep.GeneratingCode,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = now,
        });

        // RunService: required so Overview._activeCount and the Runs list active-merge path
        // see the run (the /api/pipeline-runs endpoint merges IOrchestratorRunService.GetActiveRuns
        // when includeActive=true).
        var activePipelineRun = new PipelineRun
        {
            RunId = runningRunId.ToString(),
            IssueIdentifier = runningIssue,
            IssueTitle = "RSC Running Issue",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            RunType = PipelineRunType.Implementation,
        };
        activePipelineRun.CurrentStep = PipelineStep.GeneratingCode;
        Fixture.RunService.AddRun(activePipelineRun);

        // ── Seed work items ───────────────────────────────────────────────────
        // Running work item: drives Work page "In flight" count.
        // Pending work item: drives Work page "Queue" count and Overview QUEUE tile.
        var runningWorkItemId = runningRunId;  // RunId == WorkItem.Id by contract
        var pendingWorkItemId = Guid.NewGuid();

        using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = runningWorkItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = runningIssue,
                IssueProviderConfigId = "issue-e2e",
                Status = WorkItemStatus.Running,
                AgentSelector = "kiro,dotnet",
                Payload = "{}",
                CreatedAt = now,
                TimeoutSeconds = 3600,
                ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
            });
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = pendingWorkItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = pendingIssue,
                IssueProviderConfigId = "issue-e2e",
                Status = WorkItemStatus.Pending,
                AgentSelector = "kiro,dotnet",
                Payload = "{}",
                CreatedAt = now,
                TimeoutSeconds = 3600,
                ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
            });
            await db.SaveChangesAsync();
        }

        // ════════════════════════════════════════════════════════════════════════
        // AC1 — Overview ACTIVE/QUEUE tiles match Work IN FLIGHT/QUEUED counts
        // ════════════════════════════════════════════════════════════════════════

        var overviewPage = new OverviewPage(Page, BaseUrl);
        await overviewPage.NavigateAsync();

        var overviewActive = await overviewPage.GetActiveCountAsync();
        var overviewQueue = await overviewPage.GetQueueCountAsync();

        var workPage = new WorkPage(Page, BaseUrl);
        await workPage.NavigateAsync();

        var workInFlight = await workPage.GetInFlightCountAsync();
        var workQueued = await workPage.GetQueuedCountAsync();

        // Overview and Work must agree — the pre-fix bug inflated Overview.Active by counting
        // ConflictRestart/PrMerged/PrClosed runs as active because those pages had divergent
        // classification logic.
        Assert.Equal(1, overviewActive);
        Assert.Equal(1, overviewQueue);
        Assert.Equal(overviewActive, workInFlight);
        Assert.Equal(overviewQueue, workQueued);

        // ════════════════════════════════════════════════════════════════════════
        // AC2 — Runs page: badge labels match RunOutcomeDisplay.Label (All tab)
        // These three are the "fail on old build" sentinels: pre-RunOutcomeDisplay,
        // ConflictRestart → "Running", PrMerged → "Completed"/"Running", PrClosed → "Cancelled"/"Running".
        // ════════════════════════════════════════════════════════════════════════

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();
        // Default tab is "All" — all non-active terminal runs are visible here.
        // The running run also appears via the active-merge path.

        Assert.Equal(RunOutcomeDisplay.Label(PipelineStep.ConflictRestart), await runsPage.GetBadgeLabelForRunAsync(conflictIssue));
        Assert.Equal(RunOutcomeDisplay.Label(PipelineStep.PrMerged), await runsPage.GetBadgeLabelForRunAsync(mergedIssue));
        Assert.Equal(RunOutcomeDisplay.Label(PipelineStep.PrClosed), await runsPage.GetBadgeLabelForRunAsync(closedIssue));
        Assert.Equal(RunOutcomeDisplay.Label(PipelineStep.Completed), await runsPage.GetBadgeLabelForRunAsync(completedIssue));
        Assert.Equal(RunOutcomeDisplay.Label(PipelineStep.Failed), await runsPage.GetBadgeLabelForRunAsync(failedIssue));
        Assert.Equal(RunOutcomeDisplay.Label(PipelineStep.Cancelled), await runsPage.GetBadgeLabelForRunAsync(cancelledIssue));
        Assert.Equal(RunOutcomeDisplay.Label(PipelineStep.GeneratingCode), await runsPage.GetBadgeLabelForRunAsync(runningIssue));

        // Confirm the sentinel values are what we think (documents intent, catches label changes).
        Assert.Equal("Restarted", RunOutcomeDisplay.Label(PipelineStep.ConflictRestart));
        Assert.Equal("Merged", RunOutcomeDisplay.Label(PipelineStep.PrMerged));
        Assert.Equal("Closed", RunOutcomeDisplay.Label(PipelineStep.PrClosed));
        Assert.Equal("Running", RunOutcomeDisplay.Label(PipelineStep.GeneratingCode));

        // ════════════════════════════════════════════════════════════════════════
        // AC3 — Runs tabs route runs by exact FinalStep match
        // FinalStepFilter maps: Completed→PipelineStep.Completed, Failed→Failed, Cancelled→Cancelled.
        // PrMerged and PrClosed appear ONLY in the All tab (no exact-match tab exists for them).
        // ════════════════════════════════════════════════════════════════════════

        // Completed tab: only the Completed run.
        await runsPage.SelectTabAsync("Completed");
        Assert.True(await runsPage.IsRunVisibleAsync(completedIssue), "Completed run should appear in Completed tab");
        Assert.False(await runsPage.IsRunVisibleAsync(mergedIssue), "PrMerged run should NOT appear in Completed tab");
        Assert.False(await runsPage.IsRunVisibleAsync(conflictIssue), "ConflictRestart run should NOT appear in Completed tab");
        Assert.False(await runsPage.IsRunVisibleAsync(failedIssue), "Failed run should NOT appear in Completed tab");
        Assert.False(await runsPage.IsRunVisibleAsync(cancelledIssue), "Cancelled run should NOT appear in Completed tab");
        Assert.False(await runsPage.IsRunVisibleAsync(closedIssue), "PrClosed run should NOT appear in Completed tab");

        // Failed tab: only the Failed run.
        await runsPage.SelectTabAsync("Failed");
        Assert.True(await runsPage.IsRunVisibleAsync(failedIssue), "Failed run should appear in Failed tab");
        Assert.False(await runsPage.IsRunVisibleAsync(completedIssue), "Completed run should NOT appear in Failed tab");
        Assert.False(await runsPage.IsRunVisibleAsync(conflictIssue), "ConflictRestart run should NOT appear in Failed tab");
        Assert.False(await runsPage.IsRunVisibleAsync(cancelledIssue), "Cancelled run should NOT appear in Failed tab");
        Assert.False(await runsPage.IsRunVisibleAsync(mergedIssue), "PrMerged run should NOT appear in Failed tab");
        Assert.False(await runsPage.IsRunVisibleAsync(closedIssue), "PrClosed run should NOT appear in Failed tab");
        Assert.False(await runsPage.IsRunVisibleAsync(runningIssue), "Running run should NOT appear in Failed tab");

        // Cancelled tab: only the Cancelled run.
        await runsPage.SelectTabAsync("Cancelled");
        Assert.True(await runsPage.IsRunVisibleAsync(cancelledIssue), "Cancelled run should appear in Cancelled tab");
        Assert.False(await runsPage.IsRunVisibleAsync(closedIssue), "PrClosed run should NOT appear in Cancelled tab");
        Assert.False(await runsPage.IsRunVisibleAsync(failedIssue), "Failed run should NOT appear in Cancelled tab");
        Assert.False(await runsPage.IsRunVisibleAsync(completedIssue), "Completed run should NOT appear in Cancelled tab");
        Assert.False(await runsPage.IsRunVisibleAsync(mergedIssue), "PrMerged run should NOT appear in Cancelled tab");
        Assert.False(await runsPage.IsRunVisibleAsync(conflictIssue), "ConflictRestart run should NOT appear in Cancelled tab");
        Assert.False(await runsPage.IsRunVisibleAsync(runningIssue), "Running run should NOT appear in Cancelled tab");

        // All tab: all terminal runs are visible.
        await runsPage.SelectTabAsync("All");
        Assert.True(await runsPage.IsRunVisibleAsync(completedIssue), "Completed run should appear in All tab");
        Assert.True(await runsPage.IsRunVisibleAsync(mergedIssue), "PrMerged run should appear in All tab");
        Assert.True(await runsPage.IsRunVisibleAsync(closedIssue), "PrClosed run should appear in All tab");
        Assert.True(await runsPage.IsRunVisibleAsync(conflictIssue), "ConflictRestart run should appear in All tab");
        Assert.True(await runsPage.IsRunVisibleAsync(failedIssue), "Failed run should appear in All tab");
        Assert.True(await runsPage.IsRunVisibleAsync(cancelledIssue), "Cancelled run should appear in All tab");
        Assert.True(await runsPage.IsRunVisibleAsync(runningIssue), "Running run should appear in All tab");
        Assert.Equal(7, await runsPage.GetRowCountAsync());

        // ════════════════════════════════════════════════════════════════════════
        // AC4 — RunPage: Cancel button and Live output panel visibility
        // Terminal runs (ConflictRestart, Completed, Failed): no Cancel button, no Live output.
        // Running run: Cancel button present, Live output card present.
        // ════════════════════════════════════════════════════════════════════════

        var runDetail = new RunDetailPage(Page, BaseUrl);

        // Terminal: ConflictRestart
        await runDetail.NavigateAsync(conflictRunId.ToString());
        Assert.False(await runDetail.IsCancelButtonVisibleAsync(), "ConflictRestart: Cancel button should not be visible");
        Assert.False(await runDetail.HasLiveOutputPanelAsync(), "ConflictRestart: Live output panel should not be present");

        // Terminal: Completed
        await runDetail.NavigateAsync(completedRunId.ToString());
        Assert.False(await runDetail.IsCancelButtonVisibleAsync(), "Completed: Cancel button should not be visible");
        Assert.False(await runDetail.HasLiveOutputPanelAsync(), "Completed: Live output panel should not be present");

        // Terminal: Failed
        await runDetail.NavigateAsync(failedRunId.ToString());
        Assert.False(await runDetail.IsCancelButtonVisibleAsync(), "Failed: Cancel button should not be visible");
        Assert.False(await runDetail.HasLiveOutputPanelAsync(), "Failed: Live output panel should not be present");

        await runDetail.NavigateAsync(mergedRunId.ToString());
        Assert.False(await runDetail.IsCancelButtonVisibleAsync(), "PrMerged: Cancel button should not be visible");
        Assert.False(await runDetail.HasLiveOutputPanelAsync(), "PrMerged: Live output panel should not be present");

        await runDetail.NavigateAsync(closedRunId.ToString());
        Assert.False(await runDetail.IsCancelButtonVisibleAsync(), "PrClosed: Cancel button should not be visible");
        Assert.False(await runDetail.HasLiveOutputPanelAsync(), "PrClosed: Live output panel should not be present");

        // Active: Running
        await runDetail.NavigateAsync(runningRunId.ToString());
        Assert.True(await runDetail.IsCancelButtonVisibleAsync(), "Running: Cancel button should be visible");
        Assert.True(await runDetail.HasLiveOutputPanelAsync(), "Running: Live output panel should be present");

        // ════════════════════════════════════════════════════════════════════════
        // AC5 — Insights: outcome mix sums to the 6 terminal runs; Restarted listed
        // Insights uses includeActive:false, so the Running run is excluded.
        // Pending work item is not a run at all — excluded.
        // The 24h default window covers all seeded runs (StartedAtOffset = now).
        // RunOutcomeDisplay.Classify: Completed+PrMerged→Succeeded(2), Cancelled+PrClosed→Cancelled(2),
        //                             Failed→Failed(1), ConflictRestart→Restarted(1). Total=6.
        // ════════════════════════════════════════════════════════════════════════

        await Page.GotoCockpitPageAsync($"{BaseUrl}/insights");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        await Page.WaitForTimeoutAsync(2000);

        // Page subtitle shows total count.
        var subtitleText = await Page.Locator(".cockpit-page-sub").InnerTextAsync();
        Assert.Matches(@"\b6 runs?\b", subtitleText);

        // Outcome mix labels: Succeeded, Failed, Cancelled present; Restarted conditional section.
        // Assert the Restarted legend is visible (rendered only when _restarted > 0).
        var restartedLegend = Page.Locator(".cockpit-card:has(h2:has-text('Outcome mix'))")
            .Locator("span[title='Superseded by a conflict restart']");
        await Assertions.Expect(restartedLegend).ToBeVisibleAsync(new() { Timeout = 5_000 });
    }
}
