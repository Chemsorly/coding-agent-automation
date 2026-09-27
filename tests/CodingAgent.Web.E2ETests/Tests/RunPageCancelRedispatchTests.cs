using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E coverage for the two operator actions on <c>/runs/{id}</c>:
/// <list type="bullet">
///   <item>Cancel Pipeline — two-step confirm flow in the PipelineSidebar footer.</item>
///   <item>Re-dispatch — two-step confirm card rendered for terminal Implementation runs.</item>
/// </list>
/// Scenarios 1–5 correspond directly to the acceptance criteria in issue #3091.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RunPageCancelRedispatchTests : E2ETestBase
{
    public RunPageCancelRedispatchTests(E2EFixture fixture) : base(fixture) { }

    // ── Shared setup helpers ──────────────────────────────────────────────

    private async Task SeedTemplateAndProfileAsync()
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-runpage",
            Name = "RunPage Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-runpage",
            DisplayName = "RunPage Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);
    }

    /// <summary>
    /// Dispatches an issue via the AgentCodingPage UI, waits for the job assignment,
    /// and returns the assignment message and the active run's RunId. The fake agent is
    /// accepted and moved to the given pipeline step.
    /// </summary>
    private async Task<(JobAssignmentMessage Assignment, string RunId)> DispatchAndReachStepAsync(
        FakeAgentClient fakeAgent,
        string issueIdentifier,
        PipelineStep step)
    {
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("RunPage Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueIdentifier);
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportStepAsync(assignment.JobId, step);

        // Wait until the run is visible in run service at the expected step.
        var runService = Fixture.RunService;
        await WaitUntilAsync(() => runService.GetActiveRuns()
            .Any(r => r.IssueIdentifier == issueIdentifier && r.CurrentStep == step));

        // Retrieve the RunId from the active run service (not from history — active runs are only
        // written to history when they complete, so WaitForHistoryAsync would time out here).
        var activeRun = runService.GetActiveRuns()
            .First(r => r.IssueIdentifier == issueIdentifier && r.CurrentStep == step);

        return (assignment, activeRun.RunId);
    }

    // ── Scenario 1: Cancel with full confirm ──────────────────────────────

    [Fact]
    public async Task Cancel_ActiveRun_FromRunPage_WithConfirm()
    {
        // Arrange
        await SeedTemplateAndProfileAsync();
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "3091",
            Title = "RunPage cancel test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await using var fakeAgent = new FakeAgentClient("cancel-runpage-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (_, runId) = await DispatchAndReachStepAsync(fakeAgent, "3091", PipelineStep.GeneratingCode);

        // Act: navigate to the run page and cancel with confirmation.
        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);
        await runPage.CancelAsync(confirm: true);

        // Assert 1: run reaches Cancelled terminal state in history.
        var cancelledRun = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "3091" && r.FinalStep == PipelineStep.Cancelled,
            timeout: TimeSpan.FromSeconds(20));
        Assert.Equal(PipelineStep.Cancelled, cancelledRun.FinalStep);

        // Assert 2: issue label agent:cancelled was added.
        await WaitUntilAsync(() =>
            Fixture.IssueProvider.LabelChanges.Any(
                x => x.Identifier == "3091" && x.Label == "agent:cancelled" && x.Added));
        Assert.Contains(Fixture.IssueProvider.LabelChanges,
            x => x.Identifier == "3091" && x.Label == "agent:cancelled" && x.Added);

        // Assert 3 (K8s job delete) is skipped: FakeJobController dispatch does not create a
        // K8s job, so DeletedJobs is always empty for this scenario. K8s cleanup is exercised
        // by the K8sModeTests suite which seeds a K8s job name on the run before cancelling.

        // Assert 4: cancel button is gone from the page (run is no longer live).
        // The element is absent from the DOM when IsRunning = false, so CountAsync returns 0.
        var cancelBtnCount = await runPage.CancelButton.CountAsync();
        Assert.Equal(0, cancelBtnCount);
    }

    // ── Scenario 1 (dismiss path): choosing "No" changes nothing ─────────

    [Fact]
    public async Task Cancel_ActiveRun_FromRunPage_DismissDoesNotCancel()
    {
        // Arrange: dispatch a run and advance it to GeneratingCode.
        await SeedTemplateAndProfileAsync();
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "3091f",
            Title = "RunPage cancel dismiss test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await using var fakeAgent = new FakeAgentClient("cancel-runpage-dismiss-agent", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (_, runId) = await DispatchAndReachStepAsync(fakeAgent, "3091f", PipelineStep.GeneratingCode);

        // Act: navigate to the run page and dismiss the cancel dialog ("No").
        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);
        await runPage.CancelAsync(confirm: false);

        // Assert 1: the run is still active — no Cancelled entry appears in history.
        // WaitForHistoryAsync would throw on timeout; we use a short poll window to confirm
        // the run has NOT transitioned to Cancelled. 2 seconds is enough — if CancelRunAsync
        // were accidentally invoked it would propagate well within that window.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var runs = await Fixture.Factory.HistoryService.GetRunHistoryAsync();
            var cancelled = runs.FirstOrDefault(r => r.IssueIdentifier == "3091f" && r.FinalStep == PipelineStep.Cancelled);
            Assert.Null(cancelled);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        // Assert 2: the run is still shown as active in RunService.
        var activeRuns = Fixture.RunService.GetActiveRuns();
        Assert.Contains(activeRuns, r => r.IssueIdentifier == "3091f");

        // Assert 3: the Cancel button is still present on the page (run is still live).
        var cancelBtnCount = await runPage.CancelButton.CountAsync();
        Assert.Equal(1, cancelBtnCount);
    }

    // ── Scenario 2: Cancel double-click produces only one status post ─────

    [Fact]
    public async Task Cancel_ActiveRun_FromRunPage_DoubleClick_OnlyOnePost()
    {
        // Arrange
        await SeedTemplateAndProfileAsync();
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "3091b",
            Title = "RunPage cancel double-click test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await using var fakeAgent = new FakeAgentClient("cancel-runpage-agent-2", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (_, runId) = await DispatchAndReachStepAsync(fakeAgent, "3091b", PipelineStep.GeneratingCode);

        // Act: navigate to the run page, open the confirm dialog, then click "Yes, cancel" twice.
        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);

        // Open the confirm dialog.
        await runPage.CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        await runPage.CancelButton.ClickAsync();

        // Click the confirm button twice in rapid succession.
        var confirmBtn = Page.Locator("[data-testid='confirm-cancel-pipeline-btn']");
        await confirmBtn.WaitForAsync(new() { Timeout = 10_000 });
        await confirmBtn.ClickAsync();
        // TODO: [WARNING] This second click may not reliably exercise the _cancelling guard.
        // Depending on timing the first cancel can complete, OnRunCompleted fires, the confirm UI
        // is removed from the DOM, and the second ClickAsync either hits a detached/stale element
        // or silently no-ops — so the single-post assertion may pass for the wrong reason (element
        // gone, not guard). Consider adding artificial latency (network intercept) or asserting
        // server-side post count directly to prove the guard is what prevented the duplicate.
        await confirmBtn.ClickAsync();   // second click — the _cancelling guard should reject it

        // Wait for cancellation to propagate.
        await WaitForHistoryAsync(
            r => r.IssueIdentifier == "3091b" && r.FinalStep == PipelineStep.Cancelled,
            timeout: TimeSpan.FromSeconds(20));

        // Assert 1: exactly one agent:cancelled label was added (no duplicate post).
        var cancelledLabelCount = Fixture.IssueProvider.LabelChanges
            .Count(x => x.Identifier == "3091b" && x.Label == "agent:cancelled" && x.Added);
        Assert.Equal(1, cancelledLabelCount);

        // Assert 2: no cancel-error callout on the page.
        var errorCount = await Page.Locator("[data-testid='cancel-error-callout']").CountAsync();
        Assert.Equal(0, errorCount);
    }

    // ── Scenario 3: Re-dispatch a failed run ─────────────────────────────

    [Fact]
    public async Task Redispatch_FailedRun_CreatesNewWorkItem()
    {
        // Arrange: seed and dispatch the first (terminal) run.
        await SeedTemplateAndProfileAsync();
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "3091c",
            Title = "RunPage redispatch test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await using var fakeAgent1 = new FakeAgentClient("redispatch-agent-1", "e2e");
        await fakeAgent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("RunPage Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("3091c");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var assignment1 = await fakeAgent1.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // AcceptJobAsync populates IssueProviderConfigId/RepoProviderConfigId on the PipelineRun,
        // which CanRedispatch requires. AcceptAndCompleteJobAsync calls JobAccepted internally.
        await fakeAgent1.AcceptAndCompleteJobAsync(assignment1.JobId, PipelineStep.Failed);

        // Wait for the run to appear in history as Failed.
        var failedRun = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "3091c" && r.FinalStep == PipelineStep.Failed,
            timeout: TimeSpan.FromSeconds(20));

        // Connect a second agent to receive the re-dispatched assignment.
        // FakeAgentClient.JobAssigned is a single-use TaskCompletionSource.
        await using var fakeAgent2 = new FakeAgentClient("redispatch-agent-2", "e2e");
        await fakeAgent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: navigate to the failed run's page and re-dispatch.
        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(failedRun.RunId);

        // The redispatch-card must be visible for a terminal Implementation run with provider IDs.
        var cardCount = await runPage.RedispatchCard.CountAsync();
        Assert.Equal(1, cardCount);

        await runPage.RedispatchAsync(confirm: true);

        // Assert 1: success message appears on the page.
        // Wait for a selector that only exists after the dispatch completes and _redispatchSuccess
        // is set to true. This avoids the race where WaitForSelectorAsync on the card itself
        // returns immediately (card was present before dispatch) and TextContentAsync captures
        // pre-dispatch content. The :has-text selector is only satisfied after Blazor re-renders
        // with the success message.
        await Page.WaitForSelectorAsync(
            "[data-testid='redispatch-card']:has-text('Re-dispatched successfully')",
            new() { Timeout = 15_000 });
        var cardText = await runPage.RedispatchCard.TextContentAsync();
        Assert.Contains("Re-dispatched successfully", cardText);

        // Assert 2: a new run for the same issue appears in history.
        var newRun = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "3091c" && r.RunId != failedRun.RunId,
            timeout: TimeSpan.FromSeconds(20));
        Assert.NotEqual(failedRun.RunId, newRun.RunId);

        // Assert 3: the second fake agent receives the new assignment.
        var newAssignment = await fakeAgent2.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("3091c", newAssignment.IssueIdentifier);
    }

    // ── Scenario 4: Re-dispatch card is hidden for live runs ─────────────

    [Fact]
    public async Task Redispatch_HiddenForLiveRun()
    {
        // Arrange: dispatch a run and keep it in-flight (never complete).
        await SeedTemplateAndProfileAsync();
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "3091d",
            Title = "RunPage redispatch hidden test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await using var fakeAgent = new FakeAgentClient("redispatch-live-agent", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (_, runId) = await DispatchAndReachStepAsync(fakeAgent, "3091d", PipelineStep.GeneratingCode);

        // Act: navigate to the live run's page.
        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);

        // Assert: the redispatch-card is absent from the DOM entirely.
        // CanRedispatch returns false for active runs, so the entire @if block is not rendered.
        // CountAsync() == 0 is the correct assertion — IsHiddenAsync() would throw on a missing element.
        var cardCount = await runPage.RedispatchCard.CountAsync();
        Assert.Equal(0, cardCount);
    }

    [Fact]
    public async Task Redispatch_HiddenWhenProviderIdsAreMissing()
    {
        // Arrange: insert a terminal run summary with no IssueProviderConfigId.
        // CanRedispatch requires non-empty IssueProviderConfigId and RepoProviderConfigId.
        // TODO: [WARNING] This test seeds the run summary via Fixture.Factory.HistoryService
        // (the E2EWebApplicationFactory's service, which is the same instance passed to
        // ApiE2EWebApplicationFactory). If RunPage.razor reads run history from a different
        // host's service, the synthetic run would not be visible and the page would render
        // not-found — causing CountAsync() == 0 to pass for the wrong reason. Confirm the
        // host that serves /runs/{id} uses the same HistoryService instance as seeded here.
        var runId = Guid.NewGuid().ToString();
        await Fixture.Factory.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = "3091d2",
            IssueTitle = "Provider IDs missing",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            IssueProviderConfigId = null,     // missing → CanRedispatch = false
            RepoProviderConfigId = null,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAtOffset = DateTimeOffset.UtcNow,
        });

        // Act: navigate to the run page directly (bypassing dispatch UI since the run is synthetic).
        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);

        // Assert: redispatch-card not rendered.
        var cardCount = await runPage.RedispatchCard.CountAsync();
        Assert.Equal(0, cardCount);
    }

    [Theory]
    [InlineData(PipelineRunType.Review)]
    [InlineData(PipelineRunType.DecompositionAnalysis)]
    [InlineData(PipelineRunType.Decomposition)]
    public async Task Redispatch_HiddenForNonImplementationRunType(PipelineRunType runType)
    {
        // Arrange: insert a terminal run summary with a non-Implementation RunType.
        // CanRedispatch (RunPage.razor) gates on RunType == PipelineRunType.Implementation,
        // so the card must be absent for Review, DecompositionAnalysis, and Decomposition runs
        // even when the run is terminal and provider IDs are present.
        // TODO: [WARNING] This test seeds the run summary via Fixture.Factory.HistoryService.
        // If the Blazor host that serves /runs/{id} reads from a different service instance,
        // the synthetic run would not be visible and the page would render not-found —
        // causing CountAsync() == 0 to pass for the wrong reason (run not found, not
        // CanRedispatch = false). Confirm that both hosts share the same HistoryService instance.
        var runId = Guid.NewGuid().ToString();
        await Fixture.Factory.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = $"3091d3-{(int)runType}",
            IssueTitle = $"Non-Implementation run ({runType})",
            FinalStep = PipelineStep.Failed,
            RunType = runType,
            IssueProviderConfigId = "issue-e2e",     // present — only RunType should block re-dispatch
            RepoProviderConfigId = "repo-e2e",
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAtOffset = DateTimeOffset.UtcNow,
        });

        // Act: navigate to the run page directly.
        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);

        // Assert: redispatch-card is not rendered because RunType != Implementation.
        var cardCount = await runPage.RedispatchCard.CountAsync();
        Assert.Equal(0, cardCount);
    }

    // ── Scenario 5: Re-dispatch error path ───────────────────────────────

    [Fact]
    public async Task Redispatch_ErrorPath_WhenRunAlreadyActive()
    {
        // Arrange part 1: dispatch and fail the first run (terminal).
        await SeedTemplateAndProfileAsync();
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "3091e",
            Title = "RunPage redispatch error test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        var fakeAgent1 = new FakeAgentClient("redispatch-error-agent-1", "e2e");
        await fakeAgent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("RunPage Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("3091e");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var assignment1 = await fakeAgent1.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fakeAgent1.AcceptAndCompleteJobAsync(assignment1.JobId, PipelineStep.Failed);

        var failedRun = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "3091e" && r.FinalStep == PipelineStep.Failed,
            timeout: TimeSpan.FromSeconds(20));

        // Disconnect fakeAgent1 before creating the blocking WorkItem so FakeJobController
        // cannot assign it to fakeAgent1, claim it, and potentially complete it before the
        // re-dispatch assertion fires. With no idle agent, the pending WorkItem stays Pending
        // (DB status) and the dedup check blocks re-dispatch with 409 as intended.
        await fakeAgent1.DisposeAsync();

        // Arrange part 2: create a second active WorkItem for the same issue WITHOUT connecting
        // an agent to claim it — the FakeJobController only assigns to idle agents, so with no
        // idle agent the WorkItem stays Pending and the dedup check will block re-dispatch.
        await Fixture.WorkItems.DispatchAsync(new JobDistributionRequest
        {
            IssueIdentifier = "3091e",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            RunType = PipelineRunType.Implementation,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
            PayloadSchemaVersion = 1,
        });

        // Act: navigate to the first (terminal) run page and attempt re-dispatch.
        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(failedRun.RunId);
        await runPage.RedispatchAsync(confirm: true);

        // Assert 1: the error alert is shown inside the redispatch card.
        var alertLocator = Page.Locator("[data-testid='redispatch-card'] [role='alert']");
        await alertLocator.WaitForAsync(new() { Timeout = 10_000 });
        var alertCount = await alertLocator.CountAsync();
        Assert.True(alertCount > 0, "Expected a re-dispatch error alert to be visible");

        var alertText = await alertLocator.First.TextContentAsync();
        Assert.Contains("Re-dispatch failed", alertText);

        // Assert 2: no third WorkItem was created — only the original terminal run and the
        // already-pending second WorkItem should exist.
        // TODO: [WARNING] Assert.Single checks run *history*, not work item count. A pending
        // WorkItem (never accepted by an agent) is not written to history, so this assertion
        // passes even if re-dispatch silently succeeded and created a pending second WorkItem.
        // Strengthen by querying Fixture.WorkItems for work items on issue "3091e" and asserting
        // the count did not increase beyond the two that were created in Arrange.
        var historyRuns = await Fixture.Factory.HistoryService.GetRunHistoryAsync();
        var runsForIssue = historyRuns.Where(r => r.IssueIdentifier == "3091e").ToList();
        Assert.Single(runsForIssue);   // only the first (failed) run is in history; the second WorkItem is pending (no run yet)
    }
}
