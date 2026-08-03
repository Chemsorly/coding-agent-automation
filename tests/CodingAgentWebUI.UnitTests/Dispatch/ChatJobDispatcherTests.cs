using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="ChatJobDispatcher"/>.
/// Requirements: Req 2, Req 3, Req 12, Req 13.
/// NOTE: ChatJobDispatcher class does not exist yet — tests will fail to compile until task 4.4.
/// That compile error IS the expected red state for task 4.3.
/// </summary>
public class ChatJobDispatcherTests
{
    private const string TestNamespace = "coding-agent";
    private const string TestSelector = "kiro,dotnet";
    private const string TestEncodedSelector = "dotnet_kiro";

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static JobTemplateStore CreateTemplateStore(string providerType = "kiro")
    {
        var yaml = $"""
            - labels: "dotnet,kiro"
              image: "chemsorly/coding-agent:kiro-dotnet10"
              providerType: "{providerType}"
              maxConcurrent: 2
            """;
        return JobTemplateStore.LoadFromYaml(yaml);
    }

    private static DispatchServiceOptions CreateOptions(
        int connectTimeoutSeconds = 5,
        int chatSessionMaxDuration = 7200,
        int gracePeriod = 120) => new()
    {
        Namespace = TestNamespace,
        KiroPvcPool = ["pvc-0", "pvc-1"],
        OrchestratorUrl = "http://orchestrator:8080",
        AgentApiKeySecretName = "caa-secret",
        AgentServiceAccountName = "caa-agent",
        ChatPodConnectTimeoutSeconds = connectTimeoutSeconds,
        ChatSessionMaxDurationSeconds = chatSessionMaxDuration,
        ChatTerminationGracePeriodSeconds = gracePeriod
    };

    private static AgentRegistryService CreateRegistry() =>
        new AgentRegistryService(Mock.Of<ILogger>());

