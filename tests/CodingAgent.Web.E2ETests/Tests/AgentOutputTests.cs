using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for live and saved agent output on the Run page (issue #3108).
///
/// Scenarios covered:
/// 1. <b>Live streaming</b>: lines sent by the fake agent appear in order on an open Run page
///    without a reload, and a second batch appends below the first.
/// 2. <b>Late join</b>: lines sent before the page was opened are present on load (backlog),
///    followed by new lines sent after navigation.
/// 3. <b>Finished run</b>: after completion the page shows the saved output tail card, not the
///    live panel, and the Cancel button is absent.
///
/// Scenario 4 (cross-replica): the multi-replica fixture is a shared-FakeRedis topology without
/// a Playwright browser; live SignalR output delivery to a browser page on a different replica
/// would require a full two-browser setup that is not supported by the current fixture design.
/// That scenario is therefore skipped.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class AgentOutputTests : E2ETestBase
{
    public AgentOutputTests(E2EFixture fixture) : base(fixture) { }

    // ── Shared arrange helper ─────────────────────────────────────────────

    /// <summary>
    /// Seeds a template/profile/issue, dispatches via the UI, and advances the agent to the
    /// <see cref="PipelineStep.GeneratingCode"/> step. Returns the active run's ID once the server
    /// reflects the step. The caller is responsible for disposing <paramref name="fakeAgent"/>.
    /// </summary>
    private async Task<(string runId, string jobId)> SeedDispatchAndActivateAsync(
        FakeAgentClient fakeAgent,
        string templateName,
        string issueId)
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-output",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-output",
            DisplayName = "Output Test Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = $"Output test issue {issueId}",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync(templateName);
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueId);
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });

        // TODO [WARNING]: WaitAsync is called without a CancellationToken. If the assignment never
        // arrives (e.g. hub registration races with dispatch under CI load), this throws a generic
        // TimeoutException after 30s with no indication of which test or run was waiting.
        // Consider passing a linked CancellationToken with a descriptive timeout exception message.
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        var runService = Fixture.RunService;
        await WaitUntilAsync(() =>
            runService.GetActiveRuns().Any(r => r.IssueIdentifier == issueId && r.CurrentStep == PipelineStep.GeneratingCode));

        var runId = runService.GetActiveRuns().First(r => r.IssueIdentifier == issueId).RunId;
        return (runId, assignment.JobId);
    }

    // ── Scenario 1: Live streaming ────────────────────────────────────────

    /// <summary>
    /// Scenario 1: Open /runs/{id} for an active run. The fake agent sends 3 lines; they appear
    /// on the page in order without a reload. A second batch of 2 lines then appends below the
    /// first batch.
    /// </summary>
    [Fact]
    public async Task LiveOutput_LinesAppearInOrder_AndSecondBatchAppends()
    {
        // Arrange: dispatch a job and navigate to the Run page while it is still active.
        await using var fakeAgent = new FakeAgentClient("output-agent-1", "e2e");
        var (runId, jobId) = await SeedDispatchAndActivateAsync(fakeAgent, "Live Output Template", "200");

        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // Wait for the live output panel to be present (the Blazor circuit subscribes
        // to the run group, making the "Live output" card visible).
        await detail.WaitForLiveOutputPanelAsync(timeoutMs: 15_000);

        // Act: send the first batch of 3 lines.
        await fakeAgent.ReportOutputAsync(jobId, "line-one", "line-two", "line-three");

        // Assert: all 3 lines appear on the page without a reload.
        await detail.WaitForLiveOutputLineCountAsync(3, timeoutMs: 15_000);
        var lines = await detail.GetOutputLinesAsync();
        Assert.Equal(3, lines.Count);
        Assert.Equal("line-one", lines[0]);
        Assert.Equal("line-two", lines[1]);
        Assert.Equal("line-three", lines[2]);

        // Act: send a second batch of 2 more lines.
        await fakeAgent.ReportOutputAsync(jobId, "line-four", "line-five");

        // Assert: the second batch is appended — 5 lines total, order preserved.
        await detail.WaitForLiveOutputLineCountAsync(5, timeoutMs: 15_000);
        lines = await detail.GetOutputLinesAsync();
        Assert.Equal(5, lines.Count);
        Assert.Equal("line-one", lines[0]);
        Assert.Equal("line-four", lines[3]);
        Assert.Equal("line-five", lines[4]);
    }

    // ── Scenario 2: Late join ─────────────────────────────────────────────

    /// <summary>
    /// Scenario 2: Lines sent before the Run page is opened are shown on load (backlog), followed
    /// by new lines sent after navigation.
    /// </summary>
    [Fact]
    public async Task LateJoin_BacklogLinesShownOnLoad_ThenNewLinesAppend()
    {
        // Arrange: dispatch a job, send 2 lines BEFORE opening the Run page.
        await using var fakeAgent = new FakeAgentClient("output-agent-2", "e2e");
        var (runId, jobId) = await SeedDispatchAndActivateAsync(fakeAgent, "Late Join Template", "201");

        // Send backlog lines before navigating to the Run page.
        await fakeAgent.ReportOutputAsync(jobId, "backlog-one", "backlog-two");

        // Wait for the hub to process the lines server-side (enqueued into run.OutputLines).
        // The SubscribeToRun snapshot will carry these lines to the browser page.
        var runService = Fixture.RunService;
        // TODO [WARNING]: This polls run.OutputLines.Count via GetActiveRuns(), which relies on
        // reference identity — the hub must enqueue into the same ActiveRun object returned here.
        // If AppendOutputLines stores to a copy, this condition never becomes true and the test
        // times out with an opaque "Condition not met" message. A more robust alternative would
        // be to poll GetOutputBacklogAsync (the same path SubscribeToRun uses) instead.
        await WaitUntilAsync(() =>
        {
            var run = runService.GetActiveRuns().FirstOrDefault(r => r.RunId == runId);
            return run is not null && run.OutputLines.Count >= 2;
        }, timeout: TimeSpan.FromSeconds(15));

        // Act: navigate to the Run page (late join).
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // Wait for the live output panel (the SubscribeToRun response seeds the backlog
        // and the "Live output" card becomes visible).
        await detail.WaitForLiveOutputPanelAsync(timeoutMs: 15_000);

        // The backlog should be shown on load.
        await detail.WaitForLiveOutputLineCountAsync(2, timeoutMs: 15_000);
        var lines = await detail.GetOutputLinesAsync();
        // TODO [WARNING]: Assert.Contains does not verify count — test passes even if only one
        // backlog line is rendered (partial-flush race). Consider Assert.Equal(2, lines.Count)
        // before the Contains checks to anchor the full backlog count.
        Assert.Contains("backlog-one", lines);
        Assert.Contains("backlog-two", lines);
        var backlogCount = lines.Count;

        // Act: send a new line after the page is open.
        await fakeAgent.ReportOutputAsync(jobId, "live-after-join");

        // Assert: the new line appends after the backlog.
        await detail.WaitForLiveOutputLineCountAsync(backlogCount + 1, timeoutMs: 15_000);
        lines = await detail.GetOutputLinesAsync();
        Assert.Contains("live-after-join", lines);

        // Ordering: "live-after-join" must appear after "backlog-two".
        var lineList = lines.ToList();
        var liveAfterIdx = lineList.IndexOf("live-after-join");
        // TODO [WARNING]: IndexOf returns -1 if the string is absent. If "backlog-two" is missing
        // from rendered lines, backlogTwoIdx == -1 and -1 < liveAfterIdx makes the ordering
        // assertion trivially true in the wrong case. Add Assert.True(backlogTwoIdx >= 0) as a
        // precondition, or use an explicit expected-lines list assertion instead.
        var backlogTwoIdx = lineList.IndexOf("backlog-two");
        Assert.True(liveAfterIdx > backlogTwoIdx, "New line should appear after the backlog");
    }

    // ── Scenario 3: Finished run ──────────────────────────────────────────

    /// <summary>
    /// Scenario 3: After the run completes, the page shows the saved output tail card (not the
    /// live panel), and the Cancel button is absent.
    /// </summary>
    [Fact]
    public async Task FinishedRun_ShowsSavedOutputTail_AndNoCancelButton()
    {
        // Arrange: dispatch a job, send output lines, then complete the run.
        await using var fakeAgent = new FakeAgentClient("output-agent-3", "e2e");
        var (runId, jobId) = await SeedDispatchAndActivateAsync(fakeAgent, "Finished Run Template", "202");

        // Send output lines during the active run.
        await fakeAgent.ReportOutputAsync(jobId, "saved-line-one", "saved-line-two", "saved-line-three");

        // Allow the hub to enqueue lines into run.OutputLines so ToSummary() captures them.
        var runService = Fixture.RunService;
        // TODO [WARNING]: Same reference-identity assumption as Scenario 2 — WaitUntilAsync polls
        // run.OutputLines.Count via GetActiveRuns(). If the hub stores lines in a different object
        // than what GetActiveRuns() returns, this never becomes true and the timeout message is
        // opaque. Additionally, if the count never reaches 3 here, the run completes with an empty
        // OutputTail and the later Assert.Contains("saved-line-one") fails with a misleading error.
        // A more robust alternative would be to poll GetOutputBacklogAsync instead.
        await WaitUntilAsync(() =>
        {
            var run = runService.GetActiveRuns().FirstOrDefault(r => r.RunId == runId);
            return run is not null && run.OutputLines.Count >= 3;
        }, timeout: TimeSpan.FromSeconds(15));

        // Act: navigate to the Run page while still active (establishes the Blazor circuit
        // and subscribes to the run group).
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);
        await detail.WaitForLiveOutputPanelAsync(timeoutMs: 15_000);

        // Complete the run.
        await fakeAgent.ReportStepAsync(jobId, PipelineStep.Completed);
        await fakeAgent.ReportCompletionAsync(jobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/1",
            RetryCount = 0,
            FilesChangedCount = 2,
            LinesAdded = 20,
            LinesRemoved = 5,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>(),
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 0,
            CodeReviewSuggestionCount = 0
        });

        // Wait for the run to appear in history (confirms OnCompleted fired and the summary
        // with OutputTail was persisted).
        await WaitForHistoryAsync(r => r.RunId == runId);

        // Assert: after completion the live panel is gone and the saved output tail card appears.
        // RunPage.razor transitions _isLive = false on OnCompleted and reloads the terminal
        // summary, which has OutputTail populated from the enqueued OutputLines.
        await Page.WaitForFunctionAsync(
            "() => !!document.querySelector('[data-testid=\"output-tail-card\"]')",
            null,
            new() { Timeout = 15_000 });

        Assert.True(await detail.HasSavedOutputCardAsync(), "Finished run should show the saved output tail card");
        Assert.False(await detail.HasLiveOutputPanelAsync(), "Finished run should NOT show the live output panel");

        // The saved output should contain the lines sent before completion.
        var savedLines = await detail.GetOutputLinesAsync();
        // TODO [WARNING]: Assert.True(savedLines.Count > 0) is too weak — the three Contains
        // assertions below already imply non-empty. If the tail card renders with fewer than 3
        // lines (e.g. due to tail-capacity misconfiguration), this passes. Replace with
        // Assert.Equal(3, savedLines.Count) for a precise count assertion.
        Assert.True(savedLines.Count > 0, "Saved output should be non-empty");
        Assert.Contains("saved-line-one", savedLines);
        Assert.Contains("saved-line-two", savedLines);
        Assert.Contains("saved-line-three", savedLines);

        // The Cancel button should not be visible for a completed run.
        Assert.False(await detail.IsCancelButtonVisibleAsync(), "Cancel button should not be visible after completion");
    }
}
