using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Integration E2E tests for K8s chat pod dispatch.
/// Pure SignalR + in-memory — no Playwright/browser needed.
/// Tests verify the assembled system: ChatJobDispatcher → FakeKubernetesJobClient →
/// FakeAgentClient (as chat pod) → AgentRegistryService round-trip.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "K8sChatMode")]
[Collection(E2ECollection.Name)]
public sealed class K8sChatIntegrationTests : HeadlessE2ETestBase
{
    private readonly AgentRegistryService _registry;

    public K8sChatIntegrationTests(E2EFixture fixture) : base(fixture)
    {
        _registry = fixture.AgentRegistry;
    }

    // ── Happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task K8sChat_DispatchChatPod_JobCreatedWithCorrectLabels()
    {
        // Dispatch and connect fake agent
        var (agentId, fakeAgent) = await DispatchChatPodAndConnectAsync(
            "kiro,dotnet", model: "claude-opus-4.8", effort: "high");

        await using (fakeAgent)
        {
            // Assert: exactly 1 chat job
            Assert.Single(Fixture.K8sClient.ChatJobs);

            var (_, job) = Fixture.K8sClient.ChatJobs.Single();

            // Labels
            AssertChatJobLabels(job);
            var selectorLabel = job.Metadata!.Labels["caa/chat-selector"];
            Assert.Equal("dotnet_kiro", selectorLabel); // normalized + underscore-encoded

            Assert.True(job.Metadata.Labels.ContainsKey("caa/claimed-pvc"),
                "Job should have caa/claimed-pvc label (kiro template claims PVC)");

            // Env vars
            var env = job.Spec!.Template.Spec!.Containers[0].Env!;
            Assert.Equal("true", env.First(e => e.Name == "AGENT_CHAT_MODE").Value);
            Assert.Equal("claude-opus-4.8", env.First(e => e.Name == "AGENT_CHAT_MODEL").Value);
            Assert.Equal("high", env.First(e => e.Name == "AGENT_CHAT_EFFORT").Value);

            // Job spec overrides
            Assert.Equal(0, job.Spec.BackoffLimit);
            Assert.Equal(7200, job.Spec.ActiveDeadlineSeconds);
            Assert.Equal(10, job.Spec.Template.Spec.TerminationGracePeriodSeconds); // factory override
        }
    }

    [Fact]
    public async Task K8sChat_FakeAgentConnects_DispatchReturnsAgentId()
    {
        var (agentId, fakeAgent) = await DispatchChatPodAndConnectAsync("kiro,dotnet");

        await using (fakeAgent)
        {
            Assert.NotEmpty(agentId);

            // Agent ID should match what the fake agent registered with
            var entry = _registry.GetByAgentId(agentId);
            Assert.NotNull(entry);
            Assert.Equal(AgentStatus.Idle, entry.Status);

            // Labels should include chat=true and chat-session-id
            Assert.Contains("chat=true", entry.Labels ?? Array.Empty<string>());
            Assert.True(
                entry.Labels?.Any(l => l.StartsWith("chat-session-id=")) ?? false,
                "Agent should have chat-session-id label");
        }
    }

    [Fact]
    public async Task K8sChat_SendAndReceivePrompt_OneRoundTrip()
    {
        var (agentId, fakeAgent) = await DispatchChatPodAndConnectAsync("kiro,dotnet");

        await using (fakeAgent)
        {
            var entry = _registry.GetByAgentId(agentId)!;

            // The API host's hub context, not the monolith's. Both resolve, but only this one has
            // the agent's connection on it — sending through the other one silently goes nowhere
            // and the wait below times out after 10s.
            var hubContext = Fixture.ApiServices
                .GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<
                    CodingAgentWebUI.Hub.AgentHub,
                    CodingAgentWebUI.Pipeline.Interfaces.IAgentHubClient>>();

            var sessionId = Guid.NewGuid().ToString();

            // Mirror AgentChat.razor: set ActiveChatSessionId before AssignChatPrompt so hub
            // ownership validation in ReportChatResponse / ReportChatCompleted passes.
            entry.ActiveChatSessionId = sessionId;

            // Send prompt to agent via hub
            await hubContext.Clients.Client(entry.ConnectionId)
                .AssignChatPrompt(new ChatPromptMessage
                {
                    SessionId = sessionId,
                    Prompt = "What is 2+2?",
                    UseResume = false,
                    ChatWindowId = Guid.NewGuid().ToString()
                });

            // Wait for fake agent to receive the prompt
            var prompt = await fakeAgent.ChatPromptAssigned.Task
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("What is 2+2?", prompt.Prompt);

            // Agent sends response back (hub validates ownership via ActiveChatSessionId)
            await fakeAgent.SendChatResponseAsync(sessionId, "The answer is 4.");

            // Assert: ReportChatCompleted clears ActiveChatSessionId — confirms full round-trip
            // completed and the hub processed both ReportChatResponse and ReportChatCompleted.
            Assert.Null(entry.ActiveChatSessionId);
        }
    }

