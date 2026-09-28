using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using k8s.Models;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E Playwright tests for the Agent Chat page, covering the full browser → API → agent → browser
/// loop. Re-creates the four scenarios deleted in the Kubernetes refactor (#2084).
///
/// Architecture:
/// The browser drives the Blazor page, which calls its injected <c>IChatJobDispatcher</c> (a
/// proxy that POSTs to the API host). The real <c>ChatJobDispatcher</c> on the API host dispatches
/// the pod and polls for agent registration. Tests subscribe to <see cref="FakeKubernetesJobClient.ChatJobCreated"/>
/// to intercept the created job, extract the chat session ID, and connect a <see cref="FakeAgentClient"/>
/// as the fake pod — mirroring <c>DispatchChatPodAndConnectAsync</c> in <see cref="HeadlessE2ETestBase"/>
/// but with the browser click replacing the direct dispatcher call.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "AgentChat")]
[Collection(E2ECollection.Name)]
public sealed class AgentChatE2ETests : E2ETestBase
{
    public AgentChatE2ETests(E2EFixture fixture) : base(fixture) { }

    // ── Scenario 1: Full round trip ───────────────────────────────────────

    /// <summary>
    /// Browser-driven round trip: template selected → pod launched → fake agent connects →
    /// prompt sent → agent receives AssignChatPrompt → agent replies → response rendered in page.
    ///
    /// Covers: AgentChat.LaunchChatPod → StartChat (hub subscription) → SendPrompt →
    /// SubscribeAndSendPromptAsync → HandleChatResponseReceived → HandleChatCompleted.
    /// </summary>
    [Fact]
    public async Task AgentChat_RoundTrip_PromptAndResponse()
    {
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();
        await chatPage.SelectTemplateAsync("kiro,dotnet");

        // Subscribe to ChatJobCreated BEFORE clicking Launch so the event fires into our TCS.
        var (job, fakeAgent) = await LaunchAndConnectAsync(chatPage);
        await using (fakeAgent)
        {
            // Chat window should be visible
            await chatPage.WaitForChatWindowAsync();

            // Header contains the agentId and "model: " prefix
            var headerText = await Page.Locator(".chat-agent-info").InnerTextAsync();
            Assert.Contains(fakeAgent.AgentId, headerText);
            // TODO [WARNING]: Assert.Contains("model: ", headerText) only verifies the prefix
            // appears — it passes even if the model name portion is blank (e.g. "model: ").
            // If model resolution is important to assert, also verify the text after "model: "
            // is non-empty or equals an expected value. FakeAgentClient registers via
            // ConnectAsChatAgentAsync (not StartAssignedWorkItemAsync) so the model shown may
            // be a default placeholder rather than a resolved name. (TestQualityReviewer:56)
            Assert.Contains("model: ", headerText);

            // Send a prompt
            await chatPage.SendPromptAsync("What is 2+2?");

            // CRITICAL: await ChatPromptAssigned BEFORE calling SendChatResponseAsync.
            // AssignChatPromptAsync on the hub runs after SubscribeToChatSession in
            // SubscribeAndSendPromptAsync. By the time ChatPromptAssigned fires, the browser's
            // hub connection is subscribed to the session group — safe to respond.
            // TODO [WARNING]: ChatPromptAssigned is a single-use TCS. If the AssignChatPrompt
            // hub message fires before this await is reached (i.e. the TCS resolves and is
            // discarded), the WaitAsync still succeeds because the completed Task is returned.
            // The 15s timeout is the only guard against hub delivery delay under CI load.
            // (Correctness:52)
            var prompt = await fakeAgent.ChatPromptAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("What is 2+2?", prompt.Prompt);

            // Agent sends response using the session ID from the received prompt message
            await fakeAgent.SendChatResponseAsync(prompt.SessionId, "The answer is 4.");

            // TODO [WARNING]: GetResponseTextAsync uses a two-step approach: first waits for
            // .chat-message-agent .chat-message-content pre, then waits for .chat-streaming to
            // disappear. There is a TOCTOU gap — if the streaming element appears and disappears
            // between the two separate Playwright checks, both could pass against different DOM
            // states. In practice this is extremely unlikely given the sequencing above, but a
            // more robust approach would wait for .chat-streaming to disappear first and then
            // read the pre element. (Correctness:67)
            var responseText = await chatPage.GetResponseTextAsync(timeoutMs: 15_000);
            Assert.Equal("The answer is 4.", responseText);
        }
    }

