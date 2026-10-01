using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using k8s.Models;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for cancel and re-dispatch actions on the /runs/{id} Run page.
///
/// <para>
/// Scenarios (from issue #3091):
/// <list type="number">
///   <item>Cancel with confirm — live run at GeneratingCode, open Run page, click Cancel
///         Pipeline, choose "No" changes nothing, then confirm; asserts WorkItem is Cancelled,
///         final label is <c>agent:cancelled</c>, K8s job delete was requested, and the
///         Cancel button is no longer shown.</item>
///   <item>Cancel double-click — clicking "Yes, cancel" twice produces only one status post
///         (no error text appears).</item>
///   <item>Re-dispatch a failed implementation run — click Re-dispatch, confirm; a new WorkItem
///         is created, the success message appears, and the fake agent receives the assignment.
///         </item>
///   <item>Re-dispatch visibility — hidden for live runs, Review/Decomposition runs, and runs
///         with missing provider IDs.</item>
///   <item>Re-dispatch error path — re-dispatch while an active WorkItem already exists for the
///         same issue shows the "Re-dispatch failed" message and no second WorkItem is
///         created.</item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class CancelAndRedispatchFromRunPageTests : E2ETestBase
{
    public CancelAndRedispatchFromRunPageTests(E2EFixture fixture) : base(fixture) { }

    // ── Shared seed helpers ───────────────────────────────────────────────

    /// <summary>
    /// Seeds template + agent profile used by all cancel/redispatch scenarios.
    /// </summary>
    private async Task SeedTemplateAndProfileAsync()
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-runpage-1",
            Name = "Run Page Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-runpage-1",
            DisplayName = "Run Page Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);
    }

    /// <summary>
    /// Seeds issue, dispatches via UI, waits for the fake agent to receive and accept the job,
    /// reports <see cref="PipelineStep.GeneratingCode"/>, and returns the active run's RunId.
    /// </summary>
    private async Task<(string runId, FakeAgentClient agent)> SetupLiveRunAsync(
        string issueId, FakeAgentClient agent)
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = $"Run page test issue #{issueId}",
            Description = "## Requirements\nTest\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement" }
        });

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Run Page Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueId);
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        var runService = Fixture.RunService;
        await WaitUntilAsync(() =>
            runService.GetActiveRuns().Any(r =>
                r.IssueIdentifier == issueId &&
                r.CurrentStep == PipelineStep.GeneratingCode));

        // TODO [WARNING]: First() here is safe because WaitUntilAsync above guarantees the run
        // exists, but diverges from the defensive FirstOrDefault + null-check pattern used
        // elsewhere. A concurrent fixture reset (unlikely) could cause an undiagnosable
        // InvalidOperationException. Consider FirstOrDefault with an explicit null-check.
        var runId = runService.GetActiveRuns()
            .First(r => r.IssueIdentifier == issueId)
            .RunId;

        return (runId, agent);
    }

    // ── Scenario 1: Cancel with confirm ──────────────────────────────────

    /// <summary>
    /// Scenario 1: Cancel with confirm from the Run page.
    ///
    /// <list type="bullet">
    ///   <item>A live run (fake agent accepted, step GeneratingCode).</item>
    ///   <item>Open /runs/{id} and click Cancel Pipeline. A confirm prompt appears.</item>
    ///   <item>Choose "No" — nothing changes; run is still live.</item>
    ///   <item>Click Cancel Pipeline again, then "Yes, cancel".</item>
    ///   <item>Assert: WorkItem ends Cancelled, issue gets agent:cancelled label,
    ///         K8s job delete was requested, and Cancel button is gone.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task CancelFromRunPage_WithConfirm_CancelsRun()
    {
        await SeedTemplateAndProfileAsync();

        await using var fakeAgent = new FakeAgentClient("cancel-runpage-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (runId, _) = await SetupLiveRunAsync("3091", fakeAgent);

        // ── Seed K8sJobName so KubernetesJobCleanup.TryDeleteJobForRunAsync can look it up ──
        // In the standard SignalR E2E flow the WorkItem has no K8sJobName (K8s dispatch was never
        // called). Set it manually so the cleanup path is exercised and DeletedJobs is populated.
        var workItemId = Guid.Parse(runId);
        var expectedK8sJobName = JobNameFactory.ForBrain(workItemId);

        await using (var dbSetup = Fixture.DbContextFactory.CreateDbContext())
        {
            var item = await dbSetup.WorkItems.FirstAsync(w => w.Id == workItemId);
            item.K8sJobName = expectedK8sJobName;
            await dbSetup.SaveChangesAsync();
        }

        // Also register the fake job so FakeKubernetesJobClient.DeleteJobAsync can remove it
        // (FakeKubernetesJobClient.DeleteJobAsync calls CreatedJobs.TryRemove which is fine even
        // if the key is absent, but adding it here keeps the fake state realistic).
        Fixture.K8sClient.CreatedJobs[expectedK8sJobName] = new k8s.Models.V1Job
        {
            Metadata = new k8s.Models.V1ObjectMeta { Name = expectedK8sJobName }
        };

        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // Verify the Cancel button is present while live
        Assert.True(await detail.IsCancelButtonVisibleAsync(),
            "Cancel Pipeline button should be visible for an active run");

        // --- "No" keeps the run alive ---
        await detail.CancelAsync(confirm: false);

        // TODO [WARNING]: WaitForTimeoutAsync(500) is a fixed delay that is flaky under CI load.
        // Replace with a WaitUntilAsync poll on the confirm section disappearing (e.g., poll until
        // CancelConfirmSection.CountAsync() == 0) before asserting the Cancel button is still visible.
        // The cancel confirm section should be gone (dismissed), cancel button still visible
        await Page.WaitForTimeoutAsync(500);
        Assert.True(await detail.IsCancelButtonVisibleAsync(),
            "Cancel button should still be visible after dismissing the confirm dialog");

        // --- "Yes, cancel" actually cancels ---
        await detail.CancelAsync(confirm: true);

        // Wait for the WorkItem to reach Cancelled status
        WorkItemEntity? cancelledItem = null;
        // TODO [WARNING]: DbContext is created on every 200ms poll iteration inside WaitUntilAsync.
        // If WaitUntilAsync does not propagate cancellation into the lambda, in-flight DbContext
        // allocations may leak when the outer WaitAsync timeout fires. This pattern is repeated in
        // Scenarios 1, 2, 3, and 5. Consider restructuring to create a single DbContext outside
        // the loop, or ensure WaitUntilAsync propagates a CancellationToken to the lambda.
        await WaitUntilAsync(async () =>
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            cancelledItem = await db.WorkItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == workItemId);
            return cancelledItem?.Status == WorkItemStatus.Cancelled;
        }, timeout: TimeSpan.FromSeconds(20));

        Assert.Equal(WorkItemStatus.Cancelled, cancelledItem!.Status);

        // Assert: agent:cancelled label was applied to the issue
        await WaitUntilAsync(() =>
            Fixture.IssueProvider.LabelChanges.Any(lc =>
                lc.Identifier == "3091" && lc.Label == AgentLabels.Cancelled && lc.Added),
            timeout: TimeSpan.FromSeconds(15));

        var issue = Fixture.IssueProvider.Issues.Single(i => i.Identifier == "3091");
        Assert.Contains(AgentLabels.Cancelled, issue.Labels);

        // Assert: K8s job delete was requested (KubernetesJobCleanup.TryDeleteJobForRunAsync)
        await WaitUntilAsync(
            () => Fixture.K8sClient.DeletedJobs.Contains(expectedK8sJobName),
            timeout: TimeSpan.FromSeconds(15));
        Assert.Contains(expectedK8sJobName, Fixture.K8sClient.DeletedJobs);

        // Assert: UI shows Cancelled and no Cancel button
        await detail.WaitForCancelledAsync(timeoutMs: 15_000);
        Assert.False(await detail.IsCancelButtonVisibleAsync(),
            "Cancel button should not be visible after run is cancelled");
    }

    // ── Scenario 2: Cancel double-click ──────────────────────────────────

    /// <summary>
    /// Scenario 2: Double-clicking "Yes, cancel" produces only one status post and no error text.
    /// </summary>
    [Fact]
    public async Task CancelFromRunPage_DoubleClick_NoError()
    {
        await SeedTemplateAndProfileAsync();

        await using var fakeAgent = new FakeAgentClient("cancel-runpage-2", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (runId, _) = await SetupLiveRunAsync("3092", fakeAgent);

        // Capture the CancelRunAsync call count baseline before the test actions so a
        // preceding test cannot inflate the counter.
        var cancelCountBefore = Fixture.LifecycleManagerDecorator.CancelRunCallCount;

        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // Open the confirm section
        await detail.CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        await detail.CancelButton.ClickAsync();

        await detail.CancelConfirmSection.WaitForAsync(new() { Timeout = 10_000 });

        // Click "Yes, cancel" twice in rapid succession
        await detail.ConfirmCancelButton.ClickAsync();
        await detail.ConfirmCancelButton.ClickAsync(new() { Force = true });

        // Wait for cancellation to process
        await WaitUntilAsync(async () =>
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == Guid.Parse(runId));
            return item?.Status == WorkItemStatus.Cancelled;
        }, timeout: TimeSpan.FromSeconds(20));

        // No error callout should appear
        var errorCallout = await Page.Locator("[data-testid='cancel-error-callout']").CountAsync();
        Assert.Equal(0, errorCallout);

        // Assert run ended cancelled (exactly once)
        await using var db2 = Fixture.DbContextFactory.CreateDbContext();
        var cancelled = await db2.WorkItems.AsNoTracking()
            .Where(w => w.IssueIdentifier == "3092" && w.Status == WorkItemStatus.Cancelled)
            .CountAsync();
        Assert.Equal(1, cancelled);

        // Assert: the UI double-click guard prevented a second CancelRunAsync call at the server.
        // Give the server a moment to process any in-flight second request before asserting.
        await Task.Delay(300);
        var cancelCallsForThisTest = Fixture.LifecycleManagerDecorator.CancelRunCallCount - cancelCountBefore;
        Assert.Equal(1, cancelCallsForThisTest);
    }

    // ── Scenario 3: Re-dispatch a failed implementation run ───────────────

    /// <summary>
    /// Scenario 3: Re-dispatch a failed implementation run.
    ///
    /// <list type="bullet">
    ///   <item>Complete a run with Failed final step.</item>
    ///   <item>Navigate to /runs/{id}, click Re-dispatch, confirm.</item>
    ///   <item>A new WorkItem is created for the same issue.</item>
    ///   <item>"Re-dispatched successfully" is shown.</item>
    ///   <item>The fake agent receives the new assignment.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task RedispatchFromRunPage_FailedRun_DispatchesNewWorkItem()
    {
        await SeedTemplateAndProfileAsync();

        // Agent 1 for the first (failed) run
        await using var agent1 = new FakeAgentClient("redispatch-agent-1", "e2e");
        await agent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (runId, _) = await SetupLiveRunAsync("3093", agent1);

        // Complete the first run as Failed
        // TODO [WARNING]: assignment1 is a dead variable — ReportCompletionAsync is called with
        // runId directly. The intent was likely agent1.ReportCompletionAsync(assignment1.JobId, ...)
        // for semantic clarity, since runId and assignment1.JobId are the same value here. Remove
        // the variable or use it consistently.
        var assignment1 = new { JobId = runId };
        var failedPayload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            FinalLabel = null,
            FailureReason = "Tests failed after max retries",
            FailureCategory = FailureReason.QualityGateExhausted,
            CompletedAt = DateTimeOffset.UtcNow,
            RetryCount = 3,
            FilesChangedCount = 5,
            LinesAdded = 100,
            LinesRemoved = 20,
            BrainUpdatesPushed = false,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>()
        };
        await agent1.ReportCompletionAsync(runId, failedPayload);
        // TODO [WARNING]: ReportCompletionAsync uses the SignalR hub path only, not the HTTP
        // primary completion channel (CompleteLikeProductionAsync). Label application that goes
        // through WorkItemStatusTransitionService via the HTTP channel is not exercised here. If
        // agent:cancelled label application is ever gated on the HTTP channel, this test will pass
        // while production breaks. The existing CompleteLikeProductionTests cover the two-channel
        // path separately.

        // Wait for the run to be terminal in history
        await WaitForHistoryAsync(r => r.RunId == runId && r.FinalStep == PipelineStep.Failed,
            timeout: TimeSpan.FromSeconds(20));

        // Disconnect agent1 and connect agent2 to receive the re-dispatch
        // TODO [WARNING]: agent1 is declared with `await using` at the outer scope AND explicitly
        // disposed here. When the test exits the `await using` declaration will call DisposeAsync
        // a second time. If FakeAgentClient.DisposeAsync is not idempotent this is a double-dispose
        // bug. Remove the explicit DisposeAsync call and manage the timing differently, or document
        // FakeAgentClient as idempotent.
        await agent1.DisposeAsync();

        await using var agent2 = new FakeAgentClient("redispatch-agent-2", "e2e");
        await agent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Navigate to the run page
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // Wait for the Re-dispatch button to appear (run is terminal with provider IDs)
        await Page.WaitForSelectorAsync("[data-testid='redispatch-btn']", new() { Timeout = 15_000 });
        Assert.True(await detail.IsRedispatchButtonVisibleAsync(),
            "Re-dispatch button should be visible for a failed implementation run");

        // Click Re-dispatch and confirm
        await detail.RedispatchAsync(confirm: true);

        // Wait for success message
        await detail.WaitForRedispatchSuccessAsync(timeoutMs: 15_000);

        // Assert: a new WorkItem was created for the same issue
        await WaitUntilAsync(async () =>
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var count = await db.WorkItems.AsNoTracking()
                .CountAsync(w => w.IssueIdentifier == "3093");
            return count >= 2;
        }, timeout: TimeSpan.FromSeconds(20));

        // Assert: agent2 received the new assignment
        var newAssignment = await agent2.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(runId, newAssignment.JobId);
        Assert.Equal("3093", newAssignment.IssueIdentifier);
        // TODO [WARNING]: The assertion only verifies IssueIdentifier. It does not verify
        // TemplateId or RepoProviderConfigId on the new WorkItem, so a re-dispatch that uses the
        // wrong template would still pass. Consider asserting the new WorkItem's provider IDs
        // match those of the original run (IssueProviderConfigId="issue-e2e",
        // RepoProviderConfigId="repo-e2e") by querying the DB for the new WorkItem.
    }

    // ── Scenario 4: Re-dispatch visibility ───────────────────────────────

    /// <summary>
    /// Scenario 4a: Re-dispatch button is NOT shown for a live (active) run.
    /// </summary>
    // TODO [WARNING]: Issue identifier "3094" is a prefix of "3094b" and "3094c" used in
    // Scenarios 4b and 4c. If any code resolves issue identifiers by prefix match rather than
    // exact match, Scenario 4a's issue could interfere with 4b/4c. Also note that
    // IsRedispatchCardPresentAsync returns false for a page-not-found (404), making a page load
    // failure indistinguishable from a correctly hidden card — consider asserting the page loaded
    // successfully before asserting the card is absent.
    [Fact]
    public async Task RedispatchCard_NotShown_ForLiveRun()
    {
        await SeedTemplateAndProfileAsync();

        await using var fakeAgent = new FakeAgentClient("visibility-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (runId, _) = await SetupLiveRunAsync("3094", fakeAgent);

        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // The redispatch card must NOT be present for a live run
        Assert.False(await detail.IsRedispatchCardPresentAsync(),
            "Re-dispatch card should NOT be visible for a live/active run");
    }

    /// <summary>
    /// Scenario 4b: Re-dispatch button is NOT shown for a run whose type is Review or
    /// Decomposition (even when in a terminal state).
    /// </summary>
    // TODO [WARNING]: This test only seeds a PipelineRunType.Review run. The issue description and
    // the class-level doc-comment also require that the re-dispatch button is hidden for
    // PipelineRunType.Decomposition runs. Add a companion test (or extend this one) that seeds a
    // terminal Decomposition run and asserts IsRedispatchCardPresentAsync() == false.
    [Fact]
    public async Task RedispatchCard_NotShown_ForReviewRun()
    {
        // Seed a Review run directly in history with a terminal state but RunType=Review
        var reviewRunId = Guid.NewGuid();
        var reviewRun = new PipelineRun
        {
            RunId = reviewRunId.ToString(),
            IssueIdentifier = "3094b",
            IssueTitle = "PR Review run visibility test",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            RunType = PipelineRunType.Review,
        };
        reviewRun.CurrentStep = PipelineStep.Completed;
        await Fixture.Factory.HistoryService.AddRunToHistoryAsync(reviewRun);

        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(reviewRunId.ToString());

        // Wait for the page to load
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        // TODO [WARNING]: WaitForTimeoutAsync(1_000) is a fixed delay used as a page-settle guard.
        // Replace with a condition-based poll (e.g., wait until the run-status element is visible,
        // or until IsRedispatchCardPresentAsync() stops changing) to avoid flakiness under CI load.
        await Page.WaitForTimeoutAsync(1_000);

        // TODO [WARNING]: IsRedispatchCardPresentAsync() returns false for a 404/error page as well
        // as a correctly hidden card. Assert that the page loaded the expected run first (e.g.,
        // await Page.WaitForSelectorAsync("[data-testid='run-status']") or check for the run title
        // in h1) before asserting on card absence to prevent a broken navigation masking a regression.

        // The redispatch card must NOT be present for a Review run
        Assert.False(await detail.IsRedispatchCardPresentAsync(),
            "Re-dispatch card should NOT be visible for a Review run");
    }

    /// <summary>
    /// Scenario 4c: Re-dispatch button is NOT shown for a terminal implementation run
    /// that lacks provider IDs (old run without those fields).
    /// </summary>
    [Fact]
    public async Task RedispatchCard_NotShown_ForRunWithMissingProviderIds()
    {
        // Seed an implementation run without IssueProviderConfigId / RepoProviderConfigId
        var oldRunId = Guid.NewGuid();
        var oldRun = new PipelineRun
        {
            RunId = oldRunId.ToString(),
            IssueIdentifier = "3094c",
            IssueTitle = "Old run without provider IDs",
            // IssueProviderConfigId intentionally left null (empty string default)
            IssueProviderConfigId = "",
            RepoProviderConfigId = "",
            RunType = PipelineRunType.Implementation,
        };
        oldRun.CurrentStep = PipelineStep.Failed;
        await Fixture.Factory.HistoryService.AddRunToHistoryAsync(oldRun);

        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(oldRunId.ToString());

        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        // TODO [WARNING]: WaitForTimeoutAsync(1_000) is a fixed delay used as a page-settle guard.
        // Replace with a condition-based poll consistent with the WaitUntilAsync pattern.
        await Page.WaitForTimeoutAsync(1_000);

        // TODO [WARNING]: Same 404-ambiguity concern as Scenario 4b — assert the page loaded
        // successfully before asserting on card absence.

        // The redispatch card must NOT be present for a run without provider IDs
        Assert.False(await detail.IsRedispatchCardPresentAsync(),
            "Re-dispatch card should NOT be visible for a run without provider IDs");
    }

    /// <summary>
    /// Scenario 4d: Re-dispatch button is NOT shown for a terminal Decomposition run.
    /// <c>CanRedispatch</c> in RunPage.razor only returns true for
    /// <c>PipelineRunType.Implementation</c>, so Decomposition runs (and DecompositionAnalysis
    /// runs) must hide the button. This companion test covers the Decomposition case that
    /// Scenario 4b's Review-only coverage left untested.
    /// </summary>
    [Fact]
    public async Task RedispatchCard_NotShown_ForDecompositionRun()
    {
        // Seed a terminal Decomposition run directly into history
        var decompositionRunId = Guid.NewGuid();
        var decompositionRun = new PipelineRun
        {
            RunId = decompositionRunId.ToString(),
            IssueIdentifier = "3094d",
            IssueTitle = "Epic decomposition run visibility test",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            RunType = PipelineRunType.Decomposition,
        };
        decompositionRun.CurrentStep = PipelineStep.Completed;
        await Fixture.Factory.HistoryService.AddRunToHistoryAsync(decompositionRun);

        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(decompositionRunId.ToString());

        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        // TODO [WARNING]: WaitForTimeoutAsync(1_000) is a fixed delay used as a page-settle guard.
        // Replace with a condition-based poll consistent with the WaitUntilAsync pattern.
        await Page.WaitForTimeoutAsync(1_000);

        // TODO [WARNING]: Same 404-ambiguity concern as Scenarios 4b/4c — assert the page loaded
        // successfully before asserting on card absence.

        // The redispatch card must NOT be present for a Decomposition run
        Assert.False(await detail.IsRedispatchCardPresentAsync(),
            "Re-dispatch card should NOT be visible for a Decomposition run");
    }

    // ── Scenario 5: Re-dispatch error path ───────────────────────────────

    /// <summary>
    /// Scenario 5: Re-dispatch while an active WorkItem for the same issue already exists.
    ///
    /// <list type="bullet">
    ///   <item>Complete run A (failed) for issue 3095.</item>
    ///   <item>Dispatch a second, still-active WorkItem for issue 3095 directly (bypass UI).</item>
    ///   <item>Navigate to run A's page and attempt re-dispatch.</item>
    ///   <item>Assert: "Re-dispatch failed" message appears, and no third WorkItem was created.
    ///         </item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task RedispatchFromRunPage_ActiveRunAlreadyExists_ShowsErrorNoDuplicate()
    {
        await SeedTemplateAndProfileAsync();

        // Run the first implementation run and fail it
        await using var agent1 = new FakeAgentClient("redispatch-error-agent-1", "e2e");
        await agent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (runId, _) = await SetupLiveRunAsync("3095", agent1);

        var failedPayload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            FinalLabel = null,
            FailureReason = "Test failure for redispatch error scenario",
            FailureCategory = FailureReason.AgentError,
            CompletedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            FilesChangedCount = 0,
            LinesAdded = 0,
            LinesRemoved = 0,
            BrainUpdatesPushed = false,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>()
        };
        await agent1.ReportCompletionAsync(runId, failedPayload);

        // Wait for it to be in history
        await WaitForHistoryAsync(r => r.RunId == runId && r.FinalStep == PipelineStep.Failed,
            timeout: TimeSpan.FromSeconds(20));

        // Now create a second active WorkItem for the same issue (mimics the "already in progress"
        // state). Connect a second agent and dispatch directly so it stays in Running state.
        await using var agent2 = new FakeAgentClient("redispatch-error-agent-2", "e2e");
        await agent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Dispatch a second work item for issue 3095 via the dispatch endpoint
        var secondDispatch = await Fixture.WorkItems.DispatchAsync(new JobDistributionRequest
        {
            IssueIdentifier = "3095",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            AgentSelector = "",
            TimeoutSeconds = 3600,
            TaskType = WorkItemTaskType.Implementation,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
            InitiatedBy = "e2e-test",
            RunType = PipelineRunType.Implementation,
            PayloadSchemaVersion = 1
        }, CancellationToken.None);
        // Accept the job so it's Running (not just Pending/Dispatched)
        var assignment2 = await agent2.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent2.AcceptJobAsync(assignment2.JobId);

        // Count WorkItems before the attempted re-dispatch
        await using var dbBefore = Fixture.DbContextFactory.CreateDbContext();
        var countBefore = await dbBefore.WorkItems.AsNoTracking()
            .CountAsync(w => w.IssueIdentifier == "3095");

        // Navigate to the original failed run's page and attempt re-dispatch
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        await Page.WaitForSelectorAsync("[data-testid='redispatch-btn']", new() { Timeout = 15_000 });
        await detail.RedispatchAsync(confirm: true);

        // Wait for the error message
        await detail.WaitForRedispatchErrorAsync(timeoutMs: 15_000);

        // Assert: no additional WorkItem was created
        await using var dbAfter = Fixture.DbContextFactory.CreateDbContext();
        var countAfter = await dbAfter.WorkItems.AsNoTracking()
            .CountAsync(w => w.IssueIdentifier == "3095");
        Assert.Equal(countBefore, countAfter);
    }
}
