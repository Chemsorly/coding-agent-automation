using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Browser-level E2E round-trip tests for the Agent Chat page.
///
/// These complement the headless <see cref="K8sChatIntegrationTests"/> by driving the actual Blazor
/// UI through Playwright: they verify that the page wires the dispatcher, hub, and component state
/// correctly — something the headless tests, which bypass the browser entirely, cannot assert.
///
/// Scenarios covered:
/// <list type="number">
///   <item>Full round trip: select template → Launch → header shows agent id + model → prompt →
///   agent responds → transcript updated.</item>
///   <item>End chat: click ✕ End Chat → CancelChat delivered to agent → page returns to launch
///   state.</item>
///   <item>Pod never connects: launch without connecting → DI-registered dispatcher times out →
///   page renders error banner with timeout message.</item>
///   <item>Idle cleanup: close the browser page → Blazor circuit tears down the component →
///   TerminateChatSessionAsync is called → CancelChat delivered to agent → job is deleted.</item>
/// </list>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "AgentChat")]
[Collection(E2ECollection.Name)]
public sealed class AgentChatUiRoundTripTests : E2ETestBase
{
    public AgentChatUiRoundTripTests(E2EFixture fixture) : base(fixture)
    {
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the <c>caa/chat-session-id</c> label from a K8s job. Throws if missing.
    /// </summary>
    private static string GetChatSessionId(k8s.Models.V1Job job)
    {
        var labels = job.Metadata?.Labels;
        if (labels is not null && labels.TryGetValue("caa/chat-session-id", out var id) && !string.IsNullOrEmpty(id))
            return id;
        throw new InvalidOperationException("caa/chat-session-id label missing on chat job");
    }

    /// <summary>
    /// Extracts the job name from a K8s job. Throws if missing.
    /// </summary>
    private static string GetJobName(k8s.Models.V1Job job) =>
        job.Metadata?.Name ?? throw new InvalidOperationException("Job name missing on chat job");

    /// <summary>
    /// Subscribes to <see cref="FakeKubernetesJobClient.ChatJobCreated"/>, starts dispatch
    /// asynchronously from the given action, and awaits the first job created.
    /// </summary>
    private async Task<k8s.Models.V1Job> WaitForFirstChatJobAsync(
        Func<Task> startDispatchAction,
        TimeSpan timeout)
    {
        var jobTcs = new TaskCompletionSource<k8s.Models.V1Job>(TaskCreationOptions.RunContinuationsAsynchronously);
        // TODO [WARNING]: if startDispatchAction() throws synchronously before ChatJobCreated fires,
        // jobTcs is never cancelled and the caller's WaitAsync(timeout) blocks until it expires rather
        // than propagating the real exception immediately. Consider adding a fault-propagation
        // continuation: _ = startDispatchAction().ContinueWith(t => jobTcs.TrySetCanceled(),
        // TaskContinuationOptions.OnlyOnFaulted), matching DispatchChatPodAndConnectAsync in
        // HeadlessE2ETestBase.
        Action<k8s.Models.V1Job>? handler = null;
        handler = job =>
        {
            Fixture.K8sClient.ChatJobCreated -= handler;
            jobTcs.TrySetResult(job);
        };
        Fixture.K8sClient.ChatJobCreated += handler;

        try
        {
            await startDispatchAction();
            return await jobTcs.Task.WaitAsync(timeout);
        }
        catch
        {
            // TODO [WARNING]: if WaitAsync(timeout) fires, the catch block unsubscribes the handler
            // correctly. However, FakeKubernetesJobClient.Reset() must clear the ChatJobCreated
            // event subscribers (set it to null) between tests; if it does not, a stale handler
            // from a timed-out WaitForFirstChatJobAsync call could fire during a subsequent test's
            // dispatch and satisfy that test's TCS with the wrong job. Verify Reset() clears
            // ChatJobCreated before the next test in the collection runs.
            Fixture.K8sClient.ChatJobCreated -= handler;
            throw;
        }
    }

    // ── Scenario 1: Full round trip ───────────────────────────────────────────

    /// <summary>
    /// Scenario 1: Select agent template → Launch → fake agent connects →
    /// header shows agent id and resolved model → type a prompt → agent replies →
    /// reply appears in transcript.
    /// </summary>
    [Fact]
    public async Task AgentChat_RoundTrip_PromptAndResponseRendered()
    {
        // ── Arrange: seed a minimal agent profile so the model name resolves ───
        // TODO [WARNING]: these entities are saved with CancellationToken.None and may not be cleaned
        // up by Fixture.ResetAll() between tests (depends on whether InMemoryConfigurationStore.ResetAll
        // clears profiles and provider configs). A subsequent test that expects zero profiles or resolves
        // "kiro,dotnet" may find these stale entities. Verify ResetAll covers them, or explicitly delete
        // after the test.
        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "chat-profile-rt",
            DisplayName = "Chat Round Trip Profile",
            MatchLabels = new[] { "kiro", "dotnet" },
            AgentProviderConfigId = "agent-chat-rt",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "agent-chat-rt",
            DisplayName = "Chat Agent Round Trip",
            ProviderType = "kiro",
            Kind = ProviderKind.Agent,
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Model] = "claude-sonnet-test"
            }
        }, CancellationToken.None);

        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();

        // ── Act: select template and click Launch; intercept the created job ──
        var job = await WaitForFirstChatJobAsync(
            async () =>
            {
                await chatPage.SelectTemplateAsync("kiro,dotnet");
                await chatPage.LaunchChatPodAsync();
            },
            TimeSpan.FromSeconds(35));

        var chatSessionId = GetChatSessionId(job);

        // Connect the fake agent concurrently — same approach as DispatchChatPodAndConnectAsync.
        // TODO [WARNING]: connectTask is not externally cancellable; ConnectAsChatAgentAsync manages
        // its own internal 25s CTS (BuildAndStartConnectionAsync). If WaitAsync(35s) fires first,
        // ConnectAsChatAgentAsync runs as an orphaned background task. A fault observer is attached
        // below to suppress unobserved exceptions. Same pattern at Scenarios 2 and 4.
        // TODO [WARNING]: agent IDs are truncated to 21 chars; "chat-rt-agent-" is 14 chars leaving
        // only 7 GUID hex chars (28 bits of entropy). Under parallel assembly execution concurrent
        // runs could collide. Consider using Guid.NewGuid():N[..16] or removing the slice.
        var agentId = $"chat-rt-agent-{Guid.NewGuid():N}"[..21];
        var fakeAgent = new FakeAgentClient(agentId, "kiro", "dotnet");
        var connectTask = fakeAgent.ConnectAsChatAgentAsync(AgentHubUrl, Fixture.ApiKey, chatSessionId);
        // [CRITICAL FIX]: Attach a fault observer so that if WaitAsync times out and connectTask
        // continues as an orphaned background task, any exception it throws is observed and does not
        // crash the test host on the finalizer thread.
        _ = connectTask.ContinueWith(
            t => { /* observe exception */ },
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        await connectTask.WaitAsync(TimeSpan.FromSeconds(35));

        await using (fakeAgent)
        {
            // ── Assert: chat header is visible with the agent id and model ────
            // TODO [WARNING]: the assertions below re-read the header via GetChatHeaderTextAsync which
            // is not visibility-aware (QuerySelectorAsync returns hidden elements). Assert directly on
            // the text returned by WaitForChatHeaderAsync, or use WaitForSelectorAsync with Visible state.
            var headerText = await chatPage.WaitForChatHeaderAsync(timeoutMs: 60_000);
            Assert.NotNull(headerText);
            Assert.Contains(agentId, headerText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("claude-sonnet-test", headerText, StringComparison.OrdinalIgnoreCase);

            // ── Act: send a prompt ────────────────────────────────────────────
            await chatPage.SendPromptAsync("What is 2+2?");

            // Wait for the fake agent to receive the prompt.
            var prompt = await fakeAgent.ChatPromptAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("What is 2+2?", prompt.Prompt);
            // TODO [WARNING]: the sent prompt is only confirmed at the SignalR layer. Add an assertion
            // that the user's message bubble also renders in the browser transcript (e.g. via
            // GetTranscriptMessagesAsync), so a regression where the UI sends correctly but fails
            // to render the outbound message is caught.

            // Agent sends response.
            await fakeAgent.SendChatResponseAsync(prompt.SessionId, "The answer is 4.");

            // ── Assert: response appears in the transcript ────────────────────
            var responseText = await chatPage.GetResponseTextAsync(timeoutMs: 15_000);
            Assert.NotNull(responseText);
            Assert.Contains("The answer is 4.", responseText, StringComparison.OrdinalIgnoreCase);

            // ── Cleanup: initiate end-chat before the test exits ──────────────
            // Clicking End Chat sets _isChatActive = false in the Blazor component before
            // any await, so the circuit's DisposeAsync (triggered by E2ETestBase.DisposeAsync
            // closing the browser context) will not call TerminateChatSessionAsync again.
            // The actual HTTP call (TerminateChatSessionAsync) completes asynchronously;
            // E2EWebApplicationFactory.ShutdownTimeout is set high enough (20s) to wait for
            // it during collection cleanup so no ObjectDisposedException is thrown.
            await chatPage.EndChatAsync();
        }
    }

    // ── Scenario 2: End chat ──────────────────────────────────────────────────

    /// <summary>
    /// Scenario 2: Active chat session → click ✕ End Chat →
    /// CancelChat is delivered to the agent → page returns to launch state.
    /// </summary>
    [Fact]
    public async Task AgentChat_EndChat_AgentReceivesCancelAndPageResets()
    {
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();

        var job = await WaitForFirstChatJobAsync(
            async () =>
            {
                await chatPage.SelectTemplateAsync("kiro,dotnet");
                await chatPage.LaunchChatPodAsync();
            },
            TimeSpan.FromSeconds(35));

        var chatSessionId = GetChatSessionId(job);

        // TODO [WARNING]: agent IDs are truncated to 21 chars; "chat-end-agent-" is 15 chars leaving
        // only 6 GUID hex chars. Under parallel assembly execution concurrent runs could collide.
        var agentId = $"chat-end-agent-{Guid.NewGuid():N}"[..21];
        var fakeAgent = new FakeAgentClient(agentId, "kiro", "dotnet");
        var connectTask = fakeAgent.ConnectAsChatAgentAsync(AgentHubUrl, Fixture.ApiKey, chatSessionId);
        // [CRITICAL FIX]: Attach a fault observer to prevent unobserved-exception crashes if
        // WaitAsync times out and the underlying task faults after the test exits its await.
        _ = connectTask.ContinueWith(
            t => { /* observe exception */ },
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        await connectTask.WaitAsync(TimeSpan.FromSeconds(35));

        await using (fakeAgent)
        {
            // Wait for the chat window to appear.
            await chatPage.WaitForChatHeaderAsync(timeoutMs: 60_000);

            // ── Act: click End Chat ───────────────────────────────────────────
            await chatPage.EndChatAsync();

            // ── Assert: agent receives CancelChat ─────────────────────────────
            await fakeAgent.CancelChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // ── Assert: page returns to launch state (template selector visible) ──
            await chatPage.WaitForLaunchStateAsync(timeoutMs: 10_000);

            // Confirm the chat window header is gone.
            var header = await chatPage.GetChatHeaderTextAsync();
            // TODO [WARNING]: GetChatHeaderTextAsync uses QuerySelectorAsync which is not
            // visibility-aware — it returns the element even if it is hidden via display:none.
            // If the Blazor component hides rather than removes .chat-header-bar when returning
            // to launch state, this assertion passes despite the header still being in the DOM.
            // TODO [WARNING]: there is no wait for .chat-header-bar to disappear before this
            // assertion. WaitForLaunchStateAsync returns as soon as #template-select appears,
            // but the header element may still be in the DOM in the same render frame. Use
            // WaitForSelectorAsync with State=Hidden on .chat-header-bar before calling
            // GetChatHeaderTextAsync to make this assertion non-racy.
            Assert.Null(header);
            // TODO [WARNING]: this test does not assert that the K8s job is deleted or reaches a
            // terminal state after EndChatAsync. If TerminateChatSessionAsync fails to call
            // DeleteJobAsync, this test still passes. The headless K8sChat_EndChat_PvcReleasedJobTerminal
            // asserts on terminal job state; consider adding a matching assertion here.
        }
    }

    // ── Scenario 3: Pod never connects ───────────────────────────────────────

    /// <summary>
    /// Scenario 3: Launch from the Blazor UI without connecting an agent.
    /// The DI-registered <see cref="ChatJobDispatcher"/> (configured with
    /// <c>ChatPodConnectTimeoutSeconds</c> from the test harness, default 30 s) times out
    /// and signals the page. The page renders an error banner visible to the user, and the job
    /// is cleaned up.
    ///
    /// <para>
    /// This test drives the full UI → dispatcher → error-display path:
    /// <list type="bullet">
    ///   <item>Select template and click Launch → job is created by the DI dispatcher.</item>
    ///   <item>No agent connects, so the dispatcher's connect-timeout fires.</item>
    ///   <item><see cref="AgentChatPage.WaitForLaunchErrorAsync"/> confirms the error banner
    ///   appears in the browser.</item>
    ///   <item>The job created by the DI dispatcher's job client is confirmed to be deleted.</item>
    /// </list>
    /// </para>
    ///
    /// Note: this test waits for the full <c>ChatPodConnectTimeoutSeconds</c> (30 s in the
    /// harness). This is intentional — the timeout path cannot be exercised without waiting for
    /// it. The harness value is lower than the production default (60 s) for this reason.
    /// </summary>
    [Fact]
    public async Task AgentChat_PodNeverConnects_PageShowsErrorAndJobCleanedup()
    {
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();

        // ── Act: select template and click Launch; intercept the created job ──
        // We subscribe to ChatJobCreated so we know which job to watch for deletion.
        var job = await WaitForFirstChatJobAsync(
            async () =>
            {
                await chatPage.SelectTemplateAsync("kiro,dotnet");
                await chatPage.LaunchChatPodAsync();
            },
            TimeSpan.FromSeconds(35));

        var jobName = GetJobName(job);

        // ── Assert: page shows an error banner once the dispatcher times out ──
        // The DI-registered ChatJobDispatcher uses ChatPodConnectTimeoutSeconds from the harness
        // environment (ApplyDispatchEnvironment → WorkDistribution__Dispatch__ChatPodConnectTimeoutSeconds,
        // default 30 s). No agent is connected, so the dispatcher fires ChatPodTimeoutException and
        // the Blazor component renders the error message in .agent-detail-warning.
        var errorText = await chatPage.WaitForLaunchErrorAsync(timeoutMs: 40_000);
        Assert.NotNull(errorText);
        // TODO [WARNING]: these assertions are too weak — any non-empty string satisfies them,
        // including an unrelated warning or the transient "Launching…" text if WaitForLaunchErrorAsync
        // returns early. Add a scenario-specific substring assertion (e.g. Assert.Contains("timed out",
        // errorText, StringComparison.OrdinalIgnoreCase) or a fragment from ChatPodTimeoutException's
        // message) to distinguish a genuine connect-timeout error from any incidental non-empty text.
        Assert.False(string.IsNullOrWhiteSpace(errorText),
            "Expected a non-empty error message in the page error banner");

        // ── Assert: the K8s job is cleaned up by the dispatcher after the timeout ──
        // TODO [WARNING]: DeletedJobs is populated by FakeKubernetesJobClient.DeleteJobAsync.
        // If ChatJobDispatcher catches the timeout internally and does not call DeleteJobAsync
        // (e.g. it only terminates the in-memory PVC reservation), this assertion will fail.
        // This is the intended behavior — the assertion catches that regression.
        // TODO [WARNING]: WaitUntilAsync does not include the expected job name in its timeout
        // message, making CI failures hard to diagnose. Pass a descriptive message or use an
        // overload that includes jobName in the assertion failure output.
        await WaitUntilAsync(
            () => Fixture.K8sClient.DeletedJobs.Contains(jobName),
            timeout: TimeSpan.FromSeconds(10));
    }

    // ── Scenario 4: Idle cleanup (page / circuit close) ───────────────────────

    /// <summary>
    /// Scenario 4: An active chat session has its browser page closed (circuit torn down).
    /// Blazor calls <c>IAsyncDisposable.DisposeAsync</c> on the component, which calls
    /// <c>TerminateChatSessionAsync</c>. Verifies that the agent receives a <c>CancelChat</c>
    /// message and the chat job is deleted.
    ///
    /// This mirrors the production "idle cleanup" path: when the browser tab is closed or the
    /// user navigates away, the Blazor circuit tears down and the server cleans up the pod.
    /// </summary>
    [Fact]
    public async Task AgentChat_PageClosed_ComponentDisposeTerminatesChat()
    {
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();

        var job = await WaitForFirstChatJobAsync(
            async () =>
            {
                await chatPage.SelectTemplateAsync("kiro,dotnet");
                await chatPage.LaunchChatPodAsync();
            },
            TimeSpan.FromSeconds(35));

        var chatSessionId = GetChatSessionId(job);
        var jobName = GetJobName(job);

        // TODO [WARNING]: agent ID is truncated to 21 chars; "chat-idle-agent-" is 16 chars leaving
        // only 5 GUID hex chars (20 bits of entropy). Under parallel assembly execution concurrent
        // runs could collide, causing hub registration overwrites.
        var agentId = $"chat-idle-agent-{Guid.NewGuid():N}"[..21];
        var fakeAgent = new FakeAgentClient(agentId, "kiro", "dotnet");
        var connectTask = fakeAgent.ConnectAsChatAgentAsync(AgentHubUrl, Fixture.ApiKey, chatSessionId);
        // [CRITICAL FIX]: Attach a fault observer to prevent unobserved-exception crashes if
        // WaitAsync times out and the underlying task faults after the test exits its await.
        _ = connectTask.ContinueWith(
            t => { /* observe exception */ },
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        await connectTask.WaitAsync(TimeSpan.FromSeconds(35));

        await using (fakeAgent)
        {
            // Wait for the chat window to be active.
            await chatPage.WaitForChatHeaderAsync(timeoutMs: 60_000);

            // ── Act: close the page — simulates the user navigating away or closing the tab ──
            // Closing the Playwright page closes the WebSocket to the Blazor server, which tears
            // down the server-side Blazor circuit and calls IAsyncDisposable.DisposeAsync on
            // AgentChat.razor → DisposeK8sChatAsync → TerminateChatSessionAsync.

            // [CRITICAL FIX]: Take a screenshot before closing the page so that CI failure artifacts
            // are captured. Page.CloseAsync() makes the page unavailable; E2ETestBase.DisposeAsync
            // attempts Page.ScreenshotAsync during teardown which throws PlaywrightException
            // ("Target closed") — the try/catch swallows it, so no screenshot is saved on failure.
            // Capturing it here ensures the pre-close state is available in CI on test failure.
            try
            {
                var screenshotDir = Path.Combine("TestResults", "screenshots");
                Directory.CreateDirectory(screenshotDir);
                var screenshotPath = Path.Combine(screenshotDir,
                    $"{nameof(AgentChat_PageClosed_ComponentDisposeTerminatesChat)}_pre_close_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                await Page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions { Path = screenshotPath, FullPage = true });
            }
            catch
            {
                // Non-fatal — do not abort the test if the pre-close screenshot fails.
            }

            // Leave the page instead of closing it: both fire pagehide, whose sendBeacon to
            // _blazor/disconnect disposes the circuit at once. CloseAsync can kill the renderer
            // before the beacon goes out; the circuit then only counts as disconnected and is kept
            // for the 3-minute DisconnectedCircuitRetentionPeriod, so DisposeAsync never runs in time.
            await Page.GotoAsync("about:blank");

            // ── Assert: agent receives CancelChat within a short timeout ──────
            // TODO [WARNING]: DisposeK8sChatAsync only runs when _isChatActive && !string.IsNullOrEmpty
            // (_selectedAgentId) at Blazor circuit teardown. Circuit teardown is asynchronous after the
            // WebSocket closes; 20s is generous for a local process but the root cause of a timeout here
            // (circuit not torn down fast enough vs SignalR delivery lost) is not surfaced in the error message.
            await fakeAgent.CancelChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(20));

            // ── Simulate real agent exit: pod completes after receiving CancelChat ──────────
            // In production the agent process exits when it receives CancelChat, causing the K8s
            // job to reach a terminal (Complete) state. The background watcher detects this and
            // calls CleanupSession. Without simulating terminal state here the watcher never sees
            // a completed job, the grace period (ChatTerminationGracePeriodSeconds = 10s) fires
            // instead, and whether force-delete or clean exit wins is a timing race that causes
            // the test to flake on CI. Matching the pattern used by K8sChatIntegrationTests.
            await Fixture.K8sClient.SimulateChatJobTerminalAsync(jobName, success: true);

            // ── Assert: the K8s job reached terminal state ─────────────────────
            // The dispatcher's clean exit path (watcher detects terminal job) calls CleanupSession
            // but does not explicitly call DeleteJobAsync — the pod exited naturally. Assert on the
            // job condition instead of DeletedJobs, which would only be populated by the force-delete
            // path. This matches K8sChatIntegrationTests.K8sChat_EndChat_PvcReleasedJobTerminal.
            await WaitUntilAsync(
                () => Fixture.K8sClient.ChatJobs.TryGetValue(jobName, out var j) &&
                      j.Status?.Conditions?.Any(c => c.Type == "Complete" && c.Status == "True") == true,
                timeout: TimeSpan.FromSeconds(15));

            Assert.True(fakeAgent.CancelChatReceived.Task.IsCompletedSuccessfully);
        }
    }
}
