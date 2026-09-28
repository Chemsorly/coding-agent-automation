using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Browser tests — Scenarios 1 (failure), 2 (cancel), 3 (re-review), 5 (draft UI)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// E2E tests for PR review lifecycle behaviours that require a browser:
/// <list type="bullet">
///   <item>Scenario 1 — Review failure applies <c>agent:error</c> to the PR.</item>
///   <item>Scenario 2 — Cancelling a live review from the Work page applies <c>agent:cancelled</c>.</item>
///   <item>Scenario 3 — Re-review after <c>agent:done</c> → <c>agent:next</c> swap creates a second run.</item>
///   <item>Scenario 5 — Draft PR shows the DRAFT badge and warning text in the drawer.</item>
/// </list>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class PrReviewLifecycleBrowserTests : E2ETestBase
{
    private static readonly string[] AgentNextLabel = ["agent:next"];

    public PrReviewLifecycleBrowserTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Seeds the issue side of a pull request so <c>GetIssueAsync</c> does not throw.
    ///
    /// <para>
    /// The dispatch pipeline calls <c>GetIssueAsync</c> for the PR identifier. On GitHub every
    /// pull request is also an issue under the same number, so the real provider handles it.
    /// The harness keeps the two fakes separate, so both must be seeded.
    /// </para>
    /// </summary>
    private void SeedPrAsIssue(string identifier, string title, string description) =>
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = identifier,
            Title = title,
            Description = description,
            Labels = AgentNextLabel
        });

    // ── Shared setup helpers ────────────────────────────────────────────────

    private async Task SaveDefaultTemplateAndProfileAsync()
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-lifecycle",
            Name = "Lifecycle Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-lifecycle",
            DisplayName = "Lifecycle Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario 1 — Review failure → agent:error label applied to the PR
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When the agent completes a review run as Failed, the PR's label is swapped to
    /// <c>agent:error</c> (not <c>agent:done</c>).
    ///
    /// <para>
    /// Label routing: <c>PipelineRun.LabelTargetKind == PullRequest</c> for review runs →
    /// <c>LabelService.SwapPrLabelAsync</c> → <c>InMemoryRepositoryProvider.AddPrLabelAsync</c>
    /// → recorded in <c>PrLabelChanges</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrReviewLifecycle_Failure_AppliesErrorLabelToPr()
    {
        // Arrange
        const int prNumber = 101;
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = prNumber,
            Identifier = "101",
            Title = "Add caching layer",
            Description = "Closes #50",
            Labels = AgentNextLabel,
            BranchName = "feature/caching",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/101",
            IsDraft = false
        });
        SeedPrAsIssue("101", "Add caching layer", "Closes #50");
        await SaveDefaultTemplateAndProfileAsync();

        await using var fakeAgent = new FakeAgentClient("lifecycle-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: dispatch via UI
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Lifecycle Template");
        await codingPage.ClickBrowsePrsAsync();
        await codingPage.SelectPrAsync("101");
        await codingPage.ClickDispatchPrReviewAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(assignment);
        Assert.Equal("101", assignment.IssueIdentifier);
        Assert.Equal(PipelineRunType.Review, assignment.RunType);

        // Agent completes as Failed — AgentJobLifecycleService derives agent:error from PipelineStep.Failed
        // TODO: AcceptAndCompleteJobAsync uses only the SignalR path (not HTTP POST /api/work-items/{id}/status).
        // AgentJobLifecycleService's skipLabelSwap guard means if ReportJobCompleted arrives after the run is
        // removed (e.g. during teardown race), the label swap is silently skipped and agent:error is never
        // written, causing the Assert.Contains below to fail intermittently. Consider using
        // CompleteLikeProductionAsync (HTTP primary channel) for more robustness on the Failed label path.
        await fakeAgent.AcceptAndCompleteJobAsync(assignment.JobId, PipelineStep.Failed);

        // Assert: history records the failed run
        var completedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "101");
        Assert.Equal(PipelineStep.Failed, completedRun.FinalStep);
        Assert.Equal(PipelineRunType.Review, completedRun.RunType);

        // Assert: agent:error was applied to the PR, not agent:done
        // TODO: WaitForHistoryAsync returns when CompleteRunAsync writes the history entry, but
        // PostCompletionBookkeepingAsync (which does the PR label swap) runs immediately after in the
        // same async method. Reading PrLabelChanges here without a WaitUntilAsync guard can race with
        // the label swap and see an empty list. Wrap these assertions in WaitUntilAsync like PrReviewPipelineTests.
        var prAdds = Fixture.RepositoryProvider.PrLabelChanges
            .Where(c => c.Action == "Add" && c.PrNumber == prNumber)
            .Select(c => c.Label)
            .ToList();
        Assert.Contains("agent:error", prAdds);
        Assert.DoesNotContain("agent:done", prAdds);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario 2 — Review cancel → agent:cancelled label applied to the PR
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Cancelling a live review from the Work page applies <c>agent:cancelled</c> to the PR.
    ///
    /// <para>
    /// Critical ordering: the agent must call <c>AcceptJobAsync</c> before the UI cancel is
    /// clicked. <c>PipelineOrchestrationService.CancelPipelineAsync</c> targets an in-memory
    /// <c>PipelineRun</c>; if no run has been created (job not accepted), the cancel is a no-op.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrReviewLifecycle_Cancel_AppliesCancelledLabelToPr()
    {
        // Arrange
        const int prNumber = 102;
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = prNumber,
            Identifier = "102",
            Title = "Refactor authentication module",
            Description = "See #51",
            Labels = AgentNextLabel,
            BranchName = "refactor/auth",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/102",
            IsDraft = false
        });
        SeedPrAsIssue("102", "Refactor authentication module", "See #51");
        await SaveDefaultTemplateAndProfileAsync();

        await using var fakeAgent = new FakeAgentClient("lifecycle-agent-2", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: dispatch via UI
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Lifecycle Template");
        await codingPage.ClickBrowsePrsAsync();
        await codingPage.SelectPrAsync("102");
        await codingPage.ClickDispatchPrReviewAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(assignment);
        Assert.Equal("102", assignment.IssueIdentifier);

        // Accept the job BEFORE cancelling: creates the in-memory PipelineRun that
        // CancelPipelineAsync targets. Without this, the cancel is a no-op.
        await fakeAgent.AcceptJobAsync(assignment.JobId);

        // Cancel the run directly via the API (the same path the UI cancel button takes:
        // RunPage → WorkItems.PostStatusAsync(id, Cancelled)).
        // The run ID from RunService IS the work item ID (WorkItem.Id == PipelineRun.RunId).
        // Cancelling via the API avoids the brittle two-step browser UI flow
        // (cancel-pipeline-btn → confirm-cancel-pipeline-btn) which is sensitive to SignalR
        // re-renders between the two clicks.
        var runService = Fixture.RunService;
        await WaitUntilAsync(
            () => runService.GetActiveRuns().Any(r => r.IssueIdentifier == "102"),
            TimeSpan.FromSeconds(15));
        var activeRunId = runService.GetActiveRuns().First(r => r.IssueIdentifier == "102").RunId;
        await Fixture.WorkItems.PostStatusAsync(
            Guid.Parse(activeRunId),
            new WorkItemStatusUpdate { Status = nameof(WorkItemStatus.Cancelled) },
            CancellationToken.None);

        // Assert: the run ends up Cancelled in history
        // TODO: WaitForHistoryAsync throws TimeoutException rather than returning null when no matching
        // run appears, so Assert.NotNull(cancelledRun) below is unreachable dead code — a timeout
        // surfaces as TimeoutException, not as a null-assertion failure. Remove the Assert.NotNull
        // or change the variable type to be explicit that the method never returns null.
        var cancelledRun = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "102" && r.FinalStep == PipelineStep.Cancelled,
            TimeSpan.FromSeconds(30));
        Assert.NotNull(cancelledRun);

        // Assert: agent:cancelled was applied to the PR
        // Label routing for Review runs: LabelTargetKind.PullRequest → SwapPrLabelAsync → PrLabelChanges
        await WaitUntilAsync(
            () => Fixture.RepositoryProvider.PrLabelChanges
                .Any(c => c.Action == "Add" && c.PrNumber == prNumber && c.Label == "agent:cancelled"),
            TimeSpan.FromSeconds(15));

        var prAdds = Fixture.RepositoryProvider.PrLabelChanges
            .Where(c => c.Action == "Add" && c.PrNumber == prNumber)
            .Select(c => c.Label)
            .ToList();
        Assert.Contains("agent:cancelled", prAdds);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario 3 — Re-review: remove agent:done, re-add agent:next, dispatch again
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// After a completed review, removing <c>agent:done</c> and adding <c>agent:next</c> allows
    /// a second manual dispatch that creates a second independent review run for the same PR.
    /// </summary>
    [Fact]
    public async Task PrReviewLifecycle_ReReview_CreatesSecondRunForSamePr()
    {
        // Arrange
        const int prNumber = 103;
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = prNumber,
            Identifier = "103",
            Title = "Improve error handling",
            Description = "Fixes #52",
            Labels = AgentNextLabel,
            BranchName = "fix/error-handling",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/103",
            IsDraft = false
        });
        SeedPrAsIssue("103", "Improve error handling", "Fixes #52");
        await SaveDefaultTemplateAndProfileAsync();

        await using var fakeAgent = new FakeAgentClient("lifecycle-agent-3", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);

        // ── First review dispatch (happy path) ──────────────────────────────
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Lifecycle Template");
        await codingPage.ClickBrowsePrsAsync();
        await codingPage.SelectPrAsync("103");
        await codingPage.ClickDispatchPrReviewAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });

        var assignment1 = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("103", assignment1.IssueIdentifier);

        // Reset BEFORE AcceptAndCompleteJobAsync to close the race window: if the pipeline emits a
        // secondary internal message that triggers OnAssignJob after CompleteJobAsync but before
        // ResetJobAssigned(), the new TCS would replace an already-resolved one and the subsequent
        // JobAssigned.Task.WaitAsync below would time out. Resetting first ensures the TCS is fresh
        // when the second assignment arrives. See ConflictRestartIntegrationTests:220 for precedent.
        fakeAgent.ResetJobAssigned();

        await fakeAgent.AcceptAndCompleteJobAsync(assignment1.JobId);

        var run1 = await WaitForHistoryAsync(r => r.IssueIdentifier == "103");
        Assert.Equal(PipelineStep.Completed, run1.FinalStep);
        Assert.Equal(PipelineRunType.Review, run1.RunType);

        // agent:done must be in PrLabelChanges after the first run
        // TODO: WaitForHistoryAsync returns when CompleteRunAsync writes the history entry, but
        // the PR label swap (PostCompletionBookkeepingAsync) runs immediately after — there is a
        // narrow race window where this assertion can read PrLabelChanges before the swap fires.
        // Wrap in WaitUntilAsync (as done for agent:cancelled in Scenario 2) to eliminate the race.
        Assert.Contains(
            Fixture.RepositoryProvider.PrLabelChanges,
            c => c.Action == "Add" && c.PrNumber == prNumber && c.Label == "agent:done");

        // ── Operator re-queues the review ────────────────────────────────────
        // Use provider methods (not direct list mutation) so PrLabelChanges tracks the changes
        // and PullRequestSummary.Labels is updated correctly.
        await Fixture.RepositoryProvider.RemovePrLabelAsync(prNumber, "agent:done", CancellationToken.None);
        await Fixture.RepositoryProvider.AddPrLabelAsync(prNumber, "agent:next", CancellationToken.None);

        // ── Second review dispatch ───────────────────────────────────────────
        // Wait for the work item to reach Succeeded in the DB. The PrDispatchDrawer calls
        // GetActiveIssueIdentifiersAsync → KubernetesWorkDistributor → DB to determine which PRs
        // are still "being processed". Clearing RunService is not enough: the DB work item must
        // also be in a terminal state before the drawer opens, otherwise pr-row-103 renders with
        // drawer-issue-dispatched (pointer-events:none) and SelectPrAsync times out.
        var workItemId = Guid.Parse(assignment1.JobId);
        await WaitUntilAsync(async () =>
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == workItemId);
            return item?.Status is WorkItemStatus.Succeeded
                or WorkItemStatus.Failed
                or WorkItemStatus.Cancelled;
        }, TimeSpan.FromSeconds(15));

        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Lifecycle Template");
        await codingPage.ClickBrowsePrsAsync();

        await codingPage.SelectPrAsync("103");
        await codingPage.ClickDispatchPrReviewAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });

        var assignment2 = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("103", assignment2.IssueIdentifier);
        Assert.Equal(PipelineRunType.Review, assignment2.RunType);

        await fakeAgent.AcceptAndCompleteJobAsync(assignment2.JobId);

        // Assert: two completed Review runs exist for PR 103
        await WaitUntilAsync(
            async () =>
            {
                var runs = await Fixture.Factory.HistoryService.GetRunHistoryAsync();
                return runs.Count(r => r.IssueIdentifier == "103" && r.RunType == PipelineRunType.Review) >= 2;
            },
            TimeSpan.FromSeconds(30));

        var history = await Fixture.Factory.HistoryService.GetRunHistoryAsync();
        var reviewRuns = history.Where(r => r.IssueIdentifier == "103" && r.RunType == PipelineRunType.Review).ToList();
        Assert.Equal(2, reviewRuns.Count);
        // TODO: WaitUntilAsync above polls for count >= 2 but only guarantees two records exist;
        // it does not guarantee that the second run's FinalStep has been written (if history entries
        // are inserted before FinalStep is set). In practice InMemoryPipelineRunHistoryService stores
        // the full summary atomically, so this is low risk, but it is not proven by the test.
        Assert.All(reviewRuns, r => Assert.Equal(PipelineStep.Completed, r.FinalStep));

        // Assert: two agent:done adds appear in PrLabelChanges (one per completed review)
        // TODO: This assertion is vulnerable to the same dedup-guard race described on ResetJobAssigned
        // above. If the dedup guard fires and the second dispatch is silently rejected (UI shows success
        // but no new run is created), only one agent:done will be in PrLabelChanges and Assert.Equal(2, ...)
        // will fail with a confusing message that doesn't point to the root cause (dedup guard).
        var doneAdds = Fixture.RepositoryProvider.PrLabelChanges
            .Count(c => c.Action == "Add" && c.PrNumber == prNumber && c.Label == "agent:done");
        Assert.Equal(2, doneAdds);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario 5 — Draft PR: DRAFT badge and warning visible, dispatch still works
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A draft PR shows the DRAFT badge in the PR list row and a warning banner when selected.
    /// Dispatch still works — the review is enqueued and the agent receives the job.
    ///
    /// <para>
    /// Selectors confirmed from <c>PrDispatchDrawer.razor</c>:
    /// <list type="bullet">
    ///   <item>Badge: <c>&lt;span class="badge-default"&gt;DRAFT&lt;/span&gt;</c> inside the pr-row element.</item>
    ///   <item>Warning: <c>&lt;div class="settings-status status-error"&gt;This PR is a draft...&lt;/div&gt;</c> in the selected-item section.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrReviewLifecycle_DraftPr_ShowsBadgeAndWarningAndDispatchWorks()
    {
        // Arrange
        const int prNumber = 300;
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = prNumber,
            Identifier = "300",
            Title = "WIP: new feature prototype",
            Description = "Draft implementation",
            Labels = AgentNextLabel,
            BranchName = "wip/prototype",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/300",
            IsDraft = true  // ← this is what enables the badge and warning in PrDispatchDrawer.razor
        });
        SeedPrAsIssue("300", "WIP: new feature prototype", "Draft implementation");
        await SaveDefaultTemplateAndProfileAsync();

        // Act: open PR drawer
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Lifecycle Template");
        await codingPage.ClickBrowsePrsAsync();

        // Assert: DRAFT badge visible in the PR row (before selecting)
        // PrDispatchDrawer.razor renders: <span class="badge-default">DRAFT</span>
        await codingPage.AssertDraftBadgeVisibleAsync("300");

        // Select the PR to show the detail section
        await codingPage.SelectPrAsync("300");

        // Assert: draft warning visible in the selected-item section
        // PrDispatchDrawer.razor renders: <div class="settings-status status-error">...This PR is a draft...
        await codingPage.AssertDraftWarningVisibleAsync();

        // Assert: dispatch still works for draft PRs
        await using var fakeAgent = new FakeAgentClient("lifecycle-agent-5", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        await codingPage.ClickDispatchPrReviewAsync();
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(assignment);
        Assert.Equal("300", assignment.IssueIdentifier);
        Assert.Equal(PipelineRunType.Review, assignment.RunType);

        await fakeAgent.AcceptAndCompleteJobAsync(assignment.JobId);

        var completedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "300");
        Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Headless tests — Scenario 4 (loop skip labels) and Scenario 6 (linked issues)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Headless E2E tests for PR review lifecycle behaviours that only check state:
/// <list type="bullet">
///   <item>Scenario 4 — Loop skip labels: PRs with agent:error, agent:in-progress, agent:done, or agent:cancelled are skipped by one loop cycle.</item>
///   <item>Scenario 6 — Linked issues: closing keywords and full issue URLs in the PR body are resolved at dispatch time.</item>
/// </list>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class PrReviewLifecycleHeadlessTests : HeadlessE2ETestBase
{
    public PrReviewLifecycleHeadlessTests(E2EFixture fixture) : base(fixture) { }

    // ── Issue seeding helper ────────────────────────────────────────────────

    private void SeedIssue(string identifier, string title = "Test issue") =>
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = identifier,
            Title = title,
            Description = "Test description",
            Labels = new[] { "agent:next" }
        });

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario 4 — Loop skip labels
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One loop cycle dispatches only the PR that has solely <c>agent:next</c>. PRs that also
    /// carry <c>agent:error</c>, <c>agent:in-progress</c>, <c>agent:done</c>, or
    /// <c>agent:cancelled</c> are filtered by <c>TryDequeueValidPr</c> in
    /// <c>DispatchScheduler.Reviews.cs</c>.
    ///
    /// <para>
    /// Note: <c>InMemoryRepositoryProvider.ListOpenPullRequestsAsync</c> ignores the label filter
    /// parameter (existing TODO), so all 5 PRs reach <c>TryDequeueValidPr</c>. This is the
    /// correct behaviour to test: the filtering happens inside the scheduler, not at the list level.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrReviewLifecycle_LoopSkipLabels_OnlyDispatchesCleanPr()
    {
        // Arrange: 5 PRs — only PR 200 (agent:next only) should be dispatched
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 200,
            Identifier = "200",
            Title = "Clean PR",
            Description = "",
            Labels = new[] { "agent:next" },
            BranchName = "feature/clean",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/200",
            IsDraft = false
        });
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 201,
            Identifier = "201",
            Title = "Error PR",
            Description = "",
            Labels = new[] { "agent:next", "agent:error" },
            BranchName = "feature/error",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/201",
            IsDraft = false
        });
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 202,
            Identifier = "202",
            Title = "In-progress PR",
            Description = "",
            Labels = new[] { "agent:next", "agent:in-progress" },
            BranchName = "feature/inprogress",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/202",
            IsDraft = false
        });
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 203,
            Identifier = "203",
            Title = "Done PR",
            Description = "",
            Labels = new[] { "agent:next", "agent:done" },
            BranchName = "feature/done",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/203",
            IsDraft = false
        });
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 204,
            Identifier = "204",
            Title = "Cancelled PR",
            Description = "",
            Labels = new[] { "agent:next", "agent:cancelled" },
            BranchName = "feature/cancelled",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/204",
            IsDraft = false
        });

        // Seed issues for all PR identifiers so GetIssueAsync doesn't throw
        foreach (var id in new[] { "200", "201", "202", "203", "204" })
            SeedIssue(id, $"Issue for PR {id}");

        // Template with ReviewEnabled = true — the loop checks this flag
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            ClosedLoopPollInterval = TimeSpan.FromSeconds(1)
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-loop-review",
            Name = "Loop Review Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            ReviewEnabled = true  // required: DispatchScheduler.DispatchPrRoundAsync checks this
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-loop-review",
            DisplayName = "Loop Review Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("loop-review-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: start the loop (do NOT call StartAsync — ExecuteAsync is already parked)
        // TODO: loopService is obtained via GetRequiredService<PipelineLoopService>() from the root
        // container. The existing Fixture.SchedulerFactory.LoopService property returns the same
        // singleton and is the conventional accessor used elsewhere in the test suite. Using the root
        // container here is harmless while PipelineLoopService is a singleton, but will silently break
        // if the registration changes to Scoped. Prefer Fixture.SchedulerFactory.LoopService for consistency.
        var loopService = Fixture.SchedulerFactory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started, "StartLoopAsync should return true with valid config");

            // Assert: only PR 200 is dispatched
            var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(assignment);
            Assert.Equal("200", assignment.IssueIdentifier);
            Assert.Equal(PipelineRunType.Review, assignment.RunType);

            // Complete the dispatched job so the loop can proceed cleanly
            await fakeAgent.AcceptAndCompleteJobAsync(assignment.JobId);

            var completedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "200");
            Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);

            // Assert: PRs 201-204 were never dispatched (no agent:in-progress added for them)
            // TODO: This negative assertion checks absence of dispatch activity but does not verify
            // that the skip-labelled PRs (201-204) still carry their original labels — a regression
            // that inadvertently mutates those labels (without applying agent:in-progress) would not
            // be caught here.
            var labelAddsFor201To204 = Fixture.RepositoryProvider.PrLabelChanges
                .Where(c => c.Action == "Add"
                    && c.PrNumber is >= 201 and <= 204
                    && c.Label == "agent:in-progress")
                .ToList();
            Assert.Empty(labelAddsFor201To204);

            // Assert: history has exactly one Review run (for 200)
            var history = await Fixture.Factory.HistoryService.GetRunHistoryAsync();
            var reviewRuns = history.Where(r => r.RunType == PipelineRunType.Review).ToList();
            Assert.Single(reviewRuns);
            Assert.Equal("200", reviewRuns[0].IssueIdentifier);
        }
        finally
        {
            // TODO: StopLoop() is synchronous but the test does not wait for the loop's internal
            // dispatch cycle to fully unwind before returning. ResetAllAsync (called by the next
            // test's InitializeAsync) also stops the loop — concurrent StopLoop() calls may leave
            // the loop in an inconsistent state for subsequent loop tests in the same E2ECollection.
            // Additionally, if StartLoopAsync() throws, StopLoop() is called on an unstarted loop;
            // verify StopLoop() is a safe no-op in that state, or guard the call with a flag.
            loopService.StopLoop();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario 6 — Linked issues
    // ═══════════════════════════════════════════════════════════════════════

    private async Task SeedTemplateAndProfileAsync()
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-linked",
            Name = "Linked Issues Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-linked",
            DisplayName = "Linked Issues Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);
    }

    /// <summary>
    /// The PR body contains a closing keyword reference to issue 12 and a full URL reference to
    /// issue 13. Both are seeded. The assignment's <c>LinkedIssueContexts</c> contains both entries
    /// with non-null titles.
    ///
    /// <para>
    /// <c>FetchLinkedIssueContextsAsync</c> reads from both <c>PrTitle</c> and <c>PrDescription</c>
    /// via <c>IssueReferenceParser.ParseAllClosingKeywords</c> and <c>ParseIssueUrls</c>.
    /// The full URL format is <c>https://github.com/e2e-org/e2e-repo/issues/{N}</c> (matching
    /// <c>InMemoryRepositoryProvider.RepositoryFullName == "e2e-org/e2e-repo"</c>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrReviewLifecycle_LinkedIssues_KeywordAndUrlBothResolved()
    {
        // Arrange
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "12",
            Title = "Issue twelve",
            Description = "Body of issue 12",
            Labels = new[] { "agent:next" }
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "13",
            Title = "Issue thirteen",
            Description = "Body of issue 13",
            Labels = new[] { "agent:next" }
        });
        // Seed the PR identifier as an issue so the dispatch pipeline doesn't fail
        SeedIssue("400", "PR 400");

        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 400,
            Identifier = "400",
            Title = "Feature: two linked issues",
            Description = "Closes #12\nhttps://github.com/e2e-org/e2e-repo/issues/13",
            Labels = new[] { "agent:next" },
            BranchName = "feature/linked",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/400",
            IsDraft = false
        });

        await SeedTemplateAndProfileAsync();

        await using var fakeAgent = new FakeAgentClient("linked-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: dispatch via the headless helper
        var result = await DispatchPrReviewAsync(
            prIdentifier: "400",
            prTitle: "Feature: two linked issues",
            prDescription: "Closes #12\nhttps://github.com/e2e-org/e2e-repo/issues/13");
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(assignment);

        // Assert: both linked issues resolved
        Assert.NotNull(assignment.LinkedIssueContexts);
        Assert.Equal(2, assignment.LinkedIssueContexts.Count);
        var identifiers = assignment.LinkedIssueContexts.Select(c => c.Identifier).ToHashSet();
        Assert.Contains("12", identifiers);
        Assert.Contains("13", identifiers);
        Assert.All(assignment.LinkedIssueContexts, c => Assert.NotNull(c.Title));
    }

    /// <summary>
    /// With 7 closing-keyword references in the PR body, <c>FetchLinkedIssueContextsAsync</c>
    /// caps the fetch at 5 (<c>MaxLinkedIssues = 5</c>).
    /// </summary>
    [Fact]
    public async Task PrReviewLifecycle_LinkedIssues_7References_OnlyFiveFetched()
    {
        // Arrange: seed 7 issues and the PR
        foreach (var id in Enumerable.Range(21, 7).Select(i => i.ToString()))
        {
            Fixture.IssueProvider.Issues.Add(new IssueDetail
            {
                Identifier = id,
                Title = $"Issue {id}",
                Description = $"Body of issue {id}",
                Labels = new[] { "agent:next" }
            });
        }
        SeedIssue("401", "PR 401");

        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 401,
            Identifier = "401",
            Title = "Feature: many linked issues",
            Description = "Closes #21\nCloses #22\nCloses #23\nCloses #24\nCloses #25\nCloses #26\nCloses #27",
            Labels = new[] { "agent:next" },
            BranchName = "feature/many-linked",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/401",
            IsDraft = false
        });

        await SeedTemplateAndProfileAsync();

        await using var fakeAgent = new FakeAgentClient("linked-agent-2", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act
        var result = await DispatchPrReviewAsync(
            prIdentifier: "401",
            prTitle: "Feature: many linked issues",
            prDescription: "Closes #21\nCloses #22\nCloses #23\nCloses #24\nCloses #25\nCloses #26\nCloses #27");
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(assignment);

        // Assert: capped at 5
        Assert.NotNull(assignment.LinkedIssueContexts);
        Assert.Equal(5, assignment.LinkedIssueContexts.Count);
    }

    /// <summary>
    /// A reference to a missing issue is skipped non-fatally. The review is still dispatched and
    /// the assignment contains only the successfully-fetched linked issue.
    ///
    /// <para>
    /// <c>FetchLinkedIssueContextsAsync</c> has a <c>try/catch(Exception)</c> around each
    /// <c>GetIssueAsync</c> call; <c>KeyNotFoundException</c> (thrown by
    /// <c>InMemoryIssueProvider.GetIssueAsync</c> when unseeded) is caught and logged as a warning.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrReviewLifecycle_LinkedIssues_MissingIssueSkipped_ReviewStillDispatched()
    {
        // Arrange: issue 31 seeded, issue 32 NOT seeded
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "31",
            Title = "Issue thirty-one",
            Description = "Body of issue 31",
            Labels = new[] { "agent:next" }
        });
        // Issue 32 is intentionally NOT seeded → GetIssueAsync("32") throws KeyNotFoundException
        SeedIssue("402", "PR 402");

        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 402,
            Identifier = "402",
            Title = "Feature: one missing linked issue",
            Description = "Closes #31\nCloses #32",
            Labels = new[] { "agent:next" },
            BranchName = "feature/missing-linked",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/402",
            IsDraft = false
        });

        await SeedTemplateAndProfileAsync();

        await using var fakeAgent = new FakeAgentClient("linked-agent-3", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act
        var result = await DispatchPrReviewAsync(
            prIdentifier: "402",
            prTitle: "Feature: one missing linked issue",
            prDescription: "Closes #31\nCloses #32");
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(assignment);

        // Assert: review was dispatched (not blocked by the missing issue)
        Assert.Equal("402", assignment.IssueIdentifier);
        Assert.Equal(PipelineRunType.Review, assignment.RunType);

        // Assert: only issue 31 is in LinkedIssueContexts; issue 32 was skipped
        Assert.NotNull(assignment.LinkedIssueContexts);
        var linkedCtx = Assert.Single(assignment.LinkedIssueContexts);
        Assert.Equal("31", linkedCtx.Identifier);
    }
}
