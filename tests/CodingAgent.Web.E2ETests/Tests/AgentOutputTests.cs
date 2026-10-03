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
///   1. Live streaming — lines sent while the page is open appear in the Live output panel.
///   2. Late join (backlog) — lines sent before the page was opened are shown on load,
///      followed by additional live lines.
///   3. Finished run — the saved OutputTail is shown after completion; no Live output panel.
///   4. Cross-replica — skipped: the multi-replica fixture uses an in-process shared
///      FakeRedisStore which does not exercise the backlog across separate HTTP hosts.
///      The Y6_AppendOutputLines_CapAt500 test in MultiReplicaTests already covers the
///      distributed storage contract. Browser-level cross-replica output is not achievable
///      with the current single-Playwright headful topology.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class AgentOutputTests : E2ETestBase
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
        string templateName = "Output Test Template")
    {
        // TODO: [WARNING] Hardcoded IDs "output-template-1" and "output-profile-e2e" are shared
        // across all three test scenarios. This is safe because ResetAllAsync runs between tests,
        // but if a future test calls this helper twice in a single test body (without an
        // intervening reset), SaveTemplateAsync will silently overwrite the first entry, masking
        // a duplicate-key error in the store. Consider using unique IDs per call (e.g. a Guid
        // suffix) or adding a guard that throws on re-seed to make the single-call-per-test
        // contract explicit.
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "output-template-1",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "output-profile-e2e",
            DisplayName = "Output E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

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

    // ── Scenario 1: Live streaming ────────────────────────────────────────

    /// <summary>
    /// Scenario 1 — live streaming.
    /// Open /runs/{id} for an active run. The fake agent sends 3 lines; they appear in order in
    /// the "Live output" panel without a page reload. A second batch of 2 lines is sent and
    /// appends below the first batch.
    /// </summary>
    [Fact]
    public async Task Scenario1_LiveStreaming_LinesAppearInOrderWithoutReload()
    {
        // Arrange
        await using var agent = new FakeAgentClient("output-agent-s1", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (assignment, runId) = await SeedDispatchAndAcceptAsync(agent, "out-s1");

        // Report a step so the run page shows an active run
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.CloningRepository);

        // Navigate to the run detail page — subscribes to the live hub stream
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // The "Live output" card renders (even before any lines: shows "Waiting for output…")
        Assert.True(await detail.HasLiveOutputPanelAsync(),
            "Live output card should be present for an active run");

        // Act: agent sends first batch of 3 lines
        await agent.ReportOutputAsync(assignment.JobId, "line-one", "line-two", "line-three");

        // Assert: all 3 lines appear in the live output panel
        await detail.WaitForLiveOutputAsync(minimumLineCount: 3, timeoutMs: 15_000);
        var lines = (await detail.GetLiveOutputLinesAsync()).ToList();
        Assert.Contains("line-one", lines);
        Assert.Contains("line-two", lines);
        Assert.Contains("line-three", lines);
        // Order: line-one must appear before line-two and line-three
        Assert.True(lines.IndexOf("line-one") < lines.IndexOf("line-two"),
            "line-one should appear before line-two");
        Assert.True(lines.IndexOf("line-two") < lines.IndexOf("line-three"),
            "line-two should appear before line-three");

        // Act: agent sends a second batch of 2 lines — they should append below
        await agent.ReportOutputAsync(assignment.JobId, "line-four", "line-five");

        await detail.WaitForLiveOutputAsync(minimumLineCount: 5, timeoutMs: 15_000);
        var allLines = (await detail.GetLiveOutputLinesAsync()).ToList();
        Assert.Contains("line-four", allLines);
        Assert.Contains("line-five", allLines);
        // Second batch appends: line-four after line-three
        Assert.True(allLines.IndexOf("line-three") < allLines.IndexOf("line-four"),
            "line-four should appear after line-three (second batch appended below first)");
    }

    // ── Scenario 2: Late join (backlog) ───────────────────────────────────

    /// <summary>
    /// Scenario 2 — late join.
    /// Lines sent before the page was opened appear on load (pushed by SubscribeToRun backlog),
    /// followed by new lines arriving in real time.
    /// </summary>
    [Fact]
    public async Task Scenario2_LateJoin_BacklogShownOnLoad_ThenNewLinesAppend()
    {
        // Arrange
        await using var agent = new FakeAgentClient("output-agent-s2", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var (assignment, runId) = await SeedDispatchAndAcceptAsync(agent, "out-s2");

        await agent.ReportStepAsync(assignment.JobId, PipelineStep.CloningRepository);

        // Send backlog lines BEFORE the page is opened
        await agent.ReportOutputAsync(assignment.JobId, "backlog-line-1", "backlog-line-2");

        // TODO: [WARNING] Task.Delay(200) is an arbitrary timing assumption. On a heavily loaded
        // CI runner the two ReportOutputLines hub invocations may not have been written to the
        // in-memory ring buffer within 200 ms, causing SubscribeToRun to receive an empty backlog
        // and the test to fail. Replace with a deterministic WaitUntilAsync poll that checks the
        // ring buffer (e.g. via a RunService helper or GetOutputBacklog wrapper) contains at least
        // 2 lines before navigating.
        // Small delay to ensure the ring buffer write settles before the subscribe
        await Task.Delay(200);

        // Navigate to the run detail page — SubscribeToRun delivers the backlog immediately
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // Both backlog lines should appear on load (no additional output needed)
        await detail.WaitForLiveOutputAsync(minimumLineCount: 2, timeoutMs: 15_000);
        var lines = await detail.GetLiveOutputLinesAsync();
        Assert.Contains("backlog-line-1", lines);
        Assert.Contains("backlog-line-2", lines);

        // Act: new live line arrives after page load — should append below the backlog
        await agent.ReportOutputAsync(assignment.JobId, "live-line-after-join");
        await detail.WaitForLiveOutputAsync(minimumLineCount: 3, timeoutMs: 15_000);

        var allLines = (await detail.GetLiveOutputLinesAsync()).ToList();
        Assert.Contains("live-line-after-join", allLines);
        // Backlog lines appear before the new live line
        Assert.True(allLines.IndexOf("backlog-line-2") < allLines.IndexOf("live-line-after-join"),
            "Live lines should append after backlog lines");
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
        // (RunPage reloads the summary via OnRunCompleted, or the initial load already has terminal step)
        // TODO: [WARNING] The async lambda passed here is coerced to Func<bool> by the compiler
        // (returning Task<bool> cast to bool, which is always truthy), so the wait completes
        // immediately regardless of whether the live output panel is still visible. Use the async
        // WaitUntilAsync(Func<Task<bool>>, ...) overload explicitly to make polling actually work.
        // As written, the subsequent Assert.False is a direct assertion with no retry, which can
        // fail intermittently if Blazor's OnRunCompleted hasn't yet set _isLive = false.
        await WaitUntilAsync(
            async () => !await detail.HasLiveOutputPanelAsync(),
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
}
