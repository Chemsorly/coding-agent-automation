using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for live and saved agent output on the Run page (/runs/{runId}).
///
/// Covers three scenarios:
/// <list type="number">
///   <item>Live streaming — output sent while the browser is open appears in real time.</item>
///   <item>Late join / backlog — output sent before the browser navigated is shown on load.</item>
///   <item>Finished run — saved output tail is shown after the run completes.</item>
/// </list>
///
/// Scenario 4 (cross-replica browser) is not implemented here. The
/// <see cref="MultiReplicaE2EFixture"/> starts no Blazor monolith and no Playwright — it is
/// designed for headless/service-layer assertions only. A browser cross-replica test would require
/// both the Blazor app and the API to share the same <c>FakeRedisStore</c>, which requires
/// fixture refactoring beyond the scope of this issue. The service-layer variant of Scenario 4
/// (output backlog visible cross-replica) is already covered by
/// <c>MultiReplicaTests.X6_GetOutputBacklog_CrossReplica_ReturnsLinesWrittenByOtherReplica</c>.
///
/// Issue identifiers 300–302 are reserved for this class.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class AgentOutputTests : E2ETestBase
{
    public AgentOutputTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Scenario 1 — Live streaming.
    ///
    /// Verifies that output lines sent by the agent via <c>ReportOutputLines</c> appear in the
    /// browser in real time without a page reload, and that a second batch is appended below the
    /// first.
    ///
    /// Path exercised:
    /// FakeAgentClient.ReportOutputAsync → AgentHub.ReportOutputLines →
    /// OutputRingBuffer.AddRange + AppendOutputLines →
    /// _uiContext.Clients.Group.SendAsync(OnOutputLines) →
    /// RunPage.OnOutput → _liveOutput.AddRange → re-render → pre.run-live-log
    /// </summary>
    [Fact]
    public async Task LiveOutput_SendBatches_AppearsInRealTime()
    {
        // Arrange — seed and dispatch using identifier "300" (reserved for this class).
        await using var fakeAgent = new FakeAgentClient("output-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var runId = await SeedDispatchAndActivateAsync(fakeAgent, "Output Template 1", "300");

        var detail = new RunDetailPage(Page, BaseUrl);

        // Navigate to the run page while the run is active.
        await detail.NavigateAsync(runId);

        // The "Live output" card renders immediately for active runs (initially "Waiting for output…").
        Assert.True(await detail.HasLiveOutputPanelAsync(),
            "Live output panel should be visible for an active run");

        // Act — first batch of output lines.
        // TODO [WARNING]: Subscription timing race — RunPage.SubscribeLiveAsync runs in
        // OnAfterRenderAsync and takes a backlog snapshot only once at subscribe time. If the
        // SubscribeToRun call has not completed within NavigateAsync's fixed 1500ms window under
        // CI load, these lines land in the ring buffer but are never re-delivered to this client.
        // Prefer waiting for a deterministic signal (e.g. "Waiting for output…" empty-state
        // rendered, or OnRunStateSnapshot receipt) before sending the first batch, rather than
        // relying on the NavigateAsync sleep. (Correctness review, line 69)
        // TODO [WARNING]: `jobId` is re-queried from GetActiveRuns() redundantly — SeedDispatchAndActivateAsync
        // already returns the same RunId value as `runId`. Reuse `runId` directly to eliminate
        // the redundant lookup and narrow TOCTOU window. Same pattern in Scenarios 2 and 3.
        // (DotNetSpecialist review, line 65; TestQualityReviewer review, line 97)
        var jobId = Fixture.RunService.GetActiveRuns().First(r => r.IssueIdentifier == "300").RunId;
        await fakeAgent.ReportOutputAsync(jobId, "line-1", "line-2", "line-3");

        // Wait for the <pre> to appear — it only renders once the first line arrives.
        await Page.Locator(".cockpit-card:not([data-testid='output-tail-card']) pre.run-live-log")
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        var text1 = await detail.GetLiveOutputLinesTextAsync();
        Assert.NotNull(text1);
        Assert.Contains("line-1", text1);
        Assert.Contains("line-2", text1);
        Assert.Contains("line-3", text1);
        // Lines must appear in order.
        // TODO [WARNING]: IndexOf-based ordering does not verify that lines appear as distinct
        // entries — a renderer joining them without separators (e.g. "line-1line-2line-3") would
        // still pass all Contains and ordering assertions. Consider asserting newline/separator
        // presence between consecutive lines to verify distinct line rendering.
        // (TestQualityReviewer review, line 107)
        Assert.True(text1.IndexOf("line-1", StringComparison.Ordinal) < text1.IndexOf("line-2", StringComparison.Ordinal),
            "line-1 should appear before line-2");
        Assert.True(text1.IndexOf("line-2", StringComparison.Ordinal) < text1.IndexOf("line-3", StringComparison.Ordinal),
            "line-2 should appear before line-3");

        // Act — second batch is appended below the first without a page reload.
        await fakeAgent.ReportOutputAsync(jobId, "line-4", "line-5");

        // Wait for the new lines to appear.
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.cockpit-card:not([data-testid=\"output-tail-card\"]) pre.run-live-log')?.textContent?.includes('line-5')",
            null,
            new() { Timeout = 15_000 });

        var text2 = await detail.GetLiveOutputLinesTextAsync();
        Assert.NotNull(text2);
        Assert.Contains("line-4", text2);
        Assert.Contains("line-5", text2);
        // All five lines present in order.
        var idx1 = text2.IndexOf("line-1", StringComparison.Ordinal);
        var idx4 = text2.IndexOf("line-4", StringComparison.Ordinal);
        var idx5 = text2.IndexOf("line-5", StringComparison.Ordinal);
        Assert.True(idx1 >= 0, "line-1 should still be present after second batch");
        Assert.True(idx1 < idx4, "line-1 should appear before line-4");
        // TODO [WARNING]: idx1 < idx4 does not verify the boundary between first and second batch.
        // If the page rendered batch-2 before batch-1 (e.g. a race where the second SendAsync
        // arrived out of order), idx1 < idx4 could still pass because line-1 is the earliest
        // element. Add an assertion that line-3 (last of batch 1) appears before line-4 (first
        // of batch 2) to verify strict cross-batch ordering.
        // (TestQualityReviewer review, line 131)
        Assert.True(idx4 < idx5, "line-4 should appear before line-5");
    }

    /// <summary>
    /// Scenario 2 — Late join / backlog.
    ///
    /// Verifies that output lines sent before the browser navigated to the run page are loaded
    /// from the backlog on subscribe and appear without additional output being sent.
    ///
    /// Path exercised (backlog):
    /// AgentHub.SubscribeToRun → GetOutputBacklogAsync →
    /// Clients.Client(ConnectionId).SendAsync(OnOutputLines) →
    /// RunPage.OnOutput → _liveOutput.AddRange → re-render → pre.run-live-log
    /// </summary>
    [Fact]
    public async Task LateJoin_BacklogLines_ShownOnNavigation()
    {
        // Arrange — seed and dispatch using identifier "301" (reserved for this class).
        await using var fakeAgent = new FakeAgentClient("output-agent-2", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var runId = await SeedDispatchAndActivateAsync(fakeAgent, "Output Template 2", "301");
        // TODO [WARNING]: `jobId` is re-queried from GetActiveRuns() redundantly — SeedDispatchAndActivateAsync
        // already returns the same RunId value as `runId`. Reuse `runId` directly to eliminate
        // the redundant lookup and narrow TOCTOU window. Same pattern in Scenarios 1 and 3.
        // (DotNetSpecialist review, line 65; TestQualityReviewer review, line 97)
        var jobId = Fixture.RunService.GetActiveRuns().First(r => r.IssueIdentifier == "301").RunId;

        // Send output BEFORE navigating to the run page.
        await fakeAgent.ReportOutputAsync(jobId, "before-1", "before-2");

        // Small wait to ensure the hub has processed the lines and they are in the ring buffer /
        // backlog store before SubscribeToRun is called by the page.
        // TODO [WARNING]: This is a fixed-duration sleep rather than a state-based wait. On a
        // loaded CI runner 500 ms may be insufficient; use WaitUntilAsync polling the ring buffer
        // output count reaching 2 for a deterministic synchronisation point.
        // (TestQualityReviewer review, line 148; Correctness review, line 148)
        await Task.Delay(500);

        // Navigate — SubscribeToRun fires in OnAfterRenderAsync; the hub immediately pushes the
        // backlog to this new subscriber via GetOutputBacklogAsync.
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        // The backlog lines should appear without sending any further output.
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.cockpit-card:not([data-testid=\"output-tail-card\"]) pre.run-live-log')?.textContent?.includes('before-1')",
            null,
            new() { Timeout = 15_000 });

        var backlogText = await detail.GetLiveOutputLinesTextAsync();
        Assert.NotNull(backlogText);
        Assert.Contains("before-1", backlogText);
        Assert.Contains("before-2", backlogText);
        Assert.True(
            backlogText.IndexOf("before-1", StringComparison.Ordinal) < backlogText.IndexOf("before-2", StringComparison.Ordinal),
            "before-1 should appear before before-2");

        // Send a new line after navigating — it must be appended below the backlog.
        await fakeAgent.ReportOutputAsync(jobId, "after-1");

        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.cockpit-card:not([data-testid=\"output-tail-card\"]) pre.run-live-log')?.textContent?.includes('after-1')",
            null,
            new() { Timeout = 15_000 });

        var updatedText = await detail.GetLiveOutputLinesTextAsync();
        Assert.NotNull(updatedText);
        Assert.Contains("after-1", updatedText);
        Assert.True(
            updatedText.IndexOf("before-2", StringComparison.Ordinal) < updatedText.IndexOf("after-1", StringComparison.Ordinal),
            "before-2 should appear before after-1");
    }

    /// <summary>
    /// Scenario 3 — Finished run saved output.
    ///
    /// Verifies that after a run completes, the page transitions from the live panel to the saved
    /// output tail card, which contains the lines sent before completion.
    ///
    /// Path exercised (saved output):
    /// HandleJobCompletedAsync → AddRunToHistoryAsync → PipelineRun.ToSummary() →
    /// OutputTail → GetRunAsync → RunPage loads run.OutputTail →
    /// data-testid="output-tail-card" renders → pre.run-live-log
    ///
    /// Critical ordering: output must be sent BEFORE completion.  After HandleJobCompletedAsync
    /// the run is removed from the active store; lines sent after that point are silently dropped
    /// (GetRun returns null), leaving OutputTail null and causing HasOutputTailCardAsync to fail.
    /// </summary>
    [Fact]
    public async Task FinishedRun_SavedOutputTail_ShownAfterCompletion()
    {
        // Arrange — seed and dispatch using identifier "302" (reserved for this class).
        await using var fakeAgent = new FakeAgentClient("output-agent-3", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var runId = await SeedDispatchAndActivateAsync(fakeAgent, "Output Template 3", "302");
        // TODO [WARNING]: `jobId` is re-queried from GetActiveRuns() redundantly — SeedDispatchAndActivateAsync
        // already returns the same RunId value as `runId`. Reuse `runId` directly to eliminate
        // the redundant lookup and narrow TOCTOU window. Same pattern in Scenarios 1 and 2.
        // (DotNetSpecialist review, line 65; TestQualityReviewer review, line 97)
        var jobId = Fixture.RunService.GetActiveRuns().First(r => r.IssueIdentifier == "302").RunId;

        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);

        Assert.True(await detail.HasLiveOutputPanelAsync(),
            "Live output panel should be visible before completion");

        // Send output BEFORE completing — this is the critical ordering constraint.
        // Lines sent after completion are silently dropped (run removed from active store).
        await fakeAgent.ReportOutputAsync(jobId, "saved-1", "saved-2");

        // Complete the run via the production two-channel path (HTTP + hub).
        await fakeAgent.CompleteLikeProductionAsync(jobId, new CodingAgent.Pipeline.Models.JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/1",
            RetryCount = 0,
            FilesChangedCount = 1,
            LinesAdded = 10,
            LinesRemoved = 2,
            BrainUpdatesPushed = false,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>()
        });

        // Wait for the page to receive OnRunCompleted (sets _isLive = false).
        // Poll HasLiveOutputPanelAsync because the page does a StateHasChanged cycle after receipt.
        await WaitUntilAsync(
            async () => !await detail.HasLiveOutputPanelAsync(),
            timeout: TimeSpan.FromSeconds(20));

        // The finished-run tail card must be visible now.
        await WaitUntilAsync(
            async () => await detail.HasOutputTailCardAsync(),
            timeout: TimeSpan.FromSeconds(15));

        // The saved lines must appear in the tail card.
        var tailText = await detail.GetOutputTailTextAsync();
        Assert.NotNull(tailText);
        Assert.Contains("saved-1", tailText);
        Assert.Contains("saved-2", tailText);

        // The Cancel button must no longer be visible (run is terminal).
        Assert.False(await detail.IsCancelButtonVisibleAsync(),
            "Cancel button should not be visible after the run completes");
    }
}
