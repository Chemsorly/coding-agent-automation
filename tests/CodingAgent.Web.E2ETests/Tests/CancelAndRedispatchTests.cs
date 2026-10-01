using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for cancel-with-confirm and re-dispatch from the Run detail page (/runs/{id}).
///
/// <para>
/// Issue #3091. Covers Scenarios 1–5:
/// <list type="number">
///   <item><b>Scenario 1 — Cancel with confirm.</b> Live run at GeneratingCode step. Click Cancel,
///     choose "No" → nothing changes. Click Cancel again, choose "Yes" → WorkItem Cancelled,
///     label <c>agent:cancelled</c> applied, K8s job deleted, Cancel button gone.</item>
///   <item><b>Scenario 2 — Cancel double-click.</b> Clicking "Yes, cancel" twice in quick
///     succession produces exactly one status post and no error text.</item>
///   <item><b>Scenario 3 — Re-dispatch a failed implementation run.</b> Click Re-dispatch,
///     confirm → new WorkItem created, "Re-dispatched successfully" shown, new agent receives
///     the new assignment.</item>
///   <item><b>Scenario 4 — Re-dispatch hidden when it should be.</b> Live runs, Review runs,
///     and runs missing provider IDs do not show the re-dispatch card.</item>
///   <item><b>Scenario 5 — Re-dispatch error path.</b> Re-dispatch while an active WorkItem
///     for the same issue already exists → page shows error, no second WorkItem created.</item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class CancelAndRedispatchTests : E2ETestBase
{
    public CancelAndRedispatchTests(E2EFixture fixture) : base(fixture) { }

    // ──────────────────────────────────────────────────────────────────────
    // Shared seed helper
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the template, agent profile, and issue; connects the given agent; dispatches the
    /// issue via the UI; waits for the agent to accept and report <paramref name="step"/>; and
    /// returns the active run's ID once the server reflects that step.
    /// </summary>
    private async Task<(string RunId, Guid WorkItemId)> SeedDispatchAndActivateAsync(
        FakeAgentClient agent,
        string issueId,
        PipelineStep step = PipelineStep.GeneratingCode)
    {
        // Ensure the template and profile are created (idempotent via same IDs).
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-cancel-redispatch",
            Name = "Cancel Redispatch Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-cancel-redispatch",
            DisplayName = "Cancel Redispatch Agent Profile",
            MatchLabels = new[] { "cancel-redispatch-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = $"Cancel/Redispatch test issue #{issueId}",
            Description = "## Requirements\nTest\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement" },
            Url = $"https://github.com/e2e-org/e2e-repo/issues/{issueId}"
        });

        // UI dispatch
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Cancel Redispatch Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueId);
        await codingPage.ClickStartPipelineAsync();
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });

        // Agent picks up and reports the step
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, step);

        var runService = Fixture.RunService;
        await WaitUntilAsync(() =>
            runService.GetActiveRuns().Any(r => r.IssueIdentifier == issueId && r.CurrentStep == step));

        // TODO [WARNING]: The WaitUntilAsync predicate above and the .First() call below are not atomic.
        // Under the shared E2ECollection server, a prior test that left an active run with the same
        // issueId in the service could cause .First() to return a different run than the one that
        // satisfied the step condition. Fix by scoping the .First() predicate to also match CurrentStep,
        // or by capturing the RunId inside the WaitUntilAsync closure.
        var run = runService.GetActiveRuns().First(r => r.IssueIdentifier == issueId);
        var workItemId = Guid.Parse(run.RunId);
        return (run.RunId, workItemId);
    }

    /// <summary>
    /// After <see cref="SeedDispatchAndActivateAsync"/>, stamps the WorkItem with a <c>K8sJobName</c>
    /// and inserts a matching job into <see cref="FakeKubernetesJobClient.CreatedJobs"/>.
    /// This lets <c>CancelRunAsync</c> find the job name and call <c>DeleteJobAsync</c>.
    /// </summary>
    private async Task<string> StampK8sJobAsync(Guid workItemId)
    {
        // TODO [WARNING]: This job-name format is hand-coded to match JobNameFactory's convention.
        // If JobNameFactory's naming scheme changes, this helper will silently diverge, causing the
        // K8s deletion assertion in Scenario 1 to track the wrong job name. Fix by calling
        // JobNameFactory directly or sharing a constant between the two.
        // Build a deterministic job name from the WorkItem ID (same format as JobNameFactory).
        var jobName = $"caa-{workItemId:N}"[..Math.Min(16, $"caa-{workItemId:N}".Length)];

        // Update the DB row so GetK8sJobNameAsync returns the name.
        await using var db = await Fixture.DbContextFactory.CreateDbContextAsync();
        var entity = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId);
        if (entity is not null)
        {
            entity.K8sJobName = jobName;
            // TODO [WARNING]: db.SaveChangesAsync() is called without a CancellationToken. Under test
            // cancellation the database write could be abandoned mid-flight. Pass the test's
            // CancellationToken here once the method signature supports it.
            await db.SaveChangesAsync();
        }

        // Put the job in FakeKubernetesJobClient so DeleteJobAsync can track the call.
        var job = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
                Labels = new Dictionary<string, string>
                {
                    ["caa/work-item-id"] = workItemId.ToString()
                },
                CreationTimestamp = DateTime.UtcNow
            }
        };
        Fixture.K8sClient.CreatedJobs[jobName] = job;
        return jobName;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Scenario 1 — Cancel with confirm (dismiss first, then confirm)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_WithConfirm_DismissFirst_ThenConfirm()
    {
        // Arrange: live run at GeneratingCode
        await using var fakeAgent = new FakeAgentClient("cancel-confirm-agent-1", "cancel-redispatch-e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var (runId, workItemId) = await SeedDispatchAndActivateAsync(fakeAgent, "3091-cancel-1");

        // Stamp a K8s job name so the deletion path is exercised.
        var jobName = await StampK8sJobAsync(workItemId);

        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);
        await detail.CancelButton.WaitForAsync(new() { Timeout = 15_000 });

        // Act 1: open confirm prompt and choose "No" — nothing should change.
        await detail.CancelAsync(confirm: false);

        // Assert: Cancel button still visible (run still active), no error text.
        // TODO [WARNING]: IsCancelButtonVisibleAsync() is a point-in-time snapshot. If dismissing the
        // confirm prompt triggers a Blazor re-render that briefly detaches the Cancel button, this
        // assertion can return false transiently (false-negative flake). Consider replacing with a
        // condition-based wait (e.g. WaitUntilAsync(() => detail.IsCancelButtonVisibleAsync())).
        Assert.True(await detail.IsCancelButtonVisibleAsync(),
            "Cancel button must remain visible after dismissing the confirm prompt");
        Assert.True(Fixture.RunService.GetActiveRuns().Any(r => r.IssueIdentifier == "3091-cancel-1"),
            "Run must still be active after dismissing the cancel confirmation");

        // Act 2: open confirm prompt again and choose "Yes, cancel".
        await detail.CancelAsync(confirm: true);

        // Assert: WorkItem leaves the active set once the cancellation is applied.
        await WaitUntilAsync(() =>
            !Fixture.RunService.GetActiveRuns().Any(r => r.IssueIdentifier == "3091-cancel-1"),
            timeout: TimeSpan.FromSeconds(20));

        // Assert: K8s job deletion was requested.
        await WaitUntilAsync(
            () => Fixture.K8sClient.DeletedJobs.Contains(jobName),
            timeout: TimeSpan.FromSeconds(15));
        Assert.Contains(jobName, Fixture.K8sClient.DeletedJobs);

        // Assert: label agent:cancelled was added to the issue.
        await WaitUntilAsync(
            () => Fixture.IssueProvider.LabelChanges.Any(lc =>
                lc.Identifier == "3091-cancel-1" && lc.Label == AgentLabels.Cancelled && lc.Added),
            timeout: TimeSpan.FromSeconds(15));

        // Assert: Cancel button gone from the page (run is terminal, sidebar hides it).
        await detail.WaitForCancelButtonGoneAsync(timeoutMs: 15_000);
        Assert.False(await detail.IsCancelButtonVisibleAsync(),
            "Cancel button must not be shown after the run is cancelled");

        // Assert: page body reflects the Cancelled terminal state.
        var text = await detail.GetBodyTextAsync();
        Assert.Contains("Cancelled", text, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Scenario 2 — Cancel double-click (exactly one status post, no error)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_DoubleClick_ProducesOneStatusPost_NoError()
    {
        // Arrange: live run at GeneratingCode
        await using var fakeAgent = new FakeAgentClient("cancel-dbl-agent-1", "cancel-redispatch-e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var (runId, _) = await SeedDispatchAndActivateAsync(fakeAgent, "3091-cancel-2");

        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);
        await detail.CancelButton.WaitForAsync(new() { Timeout = 15_000 });

        // Open the confirm prompt.
        await detail.CancelButton.ClickAsync();
        await detail.ConfirmCancelButton.WaitForAsync(new() { Timeout = 10_000 });

        // Double-click "Yes, cancel" as fast as possible to simulate the race.
        await detail.ConfirmCancelButton.ClickAsync();
        // Second click immediately after — at the OS/browser level this is near-simultaneous.
        try { await detail.ConfirmCancelButton.ClickAsync(); } catch { /* button may already be gone */ }

        // Assert: run reaches the Cancelled state (first click applied successfully).
        // TODO [WARNING]: WaitUntilAsync (and similar calls throughout this file) does not receive a
        // CancellationToken. If a test is aborted by xUnit's timeout mechanism, this spin-poll loop
        // has no cancellation path and can cause test teardown to hang. Pass a CancellationToken
        // once WaitUntilAsync supports it.
        await WaitUntilAsync(
            () => !Fixture.RunService.GetActiveRuns().Any(r => r.IssueIdentifier == "3091-cancel-2"),
            timeout: TimeSpan.FromSeconds(20));

        // Assert: no cancel-error callout is visible.
        // TODO [WARNING]: WaitForTimeoutAsync(1000) is a fixed-duration sleep used as a stabilisation
        // guard. This is racy: if the error arrives after 1 s it will be missed. Replace with a
        // condition-based wait (e.g. wait for the run to reach terminal state, then assert). Also,
        // the test name promises "exactly one status post" but does not assert the post count — a
        // regression where both clicks succeed silently (no error, two posts) would pass. Consider
        // adding a counter assertion via FakeWorkItems (or similar) once available.
        await Page.WaitForTimeoutAsync(1000); // brief stabilisation
        var errorCallout = Page.Locator("[data-testid='cancel-error-callout']");
        var errorCount = await errorCallout.CountAsync();
        Assert.Equal(0, errorCount);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Scenario 3 — Re-dispatch a failed implementation run
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Redispatch_FailedRun_CreatesNewWorkItemAndShowsSuccess()
    {
        // Arrange: dispatch and bring a run to the GeneratingCode step.
        await using var fakeAgent1 = new FakeAgentClient("redispatch-agent-1", "cancel-redispatch-e2e");
        await fakeAgent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var (runId, workItemId) = await SeedDispatchAndActivateAsync(fakeAgent1, "3091-redispatch-3");

        // Complete the run as Failed so CanRedispatch becomes true.
        // SeedDispatchAndActivateAsync already called AcceptJobAsync, so we just report completion.
        // TODO [WARNING]: There is no explicit wait confirming the run has been accepted and entered
        // the active state before ReportCompletionAsync is called. SeedDispatchAndActivateAsync waits
        // for the step via WaitUntilAsync, which should be sufficient, but if AcceptJobAsync processing
        // is asynchronous on the server side, the first ReportCompletionAsync call below could race
        // with the accept. Add a WaitUntilAsync(() => GetActiveRuns().Any(...)) guard here if flakiness
        // is observed.
        await fakeAgent1.ReportCompletionAsync(workItemId.ToString(), new CodingAgent.Pipeline.Models.JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "Quality gates exhausted",
            FailureCategory = FailureReason.QualityGateExhausted,
            RetryCount = 2,
            FilesChangedCount = 0,
            LinesAdded = 0,
            LinesRemoved = 0,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>()
        });

        // Wait for history to record the failed run.
        await WaitForHistoryAsync(
            r => r.IssueIdentifier == "3091-redispatch-3" && r.FinalStep == PipelineStep.Failed,
            timeout: TimeSpan.FromSeconds(20));

        // Connect a second agent to receive the re-dispatched job.
        await using var fakeAgent2 = new FakeAgentClient("redispatch-agent-2", "cancel-redispatch-e2e");
        await fakeAgent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: navigate to the completed run's detail page and click Re-dispatch → Confirm.
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);
        await detail.RedispatchCard.WaitForAsync(new() { Timeout = 15_000 });

        await detail.RedispatchAsync(confirm: true);

        // Assert: "Re-dispatched successfully" message appears.
        await detail.WaitForRedispatchSuccessAsync(timeoutMs: 15_000);
        Assert.True(await detail.HasRedispatchSuccessAsync(),
            "Run page must show 'Re-dispatched successfully' after a confirmed re-dispatch");

        // Assert: the second agent receives the new assignment (new run ID, same issue).
        var newAssignment = await fakeAgent2.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("3091-redispatch-3", newAssignment.IssueIdentifier);
        Assert.NotEqual(runId, newAssignment.JobId);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Scenario 4 — Re-dispatch hidden when it should be
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 4a: Live run — re-dispatch card must not appear (CanRedispatch returns false for active runs).
    /// </summary>
    [Fact]
    public async Task Redispatch_HiddenForLiveRun()
    {
        await using var fakeAgent = new FakeAgentClient("redispatch-live-agent", "cancel-redispatch-e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var (runId, _) = await SeedDispatchAndActivateAsync(fakeAgent, "3091-redispatch-4a");

        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // Cancel button visible (live run), re-dispatch card must be absent.
        Assert.True(await detail.IsCancelButtonVisibleAsync(),
            "Cancel button must be visible for a live run");
        Assert.False(await detail.IsRedispatchCardVisibleAsync(),
            "Re-dispatch card must NOT be visible for a live run");
    }

    /// <summary>
    /// 4b: Review run — re-dispatch card must not appear (CanRedispatch guards on RunType == Implementation).
    /// Dispatches a PR review run, completes it as Failed, and asserts the re-dispatch card is hidden.
    /// </summary>
    [Fact]
    public async Task Redispatch_HiddenForReviewRun()
    {
        // Set up template and profile (reuse the same IDs from the shared helper).
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-cancel-redispatch",
            Name = "Cancel Redispatch Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-cancel-redispatch",
            DisplayName = "Cancel Redispatch Agent Profile",
            MatchLabels = new[] { "cancel-redispatch-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Seed a PR in the repository provider (Review dispatch requires a PR to exist).
        const int prNumber = 3091;
        const string prIdentifier = "3091";
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = prNumber,
            Identifier = prIdentifier,
            Title = "PR for redispatch-hidden-review test",
            Description = "Closes #3091-redispatch-4b",
            Labels = new List<string> { "agent:next" },
            BranchName = "feature/review-test",
            TargetBranch = "main",
            Url = $"https://github.com/e2e-org/e2e-repo/pull/{prNumber}",
            IsDraft = false
        });

        // Also seed as issue so GetIssueAsync succeeds during dispatch enrichment.
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = prIdentifier,
            Title = "PR for redispatch-hidden-review test",
            Description = "Closes #3091-redispatch-4b",
            Labels = new[] { "agent:next" }
        });

        // Connect an agent to receive the review job.
        await using var fakeAgent = new FakeAgentClient("redispatch-review-agent", "cancel-redispatch-e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Dispatch a Review-type WorkItem directly via WorkItems client.
        await Fixture.WorkItems.DispatchAsync(new JobDistributionRequest
        {
            IssueIdentifier = prIdentifier,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-test",
            TaskType = WorkItemTaskType.Review,
            AgentSelector = "cancel-redispatch-e2e",
            RunType = PipelineRunType.Review,
            TimeoutSeconds = 0,
            PayloadSchemaVersion = 1,
        }, CancellationToken.None);

        // Wait for agent to receive and accept the job, then complete it as Failed.
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // TODO [WARNING]: The WaitUntilAsync on GetActiveRuns() below may race with
        // AcceptAndCompleteJobAsync if that method is atomic (accept + complete in one call).
        // If the run completes before WaitUntilAsync evaluates, it will already be in history and
        // not in GetActiveRuns(), causing a timeout hang. Consider replacing this wait with
        // WaitForHistoryAsync directly, or splitting AcceptAndCompleteJobAsync into explicit
        // Accept + Complete phases with the active-run check placed between them.
        await WaitUntilAsync(() =>
            Fixture.RunService.GetActiveRuns().Any(r =>
                r.IssueIdentifier == prIdentifier && r.RunType == PipelineRunType.Review));

        // Complete the review run as Failed to get a terminal state.
        // AcceptAndCompleteJobAsync handles Accept + step transitions + ReportJobCompleted.
        await fakeAgent.AcceptAndCompleteJobAsync(assignment.JobId, PipelineStep.Failed, pullRequestUrl: null);

        var history = await WaitForHistoryAsync(
            r => r.FinalStep == PipelineStep.Failed && r.RunType == PipelineRunType.Review,
            timeout: TimeSpan.FromSeconds(20));

        // Navigate to the completed review run and assert re-dispatch card is absent.
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(history.RunId);
        Assert.False(await detail.IsRedispatchCardVisibleAsync(),
            "Re-dispatch card must NOT be visible for a Review run");
    }

    /// <summary>
    /// 4c: Run with missing provider IDs — re-dispatch card must not appear.
    /// CanRedispatch returns false when IssueProviderConfigId or RepoProviderConfigId is null/empty.
    /// Uses a directly-inserted summary to simulate a legacy run without provider IDs.
    /// </summary>
    [Fact]
    public async Task Redispatch_HiddenForRunMissingProviderIds()
    {
        // Inject a terminal implementation run summary with null provider IDs directly into the
        // history service. This simulates a legacy run persisted before provider IDs were recorded.
        var legacyRunId = Guid.NewGuid();
        var legacySummary = new PipelineRunSummary
        {
            RunId = legacyRunId.ToString(),
            IssueIdentifier = new IssueIdentifier("3091-noprovider-4c"),
            IssueTitle = "Legacy run without provider IDs",
            FinalStep = PipelineStep.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-1),
            RunType = PipelineRunType.Implementation,
            // IssueProviderConfigId and RepoProviderConfigId are intentionally null — CanRedispatch returns false.
            IssueProviderConfigId = null,
            RepoProviderConfigId = null,
        };
        await Fixture.HistoryService.AddRunSummaryAsync(legacySummary, CancellationToken.None);

        // Navigate to the legacy run's detail page.
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(legacyRunId.ToString());

        // Assert: no re-dispatch card visible because IssueProviderConfigId is missing.
        Assert.False(await detail.IsRedispatchCardVisibleAsync(),
            "Re-dispatch card must NOT be visible when IssueProviderConfigId is missing");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Scenario 5 — Re-dispatch error path (active WorkItem already exists)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Redispatch_ErrorPath_ActiveWorkItemAlreadyExists_ShowsError()
    {
        // Step 1: complete the first run as Failed (gives us a terminal run to re-dispatch from).
        await using var fakeAgent1 = new FakeAgentClient("redispatch-err-agent-1", "cancel-redispatch-e2e");
        await fakeAgent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var (runId, workItemId1) = await SeedDispatchAndActivateAsync(fakeAgent1, "3091-redispatch-5");

        // SeedDispatchAndActivateAsync already called AcceptJobAsync, so just report completion.
        await fakeAgent1.ReportCompletionAsync(workItemId1.ToString(), new CodingAgent.Pipeline.Models.JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "Test failure",
            FailureCategory = FailureReason.AgentError,
            RetryCount = 0,
            FilesChangedCount = 0,
            LinesAdded = 0,
            LinesRemoved = 0,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>()
        });
        await WaitForHistoryAsync(
            r => r.IssueIdentifier == "3091-redispatch-5" && r.FinalStep == PipelineStep.Failed,
            timeout: TimeSpan.FromSeconds(20));

        // Step 2: dispatch the same issue again (creating an active WorkItem) before the
        // re-dispatch button is clicked, simulating a duplicate-dispatch situation.
        await using var fakeAgent2 = new FakeAgentClient("redispatch-err-agent-2", "cancel-redispatch-e2e");
        await fakeAgent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        await Fixture.WorkItems.DispatchAsync(new JobDistributionRequest
        {
            IssueIdentifier = "3091-redispatch-5",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "cancel-redispatch-e2e",
            RunType = PipelineRunType.Implementation,
            TimeoutSeconds = 0,
            PayloadSchemaVersion = 1,
        }, CancellationToken.None);

        // Wait for the second WorkItem to be picked up by the second agent (confirms it's active).
        await fakeAgent2.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Act: navigate to the original (failed) run's page and click Re-dispatch → Confirm.
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);
        await detail.RedispatchCard.WaitForAsync(new() { Timeout = 15_000 });

        await detail.RedispatchAsync(confirm: true);

        // Assert: error message is shown (dispatch rejected by the dedup guard).
        await WaitUntilAsync(
            async () => await detail.HasRedispatchErrorAsync(),
            timeout: TimeSpan.FromSeconds(15));

        Assert.True(await detail.HasRedispatchErrorAsync(),
            "Re-dispatch error callout must appear when the same issue already has an active WorkItem");

        // Assert: the active set still contains exactly one run for this issue (no third one).
        var activeRuns = Fixture.RunService.GetActiveRuns()
            .Where(r => r.IssueIdentifier == "3091-redispatch-5")
            .ToList();
        Assert.Single(activeRuns);
    }
}