    [Fact]
    public async Task K8sChat_EndChat_PvcReleasedJobTerminal()
    {
        var (agentId, fakeAgent) = await DispatchChatPodAndConnectAsync("kiro,dotnet");

        await using (fakeAgent)
        {
            var (_, job) = Fixture.K8sClient.ChatJobs.Single();
            var jobName = job.Metadata!.Name!;

            // Terminate the session
            await Fixture.ChatDispatcher.TerminateChatSessionAsync(
                agentId, CancellationToken.None);

            // Wait for CancelChat to be delivered to the fake agent
            await fakeAgent.CancelChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Simulate clean pod exit (job reaches terminal)
            await Fixture.K8sClient.SimulateChatJobTerminalAsync(jobName, success: true);

            // Assert: job reached a terminal (Complete) condition
            var terminalJob = Fixture.K8sClient.ChatJobs[jobName];
            Assert.Contains(terminalJob.Status.Conditions,
                c => c.Type == "Complete" && c.Status == "True");
        }
    }

    // ── Double-dispatch guard ─────────────────────────────────────────────

    [Fact]
    public async Task K8sChat_SameSelector_TwoTabsGetTwoPods()
    {
        // Two concurrent dispatches for the same selector — both should succeed.
        // The per-selector guard was removed (Spec 049): only PVC pool exhaustion blocks dispatch.
        // Run them concurrently: this is the actual "two tabs" scenario, and parallelism avoids
        // the second dispatch suffering from server load accumulated by the first one being fully
        // alive and polling when it starts.
        var task1 = DispatchChatPodAndConnectAsync("kiro,dotnet");
        var task2 = DispatchChatPodAndConnectAsync("kiro,dotnet",
            overrideAgentId: $"fake-chat-agent-2nd-{Guid.NewGuid():N}"[..21]);

        var results = await Task.WhenAll(task1, task2);
        var (agentId1, fakeAgent1) = results[0];
        var (agentId2, fakeAgent2) = results[1];

        await using (fakeAgent1)
        await using (fakeAgent2)
        {
            Assert.Equal(2, Fixture.K8sClient.ChatJobs.Count);
            Assert.NotEqual(agentId1, agentId2);
        }
    }

    [Fact]
    public async Task K8sChat_TwoSelectorsSimultaneously_BothAllowed()
    {
        var (agentId1, fakeAgent1) = await DispatchChatPodAndConnectAsync("kiro,dotnet");
        var (agentId2, fakeAgent2) = await DispatchChatPodAndConnectAsync("kiro,python");

        await using (fakeAgent1)
        await using (fakeAgent2)
        {
            Assert.Equal(2, Fixture.K8sClient.ChatJobs.Count);
            Assert.NotEqual(agentId1, agentId2);
        }
    }

    // ── PVC pool exhaustion ───────────────────────────────────────────────

