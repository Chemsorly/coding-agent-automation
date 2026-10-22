using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for live and saved agent output on the Run page (/runs/{id}).
///
/// Scenarios:
///   1. Live panel + saved tail after reload — active run shows the Live output panel; after
///      completion and page reload the Output tail card renders with the persisted lines.
///   2. Output ordering in saved tail — lines sent in two batches appear in insertion order
///      in the OutputTail after the run completes.
///   3. Finished run — the saved OutputTail is shown after completion; no Live output panel.
///   4. Cross-replica — skipped: the multi-replica fixture uses an in-process shared
///      FakeRedisStore which does not exercise the backlog across separate HTTP hosts.
///      The Y6_AppendOutputLines_CapAt500 test in MultiReplicaTests already covers the
///      distributed storage contract. Browser-level cross-replica output is not achievable
///      with the current single-Playwright headful topology.
///
/// Note on live-streaming assertions: testing that output lines appear in the DOM in real
/// time requires the Blazor page's hub subscription (SubscribeLiveAsync) to complete within
/// the test window. This subscription is sensitive to when the WebAssembly client starts
/// and has been observed to be unreliable under CI load (see issue #3108 analysis). Scenarios
/// 1 and 2 therefore verify the structural and persistence behaviour (panel visibility,
/// output ordering in the saved tail) rather than real-time DOM updates.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed partial class AgentOutputTests : E2ETestBase
{
    public AgentOutputTests(E2EFixture fixture) : base(fixture) { }

    // ── Shared arrange helpers ────────────────────────────────────────────

    /// <summary>
    /// Seeds template + profile + issue, connects the agent, dispatches via UI, and waits for the
    /// agent to receive the job assignment. Returns the assignment message and the run ID.
    /// The agent has called <c>AcceptJobAsync</c> by the time this returns.
    /// </summary>
    private async Task<(JobAssignmentMessage assignment, string runId)> SeedDispatchAndAcceptAsync(
        FakeAgentClient agent,
        string issueIdentifier,
        string templateName = "Output Test Template",
        string? projectId = null)
    {
        var targetProjectId = projectId ?? WellKnownIds.DefaultProjectId;
        // TODO: [WARNING] Hardcoded IDs "output-template-1" and "output-profile-e2e" are shared
        // across all three test scenarios. This is safe because ResetAllAsync runs between tests,
        // but if a future test calls this helper twice in a single test body (without an
        // intervening reset), SaveTemplateAsync will silently overwrite the first entry, masking
        // a duplicate-key error in the store. Consider using unique IDs per call (e.g. a Guid
        // suffix) or adding a guard that throws on re-seed to make the single-call-per-test
        // contract explicit.
        await Fixture.ConfigStore.SaveTemplateAsync(targetProjectId, new PipelineJobTemplate
        {
            Id = "output-template-1",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "output-profile-e2e",
            DisplayName = "Output E2E Agent Profile",
            JobTemplate = E2ERouting.Key(new[] { "e2e" }),
            Enabled = true
        });

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueIdentifier,
            Title = $"Output test issue {issueIdentifier}",
            Description = "E2E output coverage",
            Labels = new[] { "enhancement" }
        });

        // Dispatch via the UI
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync(templateName);
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueIdentifier);
        await codingPage.ClickStartPipelineAsync();
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });

        // Wait for the agent to receive the assignment and accept the job
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.AcceptJobAsync(assignment.JobId);

        // Wait for the run to become active so RunService has a registered run
        var runService = Fixture.RunService;
        await WaitUntilAsync(
            () => runService.GetActiveRuns().Any(r => r.IssueIdentifier == issueIdentifier),
            TimeSpan.FromSeconds(20));

        // TODO: [WARNING] TOCTOU gap: GetActiveRuns() is called twice — once to wait until a run
        // exists and again below to read the RunId. Between the two calls another thread (e.g.
        // HandleJobCompletedAsync racing a very fast agent) could remove the run from the active
        // set, causing First() to throw InvalidOperationException. Use FirstOrDefault() and assert
        // non-null, or capture the whole run in a single atomic call inside the WaitUntilAsync
        // predicate and return it directly.
        var runId = runService.GetActiveRuns()
            .First(r => r.IssueIdentifier == issueIdentifier).RunId;

        return (assignment, runId);
    }

    // ── Scenario 1: Live panel visibility + saved tail after reload ──────────────

    /// <summary>
    /// Scenario 1 — live panel and saved tail.
    ///
    /// While a run is active, the Run page shows the "Live output" card (even before any lines
    /// arrive — the placeholder "Waiting for output…" renders immediately). After the run completes
    /// and the page is reloaded, the "Live output" card disappears and the "Agent output" tail
    /// card renders with the persisted lines.
    ///
    /// This scenario exercises the two structural states of the run page's output section:
    /// active → live panel present; completed → tail card present with content.
    ///
    /// Note: live-streaming assertions (lines appearing in the DOM in real time) are intentionally
    /// omitted. The Blazor page subscribes to the hub inside SubscribeLiveAsync which runs in
    /// OnAfterRenderAsync; this hub subscription is sensitive to when the WebAssembly client
    /// starts and has proven unreliable under CI load. The structural and persistence behaviours
    /// tested here are load-independent and provide stable coverage of the same output feature.
    /// </summary>
    [Fact]
    public async Task Scenario1_LivePanelVisible_ThenSavedTailAfterReload()
    {
        // Arrange
        await using var agent = new FakeAgentClient("output-agent-s1", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (assignment, runId) = await SeedDispatchAndAcceptAsync(agent, "out-s1");
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.CloningRepository);

        // Navigate to the run detail page while the run is still active.
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // Assert: the Live output card is visible for an active run (renders even before lines).
        Assert.True(await detail.HasLiveOutputPanelAsync(),
            "Live output card must be visible while the run is active");

        // Assert: no OutputTail card while the run is active (it only renders when _isLive is false).
        Assert.False(await detail.HasOutputTailCardAsync(),
            "Output tail card must not be visible while the run is active");

        // Act: agent sends output lines and completes the run.
        await agent.ReportOutputAsync(assignment.JobId, "s1-line-1", "s1-line-2", "s1-line-3");
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.Completed);
        await agent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/1",
            RetryCount = 0,
            FilesChangedCount = 1,
            LinesAdded = 5,
            LinesRemoved = 1,
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

        // Wait for the completed run to appear in history before reloading.
        await WaitForHistoryAsync(r => r.IssueIdentifier == "out-s1" && r.FinalStep == PipelineStep.Completed);

        // Reload the page — the interactive render now loads the terminal summary from history
        // (FinalStep == Completed → _isLive == false) and the OutputTail card renders.
        await detail.NavigateAsync(runId);

        // Assert: no live output panel after reload (run is now terminal).
        Assert.False(await detail.HasLiveOutputPanelAsync(),
            "Live output panel must not be shown after the run completes and the page is reloaded");

        // Assert: the saved tail card is visible with the lines sent above.
        Assert.True(await detail.HasOutputTailCardAsync(),
            "Output tail card must be shown for a completed run that produced output");

        var savedLines = await detail.GetOutputTailLinesAsync();
        Assert.Contains("s1-line-1", savedLines);
        Assert.Contains("s1-line-2", savedLines);
        Assert.Contains("s1-line-3", savedLines);
    }

    // ── Scenario 2: Output line order preserved in the saved tail ─────────

    /// <summary>
    /// Scenario 2 — output ordering in the saved tail.
    ///
    /// Lines sent in two separate batches during a run appear in the saved OutputTail in the
    /// correct order after completion: first batch lines come before second batch lines.
    ///
    /// This verifies that the run's ring buffer maintains insertion order and that
    /// PipelineRun.ToSummary() captures the tail correctly for display.
    /// </summary>
    [Fact]
    public async Task Scenario2_OutputLinesOrderPreservedInSavedTail()
    {
        // Arrange
        await using var agent = new FakeAgentClient("output-agent-s2", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (assignment, runId) = await SeedDispatchAndAcceptAsync(agent, "out-s2");
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.CloningRepository);

        // Act: send two batches of lines, then complete the run.
        await agent.ReportOutputAsync(assignment.JobId, "batch-a-1", "batch-a-2", "batch-a-3");
        await agent.ReportOutputAsync(assignment.JobId, "batch-b-1", "batch-b-2");

        // Wait for the backlog to contain all 5 lines before completing the run, so they
        // are captured in OutputTail by PipelineRun.ToSummary() inside RunTerminalCleanupAsync.
        var runService = Fixture.RunService;
        await WaitUntilAsync(
            (Func<Task<bool>>)(async () =>
                (await runService.GetOutputBacklogAsync(new CodingAgent.Pipeline.Models.RunId(runId))).Count >= 5),
            TimeSpan.FromSeconds(10));

        await agent.ReportStepAsync(assignment.JobId, PipelineStep.Completed);
        await agent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/2",
            RetryCount = 0,
            FilesChangedCount = 1,
            LinesAdded = 3,
            LinesRemoved = 1,
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

        await WaitForHistoryAsync(r => r.IssueIdentifier == "out-s2" && r.FinalStep == PipelineStep.Completed);

        // Navigate directly to the completed run page.
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // Assert: tail card is present with all 5 lines.
        Assert.True(await detail.HasOutputTailCardAsync(),
            "Output tail card must be visible for a completed run with output");

        var savedLines = (await detail.GetOutputTailLinesAsync()).ToList();

        Assert.True(savedLines.Count >= 5,
            $"Expected at least 5 lines in the output tail, got {savedLines.Count}: [{string.Join(", ", savedLines)}]");

        // All 5 lines from both batches must be present.
        Assert.Contains("batch-a-1", savedLines);
        Assert.Contains("batch-a-2", savedLines);
        Assert.Contains("batch-a-3", savedLines);
        Assert.Contains("batch-b-1", savedLines);
        Assert.Contains("batch-b-2", savedLines);

        // Order: batch A lines come before batch B lines (insertion order preserved).
        Assert.True(savedLines.IndexOf("batch-a-3") < savedLines.IndexOf("batch-b-1"),
            "Batch A must appear before Batch B in the tail (insertion order preserved)");
    }

    // ── Scenario 3: Finished run shows saved OutputTail ───────────────────

    /// <summary>
    /// Scenario 3 — finished run.
    /// After a run completes, /runs/{id} shows the saved OutputTail (not a live panel).
    /// The "Cancel Pipeline" button is also absent on a completed run.
    /// </summary>
    [Fact]
    public async Task Scenario3_FinishedRun_ShowsSavedOutputTail_NoLivePanel()
    {
        // Arrange
        await using var agent = new FakeAgentClient("output-agent-s3", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (assignment, runId) = await SeedDispatchAndAcceptAsync(agent, "out-s3");

        // Report some output lines while the run is in progress
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);
        await agent.ReportOutputAsync(assignment.JobId, "output-before-complete-1", "output-before-complete-2");

        // Complete the run — this persists OutputTail via PipelineRun.ToSummary
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.Completed);
        await agent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/42",
            RetryCount = 0,
            FilesChangedCount = 1,
            LinesAdded = 10,
            LinesRemoved = 2,
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

        // TODO: [WARNING] WaitForHistoryAsync only guarantees FinalStep == Completed in the history
        // service. The OutputTail on PipelineRunSummary is set by PipelineRun.ToSummary() inside
        // RunTerminalCleanupAsync. If ReportOutputLines writes to run.OutputLines asynchronously
        // after RunTerminalCleanupAsync has already snapshotted the run, OutputTail will be null,
        // HasOutputTailCardAsync will return false, and the test will silently fail with no
        // diagnostic about the root cause. Consider asserting savedLines.Count > 0 before the
        // Assert.Contains calls, and adding a clear failure message that identifies a null OutputTail
        // as the cause.
        // Wait until history has the completed run
        await WaitForHistoryAsync(r => r.IssueIdentifier == "out-s3" && r.FinalStep == PipelineStep.Completed);

        // Navigate to the run detail page
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // Wait for the page to fully render the terminal state
        // (RunPage reloads the summary via OnRunCompleted, or the initial load already has terminal step).
        // Uses the async WaitUntilAsync overload (Func<Task<bool>>) explicitly to avoid the compiler
        // silently coercing the async lambda to Func<bool> (which always returns a truthy Task<bool>
        // object and exits immediately without polling).
        await WaitUntilAsync(
            (Func<Task<bool>>)(async () => !await detail.HasLiveOutputPanelAsync()),
            TimeSpan.FromSeconds(20));

        // Assert: no live output panel for a finished run
        Assert.False(await detail.HasLiveOutputPanelAsync(),
            "Live output panel should not be shown for a completed run");

        // Assert: the saved output tail card is visible
        Assert.True(await detail.HasOutputTailCardAsync(),
            "Output tail card should be shown for a completed run that produced output");

        // Assert: the saved lines are displayed
        var savedLines = await detail.GetOutputTailLinesAsync();
        Assert.Contains("output-before-complete-1", savedLines);
        Assert.Contains("output-before-complete-2", savedLines);

        // Assert: no Cancel Pipeline button on a finished run
        Assert.False(await detail.IsCancelButtonVisibleAsync(),
            "Cancel Pipeline button should not be visible for a completed run");
    }

    // ── Scenario 4: the hub connection drops and comes back ───────────────────────

    /// <summary>
    /// Spec 052 Req 4.2, 4.3, 9.5: the page's UI hub WebSocket (through the web host) is cut, as when the
    /// web pod that carried it stops. The page is not reloaded and keeps its state; SignalR reconnects,
    /// rejoins the run's group, and live output resumes.
    /// </summary>
    [Fact]
    public async Task Scenario4_DroppedHubConnection_KeepsThePage_AndResumesLiveOutput()
    {
        var hubSockets = new System.Collections.Concurrent.ConcurrentQueue<Microsoft.Playwright.IWebSocketRoute>();
        await Page.RouteWebSocketAsync(UiHubSocket(), ws =>
        {
            ws.ConnectToServer();
            hubSockets.Enqueue(ws);
        });

        await using var agent = new FakeAgentClient("output-agent-s4", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var (assignment, runId) = await SeedDispatchAndAcceptAsync(agent, "out-s4");
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.CloningRepository);

        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);
        Assert.True(await detail.HasLiveOutputPanelAsync(), "Live output card must be visible while the run is active");
        var liveLog = Page.Locator("pre.run-live-log");

        await ReportUntilShownAsync(agent, assignment.JobId, liveLog, "s4-before-drop");

        // Page state that a reload would lose.
        await Page.EvaluateAsync("() => { window.__e2eKept = 'kept'; }");

        var socketsBefore = hubSockets.Count;
        await hubSockets.Last().CloseAsync(new() { Code = 1006, Reason = "web pod stopped" });
        await WaitUntilAsync(() => hubSockets.Count > socketsBefore, TimeSpan.FromSeconds(30));

        await ReportUntilShownAsync(agent, assignment.JobId, liveLog, "s4-after-reconnect");
        Assert.Equal("kept", await Page.EvaluateAsync<string>("() => window.__e2eKept"));
        Assert.Contains("s4-before-drop", await liveLog.InnerTextAsync());
    }

    /// <summary>
    /// Reports a numbered line until one shows in the live log: right after a (re)connect the page may not
    /// have joined the run's group yet, and a line sent before that is not delivered to it.
    /// </summary>
    private static async Task ReportUntilShownAsync(FakeAgentClient agent, string jobId, Microsoft.Playwright.ILocator liveLog, string prefix)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        for (var attempt = 1; DateTime.UtcNow < deadline; attempt++)
        {
            await agent.ReportOutputAsync(jobId, $"{prefix}-{attempt}");
            try
            {
                await Microsoft.Playwright.Assertions.Expect(liveLog).ToContainTextAsync(prefix, new() { Timeout = 2_000 });
                return;
            }
            catch (Microsoft.Playwright.PlaywrightException) when (DateTime.UtcNow < deadline)
            {
                // Not delivered yet: send the next line.
            }
        }
        throw new TimeoutException($"No '{prefix}' line reached the live log within 30 s.");
    }

    // ── Scenario 5: the hub connection ends with the session ──────────────────────

    /// <summary>
    /// Spec 052 Req 3.4, 9.5, 14.3: T5 through the BFF. The run page's hub connection is forwarded with a
    /// hub token that ends with the session, so the API closes it at expiry (the API side is
    /// UiHubTests); the page then reaches the login page through the session heartbeat, without any user
    /// action. The browser is re-signed in as the project's operator with a 10-second session.
    /// </summary>
    [Fact]
    public async Task Scenario5_HubConnection_ClosesAtSessionExpiry_AndThePageReachesTheLoginPage()
    {
        await using var agent = new FakeAgentClient("output-agent-s5", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        // Seed the run in the E2E Team project so the TeamGroup scoped-operator can view it.
        var (assignment, runId) = await SeedDispatchAndAcceptAsync(agent, "out-s5", projectId: E2ETestSignIn.TeamProjectId);
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.CloningRepository);

        // The API ends the connection with SignalR's close message, after which either side may close the
        // socket first; with an OnClose handler set, the route forwards nothing itself.
        var hubClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Page.RouteWebSocketAsync(UiHubSocket(), ws =>
        {
            var server = ws.ConnectToServer();
            server.OnClose((code, reason) =>
            {
                hubClosed.TrySetResult();
                _ = ws.CloseAsync(new() { Code = code, Reason = reason });
            });
            ws.OnClose((code, reason) =>
            {
                hubClosed.TrySetResult();
                _ = server.CloseAsync(new() { Code = code, Reason = reason });
            });
        });

        // A request, not a page load: the context's request API shares the browser's cookie jar.
        var signIn = await Page.Context.APIRequest.GetAsync(E2ETestSignIn.SignInUrl(BaseUrl, "e2e-operator", E2ETestSignIn.TeamGroup) + "&ttl=10");
        Assert.True(signIn.Ok, $"test sign-in answered {signIn.Status}");
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);
        Assert.True(await detail.HasLiveOutputPanelAsync(), "the run page subscribes to the live run over the hub");

        await hubClosed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Page.WaitForURLAsync(LoginRedirect(), new() { Timeout = 50_000 });
    }

    [System.Text.RegularExpressions.GeneratedRegex("/hubs/ui")]
    private static partial System.Text.RegularExpressions.Regex UiHubSocket();

    [System.Text.RegularExpressions.GeneratedRegex(@"/login\?returnUrl=")]
    private static partial System.Text.RegularExpressions.Regex LoginRedirect();
}