    // ── Scenario 2: End chat ──────────────────────────────────────────────

    /// <summary>
    /// Browser-driven end chat: click End Chat → agent receives CancelChat → job goes terminal →
    /// page returns to launch UI.
    ///
    /// Covers: AgentChat.EndChat → EndChatK8sModeAsync → ChatDispatcher.TerminateChatSessionAsync.
    /// </summary>
    [Fact]
    public async Task AgentChat_EndChat_AgentReceivesCancelAndPageResets()
    {
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();
        await chatPage.SelectTemplateAsync("kiro,dotnet");

        var (job, fakeAgent) = await LaunchAndConnectAsync(chatPage);
        await using (fakeAgent)
        {
            // TODO [WARNING]: jobName is only used to call SimulateChatJobTerminalAsync (a
            // test-controlled mutation) and to read back the condition the test itself wrote.
            // There is no assertion driven by the production code path (e.g.
            // Assert.Contains(jobName, Fixture.K8sClient.DeletedJobs)) to verify the dispatcher
            // actually deleted or cleaned up the K8s job after receiving the end-chat signal.
            // Consider adding Assert.Contains(jobName, Fixture.K8sClient.DeletedJobs).
            // (TestQualityReviewer:97, Correctness:110)
            var jobName = job.Metadata!.Name!;

            // Wait for chat window to confirm _isChatActive = true
            await chatPage.WaitForChatWindowAsync();

            // Click End Chat
            await chatPage.EndChatAsync();

            // Agent must receive CancelChat
            await fakeAgent.CancelChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Simulate clean pod exit — FakeKubernetesJobClient does not auto-complete jobs
            await Fixture.K8sClient.SimulateChatJobTerminalAsync(jobName, success: true);

            // TODO [WARNING]: There is no await between SimulateChatJobTerminalAsync and the
            // UI assertion below to allow the hub event → Blazor re-render cycle to flush.
            // Under CI load WaitForSelectorAsync(".btn-start-chat") may find the button in an
            // intermediate DOM state rather than after the reset completes, because .btn-start-chat
            // is present before chat starts as well. A more robust assertion would first wait for
            // .chat-header-bar to disappear, then assert .btn-start-chat is present.
            // (DotNetSpecialist:100)

            // Page returns to launch UI
            await Page.WaitForSelectorAsync(".btn-start-chat", new() { Timeout = 10_000 });

            // Chat window is gone
            var headerCount = await Page.Locator(".chat-header-bar").CountAsync();
            Assert.Equal(0, headerCount);

            // TODO [WARNING]: The Complete condition assertion below is tautological —
            // SimulateChatJobTerminalAsync (called above by this test) writes the condition
            // directly into the in-memory job object, so this assertion verifies the test helper
            // works rather than that production code produced the terminal condition. The
            // system-observable behaviour to assert is that the dispatcher deleted the job
            // (driven by the production DeleteJobAsync path):
            //   Assert.Contains(jobName, Fixture.K8sClient.DeletedJobs)
            // (TestQualityReviewer:118, Correctness:110)

            // Server-side: job reached Complete condition
            var terminalJob = Fixture.K8sClient.ChatJobs[jobName];
            Assert.Contains(terminalJob.Status.Conditions,
                c => c.Type == "Complete" && c.Status == "True");
        }
    }

    // ── Scenario 3: Pod never connects ───────────────────────────────────