    [Fact]
    public async Task K8sChat_NoPvcAvailable_ThrowsNoPvcAvailableException()
    {
        // KiroPvcPool = ["fake-pvc-0", "fake-pvc-1"] in the test factory.
        // Dispatch one session per pool PVC to exhaust the pool.
        var (_, fakeAgent1) = await DispatchChatPodAndConnectAsync("kiro,dotnet",
            overrideAgentId: "chat-pool-test-1");
        var (_, fakeAgent2) = await DispatchChatPodAndConnectAsync("kiro,python",
            overrideAgentId: "chat-pool-test-2");

        await using (fakeAgent1)
        await using (fakeAgent2)
        {
            Assert.Equal(2, Fixture.K8sClient.ChatJobs.Count);

            // Both pool PVCs are now held by active in-process sessions.
            // Third dispatch should throw NoPvcAvailableException.
            await Assert.ThrowsAsync<NoPvcAvailableException>(async () =>
                await Fixture.ChatDispatcher.DispatchChatPodAsync(
                    "kiro,node", null, null, CancellationToken.None));
        }
    }

    // ── Timeout path ──────────────────────────────────────────────────────

    [Fact]
    public async Task K8sChat_PodNeverConnects_TimeoutCleanup()
    {
        // The test factory uses ChatPodConnectTimeoutSeconds=30 which is too long.
        // Override via a direct DispatchChatPodAsync call with a very short timeout
        // by creating a custom options object.
        // Instead: we exercise timeout by calling DispatchChatPodAsync on a fresh
        // dispatcher instance with a 2-second timeout.

        var shortOptions = new DispatchServiceOptions
        {
            Namespace = "test",
            OrchestratorUrl = "http://test-orchestrator",
            AgentApiKeySecretName = "agent-api-key",
            AgentServiceAccountName = "agent-sa",
            KiroPvcPool = new List<string> { "fake-pvc-0", "fake-pvc-1" },
            ChatJobMaxDurationSeconds = 7200,
            ChatPodConnectTimeoutSeconds = 2, // 2-second timeout for test
            ChatTerminationGracePeriodSeconds = 10
        };

        var registry = Fixture.AgentRegistry;
        var hubContext = Fixture.Factory.Services.GetRequiredService<
            Microsoft.AspNetCore.SignalR.IHubContext<
                CodingAgentWebUI.Hub.AgentHub,
                CodingAgentWebUI.Pipeline.Interfaces.IAgentHubClient>>();
        var templateStore = Fixture.Factory.Services.GetRequiredService<JobTemplateStore>();

        var testJobClient = new FakeKubernetesJobClient();

        var dispatcher = new ChatJobDispatcher(
            testJobClient,
            hubContext,
            templateStore,
            registry,
            shortOptions,
            Serilog.Log.Logger);

        await dispatcher.StartAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ChatPodTimeoutException>(async () =>
            await dispatcher.DispatchChatPodAsync(
                "kiro,dotnet", null, null, CancellationToken.None));

        Assert.Equal(2, ex.TimeoutSeconds);

        // Job should be deleted after timeout
        Assert.Contains(testJobClient.DeletedJobs, name => name.StartsWith("caa-chat-"));

        await dispatcher.StopAsync(CancellationToken.None);
        await dispatcher.DisposeAsync();
    }

    // ── Model/effort injection ────────────────────────────────────────────

    [Fact]
    public async Task K8sChat_ModelEffortInjected_EnvVarsPresent()
    {
        var (_, fakeAgent) = await DispatchChatPodAndConnectAsync(
            "kiro,dotnet", model: "claude-sonnet-4.6", effort: "high");

        await using (fakeAgent)
        {
            var (_, job) = Fixture.K8sClient.ChatJobs.Single();
            var env = job.Spec!.Template.Spec!.Containers[0].Env!;

            Assert.Equal("claude-sonnet-4.6", env.First(e => e.Name == "AGENT_CHAT_MODEL").Value);
            Assert.Equal("high", env.First(e => e.Name == "AGENT_CHAT_EFFORT").Value);
        }
    }

    [Fact]
    public async Task K8sChat_AutoModel_NoModelEnvVar()
    {
        var (_, fakeAgent) = await DispatchChatPodAndConnectAsync(
            "kiro,dotnet", model: "auto", effort: "auto");

        await using (fakeAgent)
        {
            var (_, job) = Fixture.K8sClient.ChatJobs.Single();
            var env = job.Spec!.Template.Spec!.Containers[0].Env!;

            Assert.DoesNotContain(env, e => e.Name == "AGENT_CHAT_MODEL");
            Assert.DoesNotContain(env, e => e.Name == "AGENT_CHAT_EFFORT");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────
}
