using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration.Dispatch;
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
        AgentJobTimeoutSeconds = chatSessionMaxDuration,
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
        // ReadJobAsync returns non-terminal by default
        mock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Status = new V1JobStatus { Conditions = [] } });
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
    /// Registers an agent in the registry simulating a chat pod connecting.
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

    /// <summary>
    /// Creates a ChatJobDispatcher. No ILeaderElectionService parameter — removed in this refactor.
    /// </summary>
    private static ChatJobDispatcher CreateDispatcher(
        IKubernetesJobClient? jobClient = null,
        IHubContext<AgentHub, IAgentHubClient>? hubContext = null,
        JobTemplateStore? templateStore = null,
        AgentRegistryService? registry = null,
        DispatchServiceOptions? options = null,
        CodingAgentWebUI.Orchestration.Redis.IRedisStore? redis = null)
    {
        return new ChatJobDispatcher(
            jobClient ?? CreateJobClientMock().Object,
            hubContext ?? CreateHubContextMock().Object,
            templateStore ?? CreateTemplateStore(),
            registry ?? CreateRegistry(),
            options ?? CreateOptions(),
            Mock.Of<ILogger>(),
            redis);
    }

    // ─── 1. DispatchChatPodAsync — no existing chat job ───────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_NoExistingChatJob_CreatesJobWithChatLabels()
    {
        var jobClientMock = CreateJobClientMock();
        V1Job? createdJob = null;
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var capturedDispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var capId) ? capId : null;
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

    // ─── 2. DispatchChatPodAsync — --mode=chat in args ───────────────────────

    /// <summary>
    /// Spec 044 Req C5.1, C5.1a: ChatJobDispatcher MUST emit --mode=chat in the container Args.
    /// </summary>
    [Fact]
    public async Task DispatchChatPodAsync_PodSpec_ContainsModeChat()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();
        V1Job? createdJob = null;

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                if (!string.IsNullOrEmpty(dispatchId))
                    RegisterChatAgent(registry, "agent-mode-test", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        createdJob.Should().NotBeNull("job must be created");
        var container = createdJob!.Spec.Template.Spec.Containers[0];
        container.Args.Should().Contain("--mode=chat",
            "chat pods must emit --mode=chat so AgentStartupConfig can identify the pod shape (Spec 044 Req C5.1a)");
        container.Args.Should().NotContain("--mode=workitem",
            "chat pods must not emit --mode=workitem");
    }

    // ─── 3. DispatchChatPodAsync — agent connects within timeout ─────────────

    [Fact]
    public async Task DispatchChatPodAsync_AgentConnectsWithinTimeout_ReturnsAgentId()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();
        string? capturedJobName = null;

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                capturedJobName = j.Metadata.Name;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, capturedJobName!, dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        var result = await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        result.Should().Be(capturedJobName, "returned agentId must equal the job name");
    }

    // ─── 4. DispatchChatPodAsync — agent never connects ──────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_AgentNeverConnects_ThrowsChatPodTimeoutException()
    {
        var jobClientMock = CreateJobClientMock();
        var dispatcher = CreateDispatcher(
            jobClient: jobClientMock.Object,
            options: CreateOptions(connectTimeoutSeconds: 1));

        var act = () => dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<ChatPodTimeoutException>();
    }

    // ─── 5. DispatchChatPodAsync — kiro agent gets PVC from pool ─────────────

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

        createdJob.Should().NotBeNull();
        createdJob!.Metadata.Labels.Should().ContainKey("caa/claimed-pvc");
        createdJob.Metadata.Labels["caa/claimed-pvc"].Should().Be("pvc-0");
    }

    // ─── 6. DispatchChatPodAsync — no PVC available ───────────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_NoPvcAvailable_ThrowsNoPvcAvailableException()
    {
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
            Status = new V1JobStatus { Conditions = [] }
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
            Status = new V1JobStatus { Conditions = [] }
        };

        var jobClientMock = CreateJobClientMock(new V1JobList { Items = [existingJob1, existingJob2] });

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

    [Fact]
    public async Task DispatchChatPodAsync_InjectsAgentProviderTypeEnvVar()
    {
        var jobClientMock = CreateJobClientMock();
        V1Job? createdJob = null;
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-provtype", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);
        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        var envVars = createdJob!.Spec.Template.Spec.Containers[0].Env;
        envVars.Should().Contain(e => e.Name == "AGENT_PROVIDER_TYPE" && e.Value == "kiro",
            "chat pod must know its provider type so ChatJobHandler picks the right execution path");
    }

    [Fact]
    public async Task DispatchChatPodAsync_OpenCodeTemplate_InjectsOpenCodeProviderType()
    {
        var jobClientMock = CreateJobClientMock();
        V1Job? createdJob = null;
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJob = j;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-opencode-provtype", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(
            jobClient: jobClientMock.Object,
            registry: registry,
            templateStore: CreateTemplateStore(providerType: "opencode"));
        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        var envVars = createdJob!.Spec.Template.Spec.Containers[0].Env;
        envVars.Should().Contain(e => e.Name == "AGENT_PROVIDER_TYPE" && e.Value == "opencode",
            "opencode chat pod must get AGENT_PROVIDER_TYPE=opencode to activate the OpenCode execution path");
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

        jobClientMock.Verify(c => c.DeleteJobAsync(
            It.Is<string>(name => name == createdJobName),
            TestNamespace,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── 16. Session lifecycle — watcher tracked after dispatch ──────────────

    [Fact]
    public async Task DispatchChatPodAsync_Success_SessionTrackedInActiveWatchers()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();
        string? capturedJobName = null;

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                capturedJobName = j.Metadata.Name; // agentId == jobName in real pods
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                // Register with jobName as agentId — matches the real-world invariant
                RegisterChatAgent(registry, capturedJobName!, dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        var agentId = await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        // agentId == jobName (the invariant) — HasActiveSession uses agentId as the key
        agentId.Should().Be(capturedJobName, "returned agentId must equal the job name");
        dispatcher.HasActiveSession(agentId).Should().BeTrue("watcher must be tracked after successful dispatch");
    }

    // ─── 17. StartAsync — is a no-op (RecoverSessionsAsync removed) ──────────

    [Fact]
    public async Task StartAsync_IsNoOp_ReturnsImmediately()
    {
        // RecoverSessionsAsync was removed — StartAsync must return immediately without any K8s call.
        // Jobs active before restart drain via ActiveDeadlineSeconds.
        var jobClientMock = CreateJobClientMock();
        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object);

        var act = async () => await dispatcher.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Must NOT call ListJobsAsync — no session recovery
        jobClientMock.Verify(
            c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── 18. Any replica can dispatch — no leader gate ───────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_NoLeaderCheck_AnyReplicaCanDispatch()
    {
        // Previously threw InvalidOperationException when not leader.
        // Now there is no leader check — dispatch is always allowed (guarded only by K8s double-dispatch check).
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                Assert.NotNull(dispatchId); // label must be present on dispatched job
                RegisterChatAgent(registry, "agent-nonleader", dispatchId);
            })
            .Returns(Task.CompletedTask);

        // No leader election service passed — any replica is treated the same
        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        // Must NOT throw — no leader gate
        var act = async () => await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);
        await act.Should().NotThrowAsync<InvalidOperationException>(
            "dispatch must not require leadership — all replicas can dispatch");

        jobClientMock.Verify(
            c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()),
            Times.Once,
            "job must be dispatched regardless of replica leadership");
    }

    // ─── 19. StopAsync — completes cleanly ────────────────────────────────────

    [Fact]
    public async Task StopAsync_CompletesCleanly()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();
        string? capturedJobName = null;

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                capturedJobName = j.Metadata.Name;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, capturedJobName!, dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        var act = async () => await dispatcher.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("StopAsync must complete without error even with active watchers");

        // After stop, session must be cleaned up
        dispatcher.HasActiveSession(capturedJobName!).Should().BeFalse("watcher must be removed after StopAsync");
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

        string? capturedJobName = null;

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                capturedJobName = j.Metadata.Name;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                // Register with job name as agentId — matches real-world invariant (agentId == jobName)
                RegisterChatAgent(registry, capturedJobName!, dispatchId, "conn-terminate");
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(
            jobClient: jobClientMock.Object,
            registry: registry,
            hubContext: hubContextMock.Object);

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);
        await dispatcher.TerminateChatSessionAsync(capturedJobName!, CancellationToken.None);

        mockClient.Verify(c => c.CancelChat(It.IsAny<string>()), Times.Once);
    }

    // ─── 21. TerminateChatSessionAsync — session not found returns gracefully ─

    [Fact]
    public async Task TerminateChatSessionAsync_SessionNotFound_ReturnsGracefully()
    {
        var dispatcher = CreateDispatcher();

        var act = async () => await dispatcher.TerminateChatSessionAsync("unknown-agent", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ─── 22. TerminateChatSessionAsync — watcher completes → no force delete ─

    [Fact]
    public async Task TerminateChatSessionAsync_WatcherCompletesWithinGrace_NoForceDelete()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();
        string? createdJobName = null;

        // First ReadJobAsync call returns terminal — watcher exits immediately
        jobClientMock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job
            {
                Status = new V1JobStatus
                {
                    Conditions = [new V1JobCondition { Type = "Complete", Status = "True" }]
                }
            });

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJobName = j.Metadata.Name;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, createdJobName!, dispatchId, "conn-clean");
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);
        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        // Wait for the watcher to see the terminal job and exit
        var watcherDone = await dispatcher.WaitForWatcherAsync(createdJobName!, TimeSpan.FromSeconds(15));
        watcherDone.Should().BeTrue("watcher must exit when job is terminal");

        // DeleteJobAsync must NOT have been called — watcher cleanup, not force-delete
        jobClientMock.Verify(c => c.DeleteJobAsync(
            It.Is<string>(n => n == createdJobName),
            TestNamespace,
            It.IsAny<CancellationToken>()),
            Times.Never,
            "force-delete must not be called when watcher completes naturally");
    }

    // ─── 23. TerminateChatSessionAsync — force deletes when watcher stalls ───

    [Fact]
    public async Task TerminateChatSessionAsync_WatcherStalls_ForceDeletesJob()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();
        string? createdJobName = null;

        // ReadJobAsync always returns non-terminal — watcher never exits on its own
        jobClientMock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Status = new V1JobStatus { Conditions = [] } });

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJobName = j.Metadata.Name;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, createdJobName!, dispatchId, "conn-force");
            })
            .Returns(Task.CompletedTask);

        // Very short grace period so the force-delete path triggers quickly in tests
        var options = new DispatchServiceOptions
        {
            Namespace = TestNamespace,
            KiroPvcPool = ["pvc-0"],
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "caa-secret",
            AgentServiceAccountName = "caa-agent",
            ChatPodConnectTimeoutSeconds = 5,
            AgentJobTimeoutSeconds = 7200,
            ChatTerminationGracePeriodSeconds = 1   // 1 second grace → force-delete triggers fast
        };

        var dispatcher = CreateDispatcher(
            jobClient: jobClientMock.Object,
            registry: registry,
            options: options);

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);
        await dispatcher.TerminateChatSessionAsync(createdJobName!, CancellationToken.None);

        jobClientMock.Verify(c => c.DeleteJobAsync(
            It.Is<string>(n => n == createdJobName),
            TestNamespace,
            It.IsAny<CancellationToken>()),
            Times.Once,
            "force-delete must be called when watcher does not complete within grace period");
    }

    // ─── Static helpers ───────────────────────────────────────────────────────

    [Fact]
    public void IsTerminal_CompleteConditionTrue_ReturnsTrue()
    {
        var job = new V1Job { Status = new V1JobStatus { Conditions = [new V1JobCondition { Type = "Complete", Status = "True" }] } };
        ChatJobDispatcher.IsTerminal(job).Should().BeTrue();
    }

    [Fact]
    public void IsTerminal_FailedConditionTrue_ReturnsTrue()
    {
        var job = new V1Job { Status = new V1JobStatus { Conditions = [new V1JobCondition { Type = "Failed", Status = "True" }] } };
        ChatJobDispatcher.IsTerminal(job).Should().BeTrue();
    }

    [Fact]
    public void IsTerminal_CompleteConditionFalse_ReturnsFalse()
    {
        var job = new V1Job { Status = new V1JobStatus { Conditions = [new V1JobCondition { Type = "Complete", Status = "False" }] } };
        ChatJobDispatcher.IsTerminal(job).Should().BeFalse();
    }

    [Fact]
    public void IsTerminal_NullStatus_ReturnsFalse()
    {
        var job = new V1Job { Status = null };
        ChatJobDispatcher.IsTerminal(job).Should().BeFalse();
    }

    [Fact]
    public void IsTerminal_NullConditions_ReturnsFalse()
    {
        var job = new V1Job { Status = new V1JobStatus { Conditions = null } };
        ChatJobDispatcher.IsTerminal(job).Should().BeFalse();
    }

    [Fact]
    public void IsTerminal_EmptyConditions_ReturnsFalse()
    {
        var job = new V1Job { Status = new V1JobStatus { Conditions = [] } };
        ChatJobDispatcher.IsTerminal(job).Should().BeFalse();
    }

    [Theory]
    [InlineData("kiro")]
    [InlineData("KIRO")]
    [InlineData("Kiro")]
    public void IsKiroAgent_KiroVariants_ReturnsTrue(string providerType)
        => ChatJobDispatcher.IsKiroAgent(providerType).Should().BeTrue();

    [Theory]
    [InlineData("opencode")]
    [InlineData("")]
    [InlineData("kiro-dotnet")]
    public void IsKiroAgent_NonKiro_ReturnsFalse(string providerType)
        => ChatJobDispatcher.IsKiroAgent(providerType).Should().BeFalse();

    [Theory]
    [InlineData("opencode")]
    [InlineData("OPENCODE")]
    [InlineData("OpenCode")]
    public void IsOpencodeAgent_OpencodeVariants_ReturnsTrue(string providerType)
        => ChatJobDispatcher.IsOpencodeAgent(providerType).Should().BeTrue();

    [Theory]
    [InlineData("kiro")]
    [InlineData("")]
    [InlineData("opencode-dotnet")]
    public void IsOpencodeAgent_NonOpencode_ReturnsFalse(string providerType)
        => ChatJobDispatcher.IsOpencodeAgent(providerType).Should().BeFalse();

    // ─── IsNotFound ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Operation returned an invalid status code 'NotFound'")]
    [InlineData("jobs.batch \"caa-chat-abc\" not found")]
    [InlineData("404 Not Found")]
    [InlineData("HTTP 404")]
    public void IsNotFound_KnownNotFoundMessages_ReturnsTrue(string message)
        => ChatJobDispatcher.IsNotFound(new Exception(message)).Should().BeTrue();

    [Theory]
    [InlineData("Connection refused")]
    [InlineData("Timeout")]
    [InlineData("500 Internal Server Error")]
    public void IsNotFound_TransientOrOtherErrors_ReturnsFalse(string message)
        => ChatJobDispatcher.IsNotFound(new Exception(message)).Should().BeFalse();

    // ─── 24. ForceDeleteAndCleanupAsync — calls Deregister before CleanupSession (issue #2109) ─

    /// <summary>
    /// When the grace period expires and the K8s job is force-deleted,
    /// <c>ForceDeleteAndCleanupAsync</c> must call <c>_registry.Deregister</c> so the
    /// chat agent entry is fully removed and does not remain as a ghost in the UI.
    /// </summary>
    [Fact]
    public async Task TerminateChatSessionAsync_WatcherStalls_CallsRegistryDeregister()
    {
        var jobClientMock = CreateJobClientMock();
        var registryMock = new Mock<IAgentRegistryService>();
        string? createdJobName = null;
        string? capturedDispatchId = null;

        // ReadJobAsync always returns non-terminal — watcher never exits on its own
        jobClientMock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Status = new V1JobStatus { Conditions = [] } });

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJobName = j.Metadata.Name;
                capturedDispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";

                // Wire up the mock registry so GetAgentsByLabel (used in PollForAgentConnection) returns
                // the connected agent, and GetByAgentId (used in TrySendCancelChat) returns it too.
                var labels = new List<string> { "chat=true", $"chat-session-id={capturedDispatchId}" };
                var agentEntry = new AgentEntry
                {
                    AgentId = createdJobName!,
                    ConnectionId = "conn-force-reg",
                    Hostname = "test-host",
                    Labels = labels,
                    Status = AgentStatus.Idle,
                    RegisteredAt = DateTimeOffset.UtcNow
                };
                registryMock.Setup(r => r.GetAgentsByLabel("chat-session-id", capturedDispatchId!))
                    .Returns(new List<AgentEntry> { agentEntry });
                registryMock.Setup(r => r.GetByAgentId(createdJobName!))
                    .Returns(agentEntry);
                // TODO: The Deregister stub is registered here inside CreateJobAsync callback, meaning it
                // is only wired up after job creation completes. If ForceDeleteAndCleanupAsync calls
                // Deregister before this callback fires (race condition), the call hits an unstubbed mock
                // and returns false. Also note the Times.Once verify below relies on createdJobName being
                // set synchronously in this callback — if the callback hasn't fired by assertion time,
                // createdJobName could be null, and It.Is<AgentId>(a => a.Value == createdJobName) would
                // never match. Consider moving the Deregister setup outside the callback.
                // See review finding: TestQualityReviewer [WARNING] ChatJobDispatcherTests.cs:926
                registryMock.Setup(r => r.Deregister(It.IsAny<AgentId>())).Returns(true);
            })
            .Returns(Task.CompletedTask);

        // Very short grace period → force-delete path triggers fast
        var options = new DispatchServiceOptions
        {
            Namespace = TestNamespace,
            KiroPvcPool = ["pvc-0"],
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "caa-secret",
            AgentServiceAccountName = "caa-agent",
            ChatPodConnectTimeoutSeconds = 5,
            AgentJobTimeoutSeconds = 7200,
            ChatTerminationGracePeriodSeconds = 1
        };

        var dispatcher = new ChatJobDispatcher(
            jobClientMock.Object,
            CreateHubContextMock().Object,
            CreateTemplateStore(),
            registryMock.Object,
            options,
            Mock.Of<ILogger>());

        await dispatcher.StartAsync(CancellationToken.None);
        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);
        // TODO: This test has a potential timing hazard. TerminateChatSessionAsync initiates termination
        // but ForceDeleteAndCleanupAsync runs after the grace period elapses (ChatTerminationGracePeriodSeconds=1).
        // If TerminateChatSessionAsync returns before ForceDeleteAndCleanupAsync executes, the Deregister
        // call may not have fired when registryMock.Verify runs below — making the test intermittently green.
        // There is no clock abstraction or explicit delay here to guarantee the force-delete path was reached.
        // Consider injecting a time abstraction or awaiting a signal from the dispatcher to make this deterministic.
        // See review finding: TestQualityReviewer [WARNING] ChatJobDispatcherTests.cs:970
        await dispatcher.TerminateChatSessionAsync(createdJobName!, CancellationToken.None);

        // The registry Deregister must be called for the chat agent's agentId
        registryMock.Verify(r => r.Deregister(
            It.Is<AgentId>(a => a.Value == createdJobName)),
            Times.Once,
            "ForceDeleteAndCleanupAsync must call Deregister to remove the chat agent from the registry");
    }

    // ─── 404-loop regression test ─────────────────────────────────────────────

    /// <summary>
    /// Regression: when ReadJobAsync throws 404, the watcher must exit — not retry forever.
    /// </summary>
    [Fact]
    public async Task WatcherTask_WhenReadJobAsyncThrowsNotFound_SessionIsRemovedWithoutRetry()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();

        var notFoundEx = new Exception("Operation returned an invalid status code 'NotFound'");
        jobClientMock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(notFoundEx);

        string? capturedJobName = null;
        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                capturedJobName = j.Metadata.Name;
                var capturedDispatchId = j.Metadata.Labels["caa/chat-session-id"];
                // Register with job name as agentId — matches real-world invariant
                RegisterChatAgent(registry, capturedJobName!, capturedDispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        var agentId = await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);
        agentId.Should().Be(capturedJobName, "returned agentId must equal job name");

        // Watcher must exit on 404, not retry forever
        var watcherCompleted = await dispatcher.WaitForWatcherAsync(agentId, TimeSpan.FromSeconds(15));
        watcherCompleted.Should().BeTrue("watcher must exit when job returns 404, not retry forever");

        dispatcher.HasActiveSession(agentId).Should().BeFalse("session must be removed after 404");
    }

    // ─── Circuit-based lifecycle: idle-kill tests ─────────────────────────────

    /// <summary>
    /// When the client never sends a keepalive after dispatch, the watcher should
    /// terminate the pod once ChatIdleTimeoutSeconds elapses with no heartbeat.
    /// </summary>
    [Fact]
    public async Task WatcherIdleKill_NoHeartbeatReceived_TerminatesPodAfterIdleTimeout()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();
        string? createdJobName = null;

        // ReadJobAsync always returns non-terminal — pod won't exit on its own
        jobClientMock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Status = new V1JobStatus { Conditions = [] } });

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJobName = j.Metadata.Name;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, createdJobName!, dispatchId, "conn-idle");
            })
            .Returns(Task.CompletedTask);

        // Very short idle timeout so the test completes fast
        var options = CreateOptions(connectTimeoutSeconds: 5, gracePeriod: 1);
        options.ChatIdleTimeoutSeconds = 2; // 2s without a heartbeat → kill

        var dispatcher = CreateDispatcher(
            jobClient: jobClientMock.Object,
            registry: registry,
            options: options,
            redis: new CodingAgentWebUI.TestUtilities.FakeRedisStore());

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        // No heartbeat sent — watcher should auto-terminate after ChatIdleTimeoutSeconds

        var watcherDone = await dispatcher.WaitForWatcherAsync(createdJobName!, TimeSpan.FromSeconds(15));
        watcherDone.Should().BeTrue("watcher must exit after idle timeout expires");

        // Pod must be terminated (CancelChat + force-delete path)
        jobClientMock.Verify(c => c.DeleteJobAsync(
            It.Is<string>(n => n == createdJobName),
            TestNamespace,
            It.IsAny<CancellationToken>()),
            Times.Once,
            "pod must be force-deleted when no client heartbeat arrives within ChatIdleTimeoutSeconds");
    }

    /// <summary>
    /// When the client sends regular keepalive heartbeats, the pod must stay alive
    /// beyond ChatIdleTimeoutSeconds.
    /// </summary>
    [Fact]
    public async Task WatcherIdleKill_HeartbeatReceived_PodRemainsAlive()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();
        string? createdJobName = null;

        // ReadJobAsync always returns non-terminal — pod won't exit on its own
        jobClientMock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Status = new V1JobStatus { Conditions = [] } });

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJobName = j.Metadata.Name;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, createdJobName!, dispatchId, "conn-alive");
            })
            .Returns(Task.CompletedTask);

        // Idle timeout of 2s; we'll heartbeat every ~500ms for 3s then cancel
        var options = CreateOptions(connectTimeoutSeconds: 5, gracePeriod: 1);
        options.ChatIdleTimeoutSeconds = 2;

        var fakeRedis = new CodingAgentWebUI.TestUtilities.FakeRedisStore();
        var dispatcher = CreateDispatcher(
            jobClient: jobClientMock.Object,
            registry: registry,
            options: options,
            redis: fakeRedis);

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        // Send heartbeats every 500ms for 3 seconds — pod should NOT be killed
        using var heartbeatCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _ = Task.Run(async () =>
        {
            while (!heartbeatCts.IsCancellationRequested)
            {
                dispatcher.RecordClientHeartbeat(createdJobName!);
                await Task.Delay(500, heartbeatCts.Token).ContinueWith(_ => { });
            }
        }, CancellationToken.None);

        // Wait the full 3s — watcher should NOT have finished (pod still alive)
        var watcherDone = await dispatcher.WaitForWatcherAsync(createdJobName!, TimeSpan.FromSeconds(3));
        watcherDone.Should().BeFalse("watcher must NOT exit while client heartbeats are arriving");

        // Now stop heartbeats and wait for idle kill to fire
        heartbeatCts.Cancel();
        var idleKillDone = await dispatcher.WaitForWatcherAsync(createdJobName!, TimeSpan.FromSeconds(10));
        idleKillDone.Should().BeTrue("watcher must exit after heartbeats stop and idle timeout fires");

        jobClientMock.Verify(c => c.DeleteJobAsync(
            It.Is<string>(n => n == createdJobName),
            TestNamespace,
            It.IsAny<CancellationToken>()),
            Times.Once,
            "pod must be force-deleted after heartbeats stop");
    }

    /// <summary>
    /// Cross-replica scenario: keepalive arrives on a different replica than the one that owns
    /// the watcher. The "remote" replica writes to the shared Redis store but has no
    /// WatcherEntry for the agentId (so local ticks on the watcher replica are never updated).
    /// The watcher must read the Redis heartbeat and keep the pod alive.
    ///
    /// Design:
    ///  - Dispatcher A owns the watcher (dispatched the pod, has a WatcherEntry).
    ///  - Dispatcher B shares the same FakeRedisStore but has no WatcherEntry for the agentId.
    ///  - After dispatch, A's local LastClientHeartbeatTicks = StartedAt (immediately stale).
    ///  - B calls RecordClientHeartbeat → updates Redis only (no WatcherEntry on B → local no-op).
    ///  - A's watcher checks Redis (fresh) → pod must stay alive despite stale local ticks.
    /// </summary>
    [Fact]
    public async Task WatcherIdleKill_CrossReplica_HeartbeatOnRemoteReplica_PodRemainsAlive()
    {
        var jobClientMock = CreateJobClientMock();
        var registryA = CreateRegistry();
        string? createdJobName = null;

        // ReadJobAsync always returns non-terminal — pod won't exit on its own
        jobClientMock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Status = new V1JobStatus { Conditions = [] } });

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                createdJobName = j.Metadata.Name;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registryA, createdJobName!, dispatchId, "conn-replica-a");
            })
            .Returns(Task.CompletedTask);

        var options = CreateOptions(connectTimeoutSeconds: 5, gracePeriod: 1);
        options.ChatIdleTimeoutSeconds = 2;

        // Shared Redis store — simulates the shared Redis instance in a multi-replica deployment
        var sharedRedis = new CodingAgentWebUI.TestUtilities.FakeRedisStore();

        // Dispatcher A — owns the watcher, uses shared Redis
        var dispatcherA = CreateDispatcher(
            jobClient: jobClientMock.Object,
            registry: registryA,
            options: options,
            redis: sharedRedis);

        // Dispatcher B — "remote replica": separate registry (no WatcherEntry), same Redis
        var registryB = CreateRegistry(); // no agents registered on B
        var dispatcherB = CreateDispatcher(
            jobClient: CreateJobClientMock().Object,
            registry: registryB,
            options: options,
            redis: sharedRedis);

        await dispatcherA.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        // Send heartbeats via dispatcher B every 500ms for 3s.
        // B has no WatcherEntry → local ticks on A are never updated by B.
        // B writes only to sharedRedis → A's watcher must read Redis to see the heartbeat.
        using var heartbeatCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _ = Task.Run(async () =>
        {
            while (!heartbeatCts.IsCancellationRequested)
            {
                dispatcherB.RecordClientHeartbeat(createdJobName!);
                await Task.Delay(500, heartbeatCts.Token).ContinueWith(_ => { });
            }
        }, CancellationToken.None);

        // Watcher on A must NOT idle-kill while B's heartbeats are arriving via Redis
        var killedEarly = await dispatcherA.WaitForWatcherAsync(createdJobName!, TimeSpan.FromSeconds(3));
        killedEarly.Should().BeFalse(
            "watcher must NOT idle-kill the pod while a remote replica is sending heartbeats via Redis");

        // Stop B's heartbeats; A's watcher should now detect idle and terminate
        heartbeatCts.Cancel();
        var idleKillDone = await dispatcherA.WaitForWatcherAsync(createdJobName!, TimeSpan.FromSeconds(10));
        idleKillDone.Should().BeTrue("watcher must idle-kill the pod after cross-replica heartbeats stop");

        jobClientMock.Verify(c => c.DeleteJobAsync(
            It.Is<string>(n => n == createdJobName),
            TestNamespace,
            It.IsAny<CancellationToken>()),
            Times.Once,
            "pod must be force-deleted when cross-replica heartbeats stop");
    }
}