    /// <summary>
    /// Browser-driven timeout: launch pod, never connect an agent, wait for timeout error in page.
    ///
    /// NOTE: This test waits ~30 seconds — the <c>ChatPodConnectTimeoutSeconds</c> in the harness
    /// is 30s (set by <c>E2ETestDefaults.ApplyDispatchEnvironment</c>) and is read from
    /// configuration at host startup. It cannot be overridden per-dispatch at runtime without
    /// rebuilding the API host. The 40s Playwright timeout accommodates the 30s dispatcher
    /// timeout plus page re-render latency.
    ///
    /// Covers: AgentChat.LaunchChatPod → ClassifyLaunchError → ChatPodTimeoutException → error
    /// rendered in .agent-detail-warning.
    /// </summary>
    [Fact]
    public async Task AgentChat_PodNeverConnects_ShowsErrorInPage()
    {
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();
        await chatPage.SelectTemplateAsync("kiro,dotnet");

        // TODO [WARNING]: This inline job-subscription pattern (jobTcs, handler, event
        // subscribe/unsubscribe in try/finally) duplicates LaunchAndConnectAsync verbatim.
        // The duplication means fixes must be applied in two places. Unlike LaunchAndConnectAsync,
        // this inline version also lacks fault-propagation: if the dispatch faults before
        // creating a job, it waits the full 35s and then throws TimeoutException instead of
        // surfacing the real dispatch fault. Consider extracting a shared helper that accepts
        // a "connect agent" parameter (true/false). (TestQualityReviewer:151)

        // Subscribe to ChatJobCreated to get the job name for cleanup verification
        var jobTcs = new TaskCompletionSource<V1Job>(TaskCreationOptions.RunContinuationsAsynchronously);
        // TODO [WARNING]: handler is captured inside the lambda before the outer variable is
        // fully visible to the closure. Use a stable local copy (var h = handler) before
        // subscribing to eliminate the capture hazard. The current pattern is safe because the
        // lambda executes asynchronously (after handler is assigned), but is fragile.
        // (DotNetSpecialist:160, DotNetSpecialist:213)
        Action<V1Job>? handler = null;
        handler = createdJob =>
        {
            Fixture.K8sClient.ChatJobCreated -= handler;
            jobTcs.TrySetResult(createdJob);
        };
        Fixture.K8sClient.ChatJobCreated += handler;

        try
        {
            // Click Launch — intentionally never connecting a fake agent
            await chatPage.LaunchChatPodAsync();

            // Wait for the job to be created so we have its name
            var job = await jobTcs.Task.WaitAsync(TimeSpan.FromSeconds(35));
            var jobName = job.Metadata!.Name!;

            // Wait for the error element — this takes ~30s for the dispatcher timeout to fire
            await chatPage.WaitForLaunchErrorAsync(timeoutMs: 40_000);

            // TODO [WARNING]: "did not connect" is a fragile substring match on a user-facing
            // error string. If the production error message changes wording, WaitForLaunchErrorAsync
            // still passes (element appeared) but this Contains check fails with an opaque message
            // that doesn't identify which code path produced the text. Consider asserting against
            // a stable constant or CSS class rather than a prose substring. (TestQualityReviewer:172)

            // Error text confirms it is the timeout message from ClassifyLaunchError
            var errorText = await Page.Locator(".agent-detail-warning").InnerTextAsync();
            Assert.Contains("did not connect", errorText);

            // TODO [WARNING]: The DeletedJobs assertion below may race under CI load.
            // WaitForLaunchErrorAsync resolves when the DOM element appears, which happens after
            // ClassifyLaunchError throws — but job cleanup (TerminateChatSessionAsync →
            // DeleteJobAsync) may complete asynchronously slightly after the error is rendered.
            // A deterministic guard would poll: WaitUntilAsync(() =>
            // Fixture.K8sClient.DeletedJobs.Contains(jobName)) before asserting.
            // In practice the task has already completed by the time the error renders, but the
            // ordering is only guaranteed by implementation detail. (TestQualityReviewer:176,
            // Correctness:140)

            // Dispatcher cleaned up the K8s job after timeout
            Assert.Contains(jobName, Fixture.K8sClient.DeletedJobs);
        }
        finally
        {
            Fixture.K8sClient.ChatJobCreated -= handler;
        }
    }