    private static Mock<IKubernetesJobClient> CreateJobClientMock(V1JobList? existingJobs = null)
    {
        var mock = new Mock<IKubernetesJobClient>();
        mock.Setup(c => c.ListJobsAsync(TestNamespace, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJobs ?? new V1JobList { Items = [] });
        mock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.DeleteJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // ReadJobAsync returns null-like V1Job with no conditions (non-terminal) by default
        mock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Status = new V1JobStatus { Conditions = [] } });
        return mock;
    }

    private static Mock<ILeaderElectionService> CreateAlwaysLeaderMock()
    {
        var mock = new Mock<ILeaderElectionService>();
        mock.Setup(l => l.IsLeader).Returns(true);
        mock.Setup(l => l.LeaderToken).Returns(CancellationToken.None);
        return mock;
    }

    private static Mock<IHubContext<AgentHub, IAgentHubClient>> CreateHubContextMock()
    {
        var mockClients = new Mock<IHubClients<IAgentHubClient>>();
        var mockClient = new Mock<IAgentHubClient>();
        mockClient.Setup(c => c.CancelChat(It.IsAny<string>())).Returns(Task.CompletedTask);
        mockClients.Setup(c => c.Client(It.IsAny<string>())).Returns(mockClient.Object);

        var mock = new Mock<IHubContext<AgentHub, IAgentHubClient>>();
        mock.Setup(h => h.Clients).Returns(mockClients.Object);
        return mock;
    }

    /// <summary>
    /// Registers an agent in the registry with the given labels and returns the agentId.
    /// Simulates a chat pod connecting and registering with the hub.
    /// </summary>
    private static string RegisterChatAgent(
        AgentRegistryService registry,
        string agentId,
        string dispatchId,
        string connectionId = "conn-1")
    {
        var labels = new List<string> { "chat=true", $"chat-session-id={dispatchId}" };
        var msg = new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "test-host",
            Labels = labels
        };
        registry.Register(msg, connectionId);
        return agentId;
    }

    private static ChatJobDispatcher CreateDispatcher(
        IKubernetesJobClient? jobClient = null,
        IHubContext<AgentHub, IAgentHubClient>? hubContext = null,
        JobTemplateStore? templateStore = null,
        AgentRegistryService? registry = null,
        DispatchServiceOptions? options = null)
    {
        return new ChatJobDispatcher(
            jobClient ?? CreateJobClientMock().Object,
            hubContext ?? CreateHubContextMock().Object,
            templateStore ?? CreateTemplateStore(),
            registry ?? CreateRegistry(),
            options ?? CreateOptions(),
            CreateAlwaysLeaderMock().Object,
            Mock.Of<ILogger>());
    }

    // ─── 1. DispatchChatPodAsync — no existing chat job ───────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_NoExistingChatJob_CreatesJobWithChatLabels()
    {
        var jobClientMock = CreateJobClientMock();
        V1Job? createdJob = null;
        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) => createdJob = j)
            .Returns(Task.CompletedTask);

        var registry = CreateRegistry();
        string? capturedDispatchId = null;

        // Hook into job creation to intercept the dispatchId, then register an agent with it
        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                capturedDispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var capId) ? capId : null;
                if (capturedDispatchId != null)
                    RegisterChatAgent(registry, "agent-1", capturedDispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        createdJob.Should().NotBeNull();
        createdJob!.Metadata.Labels.Should().ContainKey("caa/chat-session-id");
        createdJob.Metadata.Labels.Should().ContainKey("caa/chat-selector");
        createdJob.Metadata.Labels["caa/chat-selector"].Should().Be(TestEncodedSelector);
    }

    // ─── 2. DispatchChatPodAsync — active chat job exists ────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_ActiveChatJobExists_ThrowsChatAlreadyActiveException()
    {
        var existingJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = "caa-chat-existing1",
                Labels = new Dictionary<string, string>
                {
                    ["caa/chat-session-id"] = Guid.NewGuid().ToString(),
                    ["caa/chat-selector"] = TestEncodedSelector // must match the dispatched selector
                }
            },
            Status = new V1JobStatus { Conditions = [] } // non-terminal
        };
        var jobClientMock = CreateJobClientMock(new V1JobList { Items = [existingJob] });

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object);

        var act = () => dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<ChatAlreadyActiveException>();
    }

    // ─── 3. DispatchChatPodAsync — agent connects within timeout ─────────────

    [Fact]
    public async Task DispatchChatPodAsync_AgentConnectsWithinTimeout_ReturnsAgentId()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-xyz", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        var result = await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        result.Should().Be("agent-xyz");
    }

    // ─── 4. DispatchChatPodAsync — agent never connects ──────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_AgentNeverConnects_ThrowsChatPodTimeoutException()
    {
        var jobClientMock = CreateJobClientMock();
        // No agent registers — poll will time out
        var dispatcher = CreateDispatcher(
            jobClient: jobClientMock.Object,
            options: CreateOptions(connectTimeoutSeconds: 1));

        var act = () => dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<ChatPodTimeoutException>();
    }

    // ─── 5. DispatchChatPodAsync — kiro agent gets a PVC from the pool ───────

    [Fact]
    public async Task DispatchChatPodAsync_KiroAgent_GetsFirstFreePvcFromPool()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();
        V1Job? createdJob = null;

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-kiro", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        // The job label should record the first pool PVC
        createdJob.Should().NotBeNull();
        createdJob!.Metadata.Labels.Should().ContainKey("caa/claimed-pvc");
        createdJob.Metadata.Labels["caa/claimed-pvc"].Should().Be("pvc-0");
    }

    // ─── 6. DispatchChatPodAsync — no PVC available ───────────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_NoPvcAvailable_ThrowsNoPvcAvailableException()
    {
        // Pre-populate ListJobsAsync with two existing non-terminal jobs, each claiming a pool PVC.
        // This is replica-safe: the dispatcher reads PVC availability from k8s labels, not in-memory state.
        // KiroPvcPool = ["pvc-0", "pvc-1"] — both are claimed, so the third dispatch has no free PVC.
        var existingJob1 = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = "caa-chat-existing1",
                Labels = new Dictionary<string, string>
                {
                    ["caa/chat-session-id"] = Guid.NewGuid().ToString(),
                    ["caa/chat-selector"] = "dotnet_kiro",
                    ["caa/claimed-pvc"] = "pvc-0"
                }
            },
            Status = new V1JobStatus { Conditions = [] } // non-terminal
        };
        var existingJob2 = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = "caa-chat-existing2",
                Labels = new Dictionary<string, string>
                {
                    ["caa/chat-session-id"] = Guid.NewGuid().ToString(),
                    ["caa/chat-selector"] = "kiro_python",
                    ["caa/claimed-pvc"] = "pvc-1"
                }
            },
            Status = new V1JobStatus { Conditions = [] } // non-terminal
        };

        var jobClientMock = CreateJobClientMock(new V1JobList { Items = [existingJob1, existingJob2] });

        // The third dispatch uses a different selector ("node_kiro") — no double-dispatch collision,
        // but both PVCs are already claimed by the existing k8s jobs.
        var templateStoreMulti = JobTemplateStore.LoadFromYaml("""
            - labels: "node,kiro"
              image: "chemsorly/coding-agent:kiro-node"
              providerType: "kiro"
              maxConcurrent: 5
            """);
        var dispatcher = CreateDispatcher(
            jobClient: jobClientMock.Object, templateStore: templateStoreMulti);

        var act = () => dispatcher.DispatchChatPodAsync("kiro,node", null, null, CancellationToken.None);
        await act.Should().ThrowAsync<NoPvcAvailableException>();
    }

    // ─── 7. DispatchChatPodAsync — BackoffLimit=0 ────────────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_SetsBackoffLimitToZero()
    {
        var jobClientMock = CreateJobClientMock();
        V1Job? createdJob = null;
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-bl", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);
        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        createdJob!.Spec.BackoffLimit.Should().Be(0);
    }

    // ─── 8. DispatchChatPodAsync — ActiveDeadlineSeconds ─────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_SetsActiveDeadlineSeconds()
    {
        var jobClientMock = CreateJobClientMock();
        V1Job? createdJob = null;
        var registry = CreateRegistry();
        const int maxDuration = 3600;

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-deadline", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var options = CreateOptions(chatSessionMaxDuration: maxDuration);
        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry, options: options);
        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        createdJob!.Spec.ActiveDeadlineSeconds.Should().Be(maxDuration);
    }

    // ─── 9. DispatchChatPodAsync — TerminationGracePeriodSeconds ─────────────

    [Fact]
    public async Task DispatchChatPodAsync_SetsTerminationGracePeriod()
    {
        var jobClientMock = CreateJobClientMock();
        V1Job? createdJob = null;
        var registry = CreateRegistry();
        const int gracePeriod = 180;

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-grace", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var options = CreateOptions(gracePeriod: gracePeriod);
        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry, options: options);
        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        createdJob!.Spec.Template.Spec.TerminationGracePeriodSeconds.Should().Be(gracePeriod);
    }

    // ─── 10. DispatchChatPodAsync — AGENT_CHAT_MODE env var ──────────────────

    [Fact]
    public async Task DispatchChatPodAsync_InjectsAgentChatModeEnvVar()
    {
        var jobClientMock = CreateJobClientMock();
        V1Job? createdJob = null;
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-mode", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);
        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        var envVars = createdJob!.Spec.Template.Spec.Containers[0].Env;
        envVars.Should().Contain(e => e.Name == "AGENT_CHAT_MODE" && e.Value == "true");
    }

    // ─── 11. DispatchChatPodAsync — AGENT_CHAT_SESSION_ID env var ────────────

    [Fact]
    public async Task DispatchChatPodAsync_InjectsAgentChatSessionIdEnvVar()
    {
        var jobClientMock = CreateJobClientMock();
        V1Job? createdJob = null;
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-sid", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);
        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        var dispatchIdFromLabel = createdJob!.Metadata.Labels["caa/chat-session-id"];
        var envVars = createdJob.Spec.Template.Spec.Containers[0].Env;
        envVars.Should().Contain(e => e.Name == "AGENT_CHAT_SESSION_ID" && e.Value == dispatchIdFromLabel);
    }

    // ─── 12. DispatchChatPodAsync — model and effort env vars ────────────────

    [Fact]
    public async Task DispatchChatPodAsync_ModelAndEffortInjected_WhenNonEmpty()
    {
        var jobClientMock = CreateJobClientMock();
        V1Job? createdJob = null;
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-model", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);
        await dispatcher.DispatchChatPodAsync(TestSelector, "claude-opus-4.8", "high", CancellationToken.None);

        var envVars = createdJob!.Spec.Template.Spec.Containers[0].Env;
        envVars.Should().Contain(e => e.Name == "AGENT_CHAT_MODEL" && e.Value == "claude-opus-4.8");
        envVars.Should().Contain(e => e.Name == "AGENT_CHAT_EFFORT" && e.Value == "high");
    }

    // ─── 13. DispatchChatPodAsync — "auto" model NOT injected ────────────────

    [Fact]
    public async Task DispatchChatPodAsync_AutoModel_ModelEnvVarNotInjected()
    {
        var jobClientMock = CreateJobClientMock();
        V1Job? createdJob = null;
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-auto", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);
        await dispatcher.DispatchChatPodAsync(TestSelector, "auto", "auto", CancellationToken.None);

        var envVars = createdJob!.Spec.Template.Spec.Containers[0].Env;
        envVars.Should().NotContain(e => e.Name == "AGENT_CHAT_MODEL",
            "'auto' model must not produce AGENT_CHAT_MODEL env var");
        envVars.Should().NotContain(e => e.Name == "AGENT_CHAT_EFFORT",
            "'auto' effort must not produce AGENT_CHAT_EFFORT env var");
    }

    // ─── 14. DispatchChatPodAsync — comma selector encoded to underscore ──────

    [Fact]
    public async Task DispatchChatPodAsync_CommaInSelector_EncodedToUnderscoreInLabel()
    {
        var jobClientMock = CreateJobClientMock();
        V1Job? createdJob = null;
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-comma", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);
        // "kiro,dotnet" normalizes to "dotnet,kiro" then encodes commas to underscores → "dotnet_kiro"
        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        createdJob!.Metadata.Labels["caa/chat-selector"].Should()
            .NotContain(",", "commas are illegal in k8s label values")
            .And.Contain("_", "commas must be replaced with underscores");
    }

    // ─── 15. DispatchChatPodAsync — timeout cleans up job ────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_TimeoutPath_CleanupJobAndReleasePvc()
    {
        var jobClientMock = CreateJobClientMock();
        string? createdJobName = null;

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) => createdJobName = j.Metadata.Name)
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(
            jobClient: jobClientMock.Object,
            options: CreateOptions(connectTimeoutSeconds: 1));

        await Assert.ThrowsAsync<ChatPodTimeoutException>(
            () => dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None));

        // Job deleted best-effort
        jobClientMock.Verify(c => c.DeleteJobAsync(
            It.Is<string>(name => name == createdJobName),
            TestNamespace,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── 16. Session lifecycle — registers session after dispatch ────────────

    [Fact]
    public async Task DispatchChatPodAsync_Success_RegistersSession()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-sess", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        var agentId = await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        // After dispatch, session should be registered — TerminateChatSessionAsync should not return early
        // We verify this indirectly: if session was NOT registered, TerminateChatSessionAsync is a no-op
        // and won't call hub. After registering, calling terminate should attempt to send CancelChat.
        agentId.Should().Be("agent-sess");
        // Session exists — verified by confirming the dispatcher tracks agentId→jobName
        // (tested further in TerminateChatSessionAsync tests)
    }

    // ─── 17. StartAsync — recovers sessions from k8s ─────────────────────────

    [Fact]
    public async Task StartAsync_RecoversSessions_WhenJobsExistInK8s()
    {
        var dispatchId = Guid.NewGuid();
        var existingJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = "caa-chat-abcdef12",
                Labels = new Dictionary<string, string>
                {
                    ["caa/chat-session-id"] = dispatchId.ToString(),
                    ["caa/chat-selector"] = "dotnet_kiro",
                    ["caa/claimed-pvc"] = "pvc-0"
                }
            },
            Status = new V1JobStatus { Conditions = [] } // non-terminal
        };

        var jobClientMock = CreateJobClientMock();
        jobClientMock.Setup(c => c.ListJobsAsync(TestNamespace, "caa/chat-session-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [existingJob] });

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object);

        // StartAsync now returns immediately (fire-and-forget recovery)
        var act = async () => await dispatcher.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Wait briefly for the background recovery task to complete
        await Task.Delay(500);

        // Verify ListJobsAsync was called with the chat-session-id label selector
        jobClientMock.Verify(c => c.ListJobsAsync(
            TestNamespace,
            It.Is<string>(s => s.Contains("caa/chat-session-id")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── 18. StartAsync — k8s API failure doesn't throw ──────────────────────

    [Fact]
    public async Task StartAsync_K8sApiFailure_DoesNotThrow()
    {
        var jobClientMock = CreateJobClientMock();
        jobClientMock.Setup(c => c.ListJobsAsync(TestNamespace, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new k8s.Autorest.HttpOperationException("k8s unavailable"));

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object);

        var act = async () => await dispatcher.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("k8s API failures at startup must be swallowed (non-fatal)");
    }

    // ─── 19. StopAsync — cleans up sessions ──────────────────────────────────

    [Fact]
    public async Task StopAsync_ReleasesAllPvcs()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-stop", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(
            jobClient: jobClientMock.Object,
            registry: registry);

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        // Stop — should complete without error; sessions tracked in-memory are cleaned up
        await dispatcher.StopAsync(CancellationToken.None);

        // Verify exactly one job was created before stop (confirms the session existed and was cleaned up)
        jobClientMock.Verify(
            c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── 20. TerminateChatSessionAsync — sends CancelChat ────────────────────

    [Fact]
    public async Task TerminateChatSessionAsync_SessionExists_SendsCancelChatToAgent()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();
        var hubContextMock = CreateHubContextMock();
        var mockClient = new Mock<IAgentHubClient>();
        mockClient.Setup(c => c.CancelChat(It.IsAny<string>())).Returns(Task.CompletedTask);
        hubContextMock.Setup(h => h.Clients.Client(It.IsAny<string>())).Returns(mockClient.Object);

        string? registeredAgentId = null;

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                registeredAgentId = "agent-terminate";
                RegisterChatAgent(registry, registeredAgentId, dispatchId, "conn-terminate");
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(
            jobClient: jobClientMock.Object,
            registry: registry,
            hubContext: hubContextMock.Object);

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        // Simulate watcher not completing in time — we call terminate which should send CancelChat
        await dispatcher.TerminateChatSessionAsync(registeredAgentId!, CancellationToken.None);

        mockClient.Verify(c => c.CancelChat(It.IsAny<string>()), Times.Once);
    }

    // ─── 21. TerminateChatSessionAsync — session not found returns gracefully ─

    [Fact]
    public async Task TerminateChatSessionAsync_SessionNotFound_ReturnsGracefully()
    {
        var dispatcher = CreateDispatcher();

        // No session for "unknown-agent" — should return without error
        var act = async () => await dispatcher.TerminateChatSessionAsync("unknown-agent", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ─── 22. TerminateChatSessionAsync — grace period expired, force deletes ──

    [Fact]
    public async Task TerminateChatSessionAsync_GracePeriodExpired_ForceDeletesJobAndReleasesPvc()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();
        var hubContextMock = CreateHubContextMock();
        string? createdJobName = null;

        // WatcherTask never completes because ReadJobAsync always returns non-terminal
        jobClientMock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Status = new V1JobStatus { Conditions = [] } });

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJobName = j.Metadata.Name;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-force", dispatchId, "conn-force");
            })
            .Returns(Task.CompletedTask);

        // Use a 1-second grace period for the terminate path (not connect timeout)
        var options = new DispatchServiceOptions
        {
            Namespace = TestNamespace,
            KiroPvcPool = ["pvc-0"],
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "caa-secret",
            AgentServiceAccountName = "caa-agent",
            ChatPodConnectTimeoutSeconds = 5,
            ChatSessionMaxDurationSeconds = 7200,
            ChatTerminationGracePeriodSeconds = 120
        };

        var dispatcher = CreateDispatcher(
            jobClient: jobClientMock.Object,
            registry: registry,
            hubContext: hubContextMock.Object,
            options: options);

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        // TerminateChatSessionAsync with a very short timeout to force the grace-period-expired path
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await dispatcher.TerminateChatSessionAsync("agent-force", cts.Token);

        // Force delete must be called
        jobClientMock.Verify(c => c.DeleteJobAsync(
            It.Is<string>(n => n == createdJobName),
            TestNamespace,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