    // ── Scenario 4: Idle cleanup (page close → DisposeAsync) ─────────────

    /// <summary>
    /// Browser-driven idle cleanup: close the browser context while the chat is active
    /// (without clicking End Chat). The Blazor circuit tears down and <c>DisposeAsync</c> fires,
    /// which calls <c>DisposeK8sChatAsync → TerminateChatSessionAsync</c> → agent receives
    /// CancelChat.
    ///
    /// IMPORTANT: The test must NOT click End Chat before closing the browser.
    /// <c>EndChat()</c> sets <c>_isChatActive = false</c> as its first action to prevent
    /// concurrent double-terminate. If <c>_isChatActive = false</c> when <c>DisposeAsync</c>
    /// fires, the <c>DisposeK8sChatAsync</c> guard fails and <c>TerminateChatSessionAsync</c>
    /// is never called. This test closes the page directly from the chat window state.
    ///
    /// Accessing the browser context: <c>E2ETestBase._context</c> is private. Use
    /// <c>Page.Context.CloseAsync()</c> — <c>Page</c> is protected and <c>Page.Context</c>
    /// is the Playwright <c>IBrowserContext</c> that owns the page.
    ///
    /// Circuit teardown: <c>DisconnectedCircuitRetentionPeriod = TimeSpan.Zero</c> is set in
    /// <c>E2EWebApplicationFactory</c>, making disposal fire immediately on browser disconnect.
    ///
    /// Covers: AgentChat.DisposeAsync → DisposeK8sChatAsync → TerminateChatSessionAsync.
    /// </summary>
    [Fact]
    public async Task AgentChat_PageClose_TerminatesJobViaDispose()
    {
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();
        await chatPage.SelectTemplateAsync("kiro,dotnet");

        var (job, fakeAgent) = await LaunchAndConnectAsync(chatPage);
        await using (fakeAgent)
        {
            var jobName = job.Metadata!.Name!;

            // Confirm chat window is visible — _isChatActive = true
            await chatPage.WaitForChatWindowAsync();

            // Close the browser context WITHOUT clicking End Chat.
            // Page.Context gives the IBrowserContext owning this page.
            // After CloseAsync(), the Page object is invalidated — all remaining assertions
            // must be against server-side state only.
            // TODO [WARNING]: E2ETestBase.DisposeAsync also calls _context.DisposeAsync() during
            // test teardown, which disposes the same browser context already closed here.
            // Playwright's IBrowserContext.DisposeAsync after CloseAsync is idempotent per spec,
            // so this is safe. However the screenshot attempt in E2ETestBase.DisposeAsync will
            // throw after the context is closed — it is swallowed by the catch {} guard there.
            // If a future Playwright version removes the idempotency guarantee, teardown could
            // fail. (Correctness:193)
            await Page.Context.CloseAsync();

            // DisposeAsync fires on circuit teardown → DisposeK8sChatAsync → TerminateChatSessionAsync
            // → hub sends CancelChat to the agent. Allow 15s for circuit teardown + hub delivery.
            await fakeAgent.CancelChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // TerminateChatSessionAsync also cleans up the K8s job. However the deletion is
            // asynchronous with respect to CancelChat: TerminateChatSessionAsync sends CancelChat
            // first, then waits up to ChatTerminationGracePeriodSeconds (10s in the harness) for the
            // watcher task to complete, then calls DeleteJobAsync. Poll until DeletedJobs contains the
            // job name rather than asserting immediately after CancelChatReceived.
            await WaitUntilAsync(
                () => Fixture.K8sClient.DeletedJobs.Contains(jobName),
                timeout: TimeSpan.FromSeconds(20));
            Assert.Contains(jobName, Fixture.K8sClient.DeletedJobs);
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Subscribes to <see cref="FakeKubernetesJobClient.ChatJobCreated"/>, triggers the browser
    /// launch click, waits for the K8s job to be created, extracts the chat session ID from the
    /// job labels, and connects a <see cref="FakeAgentClient"/> as the fake pod.
    ///
    /// This mirrors the <c>DispatchChatPodAndConnectAsync</c> pattern from
    /// <see cref="HeadlessE2ETestBase"/> adapted for browser-driven dispatch: the browser click
    /// replaces the direct <c>Fixture.ChatDispatcher.DispatchChatPodAsync()</c> call. The
    /// dispatch task runs asynchronously on the API host; the test detects the chat window via
    /// DOM, not by awaiting the dispatch task.
    ///
    /// The <see cref="FakeAgentClient"/> connection is fired without immediately awaiting it
    /// (same rationale as the headless helper): the dispatcher's poll starts at click time, and
    /// awaiting connect serially before the first 500ms poll tick risks missing it under CI load.
    ///
    /// Caller is responsible for disposing the returned <see cref="FakeAgentClient"/>.
    /// </summary>
    private async Task<(V1Job job, FakeAgentClient fakeAgent)> LaunchAndConnectAsync(AgentChatPage chatPage)
    {
        var jobTcs = new TaskCompletionSource<V1Job>(TaskCreationOptions.RunContinuationsAsynchronously);
        // TODO [WARNING]: handler is captured inside the lambda before the outer variable is
        // fully visible to the closure (handler = null at declaration, assigned on next line).
        // The pattern is safe because the lambda executes asynchronously (well after handler is
        // assigned), but is fragile. Use a stable local copy (var h = handler; subscribe h)
        // to eliminate the nullable capture hazard. The double-unsubscribe in finally after a
        // successful return is harmless (multicast -= no-op) but could silently break under
        // parallel test execution if E2ECollection serialisation is ever removed.
        // (DotNetSpecialist:165, DotNetSpecialist:213, Correctness:218)
        Action<V1Job>? handler = null;
        handler = createdJob =>
        {
            // Unsubscribe immediately so concurrent dispatches each own exactly one job
            Fixture.K8sClient.ChatJobCreated -= handler;
            jobTcs.TrySetResult(createdJob);
        };
        Fixture.K8sClient.ChatJobCreated += handler;

        try
        {
            // Browser click triggers Blazor → API POST → ChatJobDispatcher.DispatchChatPodAsync
            await chatPage.LaunchChatPodAsync();

            // Wait for the K8s job to be created (fires synchronously inside CreateJobAsync)
            V1Job job;
            try
            {
                job = await jobTcs.Task.WaitAsync(TimeSpan.FromSeconds(35));
            }
            catch
            {
                Fixture.K8sClient.ChatJobCreated -= handler;
                throw;
            }

            // Extract the session ID the dispatcher wrote into the job labels
            var labels = job.Metadata?.Labels
                ?? throw new InvalidOperationException("Chat job has no metadata labels");
            if (!labels.TryGetValue("caa/chat-session-id", out var sessionId) || sessionId is null)
                throw new InvalidOperationException("Chat job is missing caa/chat-session-id label");

            // Build and connect the fake agent. Fire without immediately awaiting so the
            // SignalR handshake races in parallel with the dispatcher's first 500ms poll tick.
            var agentId = $"fake-chat-e2e-{Guid.NewGuid().ToString("N")[..6]}";
            var fakeAgent = new FakeAgentClient(agentId, "kiro", "dotnet");
            var connectTask = fakeAgent.ConnectAsChatAgentAsync(AgentHubUrl, Fixture.ApiKey, sessionId);

            // Await connection first — a timeout here is clearer than ChatPodTimeoutException
            await connectTask.WaitAsync(TimeSpan.FromSeconds(35));

            return (job, fakeAgent);
        }
        finally
        {
            // Ensure no dangling subscription on any failure path
            // TODO [WARNING]: On the happy path handler was already unsubscribed inside the
            // lambda, so this -= is a no-op double-unsubscribe. It is harmless today because
            // E2ECollection serialises tests and lambda instances are unique. Safe to keep for
            // the "dangling on failure" guarantee, but document that this is intentionally
            // idempotent. (DotNetSpecialist:165, Correctness:218)
            Fixture.K8sClient.ChatJobCreated -= handler;
        }
    }
}
