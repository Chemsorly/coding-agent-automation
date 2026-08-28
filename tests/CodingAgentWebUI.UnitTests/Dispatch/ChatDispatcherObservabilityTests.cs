using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Observability tests for <see cref="ChatJobDispatcher"/>:
/// structured log events, OpenTelemetry metrics, and distributed trace spans.
/// Requirements: Req 18.
/// </summary>
/// <remarks>
/// Uses [Collection("SerilogLoggerTests")] for log-capture tests to serialize
/// global Log.Logger mutations (same pattern as JobSpecBuilderLoggingTests).
/// Metric and tracing tests run in parallel without logger mutation.
/// </remarks>
[Collection("ActivityListenerTests")]
public class ChatDispatcherObservabilityTests : IDisposable
{
    private const string TestNamespace = "coding-agent";
    private const string TestSelector = "kiro,dotnet";

    private readonly ActivityListener _activityListener;
    private readonly ConcurrentBag<Activity> _capturedActivities = [];

    public ChatDispatcherObservabilityTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _capturedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _activityListener.Dispose();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static DispatchServiceOptions CreateOptions(int connectTimeoutSeconds = 5) => new()
    {
        Namespace = TestNamespace,
        KiroPvcPool = ["pvc-0"],
        OrchestratorUrl = "http://orchestrator:8080",
        AgentApiKeySecretName = "caa-secret",
        AgentServiceAccountName = "caa-agent",
        ChatPodConnectTimeoutSeconds = connectTimeoutSeconds,
        AgentJobTimeoutSeconds = 7200,
        ChatTerminationGracePeriodSeconds = 120
    };

    private static AgentRegistryService CreateRegistry() =>
        new(Mock.Of<ILogger>());

    private static Mock<IKubernetesJobClient> CreateJobClientMock()
    {
        var mock = new Mock<IKubernetesJobClient>();
        mock.Setup(c => c.ListJobsAsync(TestNamespace, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });
        mock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(c => c.DeleteJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
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

    private static string RegisterChatAgent(
        AgentRegistryService registry, string agentId, string dispatchId, string connectionId = "conn-1")
    {
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "test-host",
            Labels = [$"chat=true", $"chat-session-id={dispatchId}"]
        }, connectionId);
        return agentId;
    }

    private static ChatJobDispatcher CreateDispatcher(
        IKubernetesJobClient? jobClient = null,
        AgentRegistryService? registry = null,
        IHubContext<AgentHub, IAgentHubClient>? hubContext = null,
        DispatchServiceOptions? options = null,
        JobTemplateStore? templateStore = null)
    {
        if (templateStore is null)
        {
            var yaml = """
                - labels: "dotnet,kiro"
                  image: "chemsorly/coding-agent:kiro-dotnet10"
                  providerType: "kiro"
                  maxConcurrent: 2
                """;
            templateStore = JobTemplateStore.LoadFromYaml(yaml);
        }

        return new ChatJobDispatcher(
            jobClient ?? CreateJobClientMock().Object,
            hubContext ?? CreateHubContextMock().Object,
            templateStore,
            registry ?? CreateRegistry(),
            options ?? CreateOptions(),
            Mock.Of<ILogger>());
    }

    // ─── ChatTelemetry static properties exist ────────────────────────────────

    [Fact]
    public void ChatTelemetry_DispatchLatency_IsNotNull()
    {
        ChatTelemetry.DispatchLatency.Should().NotBeNull();
        ChatTelemetry.DispatchLatency.Name.Should().Be("workdistribution.chat.dispatch_latency_seconds");
    }

    [Fact]
    public void ChatTelemetry_SessionsActive_IsNotNull()
    {
        ChatTelemetry.SessionsActive.Should().NotBeNull();
        ChatTelemetry.SessionsActive.Name.Should().Be("workdistribution.chat.sessions_active");
    }

    [Fact]
    public void ChatTelemetry_SessionDuration_IsNotNull()
    {
        ChatTelemetry.SessionDuration.Should().NotBeNull();
        ChatTelemetry.SessionDuration.Name.Should().Be("workdistribution.chat.session_duration_seconds");
    }

    [Fact]
    public void ChatTelemetry_PodConnectTimeouts_IsNotNull()
    {
        ChatTelemetry.PodConnectTimeouts.Should().NotBeNull();
        ChatTelemetry.PodConnectTimeouts.Name.Should().Be("workdistribution.chat.pod_connect_timeouts");
    }

    [Fact]
    public void ChatTelemetry_PodForceTerminations_IsNotNull()
    {
        ChatTelemetry.PodForceTerminations.Should().NotBeNull();
        ChatTelemetry.PodForceTerminations.Name.Should().Be("workdistribution.chat.pod_force_terminations");
    }

    [Fact]
    public void ChatTelemetry_PvcUtilization_IsNotNull()
    {
        ChatTelemetry.PvcUtilization.Should().NotBeNull();
        ChatTelemetry.PvcUtilization.Name.Should().Be("workdistribution.chat.pvc_utilization");
    }

    // ─── Tracing: Chat.Dispatch span on success ───────────────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_Success_CreatesDispatchActivity_WithRequiredTags()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-trace-1", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        await dispatcher.DispatchChatPodAsync(TestSelector, "claude-opus-4.8", "high", CancellationToken.None);

        var dispatchActivity = _capturedActivities
            .FirstOrDefault(a => a.OperationName == "Chat.Dispatch");

        dispatchActivity.Should().NotBeNull("Chat.Dispatch span must be created on success");
        dispatchActivity!.GetTagItem("agent_selector").Should().NotBeNull();
        dispatchActivity.GetTagItem("dispatch_id").Should().NotBeNull();
        dispatchActivity.GetTagItem("job_name").Should().NotBeNull();
        dispatchActivity.GetTagItem("model")!.ToString().Should().Be("claude-opus-4.8");
        dispatchActivity.GetTagItem("effort")!.ToString().Should().Be("high");
        dispatchActivity.GetTagItem("provider_type")!.ToString().Should().Be("kiro");
    }

    [Fact]
    public async Task DispatchChatPodAsync_Success_DispatchActivity_HasNoErrorStatus()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, "agent-trace-2", dispatchId);
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        var dispatchActivity = _capturedActivities
            .FirstOrDefault(a => a.OperationName == "Chat.Dispatch");

        dispatchActivity.Should().NotBeNull();
        dispatchActivity!.Status.Should().NotBe(ActivityStatusCode.Error);
    }

    // ─── Tracing: Chat.Dispatch span on timeout ───────────────────────────────

    [Fact]
    public async Task DispatchChatPodAsync_Timeout_DispatchActivity_HasErrorStatus()
    {
        var dispatcher = CreateDispatcher(options: CreateOptions(connectTimeoutSeconds: 1));

        await Assert.ThrowsAsync<ChatPodTimeoutException>(
            () => dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None));

        var dispatchActivity = _capturedActivities
            .FirstOrDefault(a => a.OperationName == "Chat.Dispatch");

        dispatchActivity.Should().NotBeNull("Chat.Dispatch span must be created even on timeout");
        dispatchActivity!.Status.Should().Be(ActivityStatusCode.Error,
            "timeout must set ERROR status on the span");
    }

    // ─── Tracing: Chat.Terminate span ────────────────────────────────────────

    [Fact]
    public async Task TerminateChatSessionAsync_CreatesTerminateActivity_WithAgentIdTag()
    {
        var jobClientMock = CreateJobClientMock();
        var registry = CreateRegistry();
        string? capturedJobName = null;

        jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                capturedJobName = j.Metadata.Name;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                RegisterChatAgent(registry, capturedJobName!, dispatchId, "conn-term");
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);
        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        await dispatcher.TerminateChatSessionAsync(capturedJobName!, CancellationToken.None);

        var terminateActivity = _capturedActivities
            .FirstOrDefault(a => a.OperationName == "Chat.Terminate"
                && a.GetTagItem("agent_id")?.ToString() == capturedJobName);

        terminateActivity.Should().NotBeNull($"Chat.Terminate span with agent_id='{capturedJobName}' must be created");
        terminateActivity!.GetTagItem("agent_id")!.ToString().Should().Be(capturedJobName);
        terminateActivity.GetTagItem("job_name").Should().NotBeNull();
    }

    [Fact]
    public async Task TerminateChatSessionAsync_SessionNotFound_CreatesTerminateActivity_NotFoundOutcome()
    {
        var dispatcher = CreateDispatcher();

        await dispatcher.TerminateChatSessionAsync("nonexistent-agent", CancellationToken.None);

        var terminateActivity = _capturedActivities
            .FirstOrDefault(a => a.OperationName == "Chat.Terminate");

        terminateActivity.Should().NotBeNull("Chat.Terminate span must be created even when session not found");
        // When no session is registered, TerminateChatSessionAsync attempts a best-effort direct
        // job delete (agentId == jobName for chat pods) and tags the outcome accordingly.
        terminateActivity!.GetTagItem("outcome")!.ToString().Should().Be("not_found_direct_delete");
    }

    // ─── Logging: DispatchChatPodAsync success ────────────────────────────────
    // NOTE: ChatJobDispatcher uses the constructor-injected ILogger (_logger) for all
    // logging. Tests capture log calls via a tracking sink on a real Serilog ILogger
    // so generic overloads are correctly intercepted.

    public class LoggingTests
    {
        private const string TestNamespace = "coding-agent";
        private const string TestSelector = "kiro,dotnet";

        /// <summary>Minimal Serilog sink that records rendered log messages.</summary>
        private sealed class ListSink(List<(Serilog.Events.LogEventLevel Level, string Message)> events)
            : Serilog.Core.ILogEventSink
        {
            public void Emit(Serilog.Events.LogEvent logEvent)
                => events.Add((logEvent.Level, logEvent.RenderMessage()));
        }

        /// <summary>
        /// Creates a real Serilog ILogger backed by a ListSink so all log calls
        /// (regardless of generic overload) are captured correctly.
        /// </summary>
        private static (Serilog.ILogger logger, List<(Serilog.Events.LogEventLevel Level, string Message)> events)
            CreateCapturingLogger()
        {
            var events = new List<(Serilog.Events.LogEventLevel Level, string Message)>();
            var logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(new ListSink(events))
                .CreateLogger();
            return (logger, events);
        }

        private static DispatchServiceOptions CreateOptions(int connectTimeoutSeconds = 5) => new()
        {
            Namespace = TestNamespace,
            KiroPvcPool = ["pvc-0"],
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "caa-secret",
            AgentServiceAccountName = "caa-agent",
            ChatPodConnectTimeoutSeconds = connectTimeoutSeconds,
            AgentJobTimeoutSeconds = 7200,
            ChatTerminationGracePeriodSeconds = 120
        };

        private static AgentRegistryService CreateRegistry() => new(Mock.Of<ILogger>());

        private static Mock<IKubernetesJobClient> CreateJobClientMock()
        {
            var mock = new Mock<IKubernetesJobClient>();
            mock.Setup(c => c.ListJobsAsync(TestNamespace, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new V1JobList { Items = [] });
            mock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            mock.Setup(c => c.DeleteJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
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

        private static string RegisterChatAgent(
            AgentRegistryService registry, string agentId, string dispatchId, string connectionId = "conn-1")
        {
            registry.Register(new AgentRegistrationMessage
            {
                AgentId = agentId,
                Hostname = "test-host",
                Labels = [$"chat=true", $"chat-session-id={dispatchId}"]
            }, connectionId);
            return agentId;
        }

        private static ChatJobDispatcher CreateDispatcher(
            Serilog.ILogger logger,
            IKubernetesJobClient? jobClient = null,
            AgentRegistryService? registry = null,
            IHubContext<AgentHub, IAgentHubClient>? hubContext = null,
            DispatchServiceOptions? options = null)
        {
            var yaml = """
                - labels: "dotnet,kiro"
                  image: "chemsorly/coding-agent:kiro-dotnet10"
                  providerType: "kiro"
                  maxConcurrent: 2
                """;
            var templateStore = JobTemplateStore.LoadFromYaml(yaml);

            return new ChatJobDispatcher(
                jobClient ?? CreateJobClientMock().Object,
                hubContext ?? CreateHubContextMock().Object,
                templateStore,
                registry ?? CreateRegistry(),
                options ?? CreateOptions(),
                logger);
        }

        // ─── Success path logs Information with required call ─────────────────

        [Fact]
        public async Task DispatchChatPodAsync_Success_LogsInformation_DispatchedChatPod()
        {
            var (logger, events) = CreateCapturingLogger();
            var jobClientMock = CreateJobClientMock();
            var registry = CreateRegistry();

            jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
                .Callback<V1Job, string, CancellationToken>((j, _, _) =>
                {
                    var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                    RegisterChatAgent(registry, "agent-log-1", dispatchId);
                })
                .Returns(Task.CompletedTask);

            var dispatcher = CreateDispatcher(logger, jobClient: jobClientMock.Object, registry: registry);
            await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

            var dispatchLogs = events
                .Where(e => e.Level == Serilog.Events.LogEventLevel.Information &&
                            e.Message.Contains("dispatched chat pod"))
                .ToList();

            dispatchLogs.Should().NotBeEmpty("dispatch success must produce an Information log");
        }

        // ─── Timeout path logs Warning ────────────────────────────────────────

        [Fact]
        public async Task DispatchChatPodAsync_Timeout_LogsWarning()
        {
            var (logger, events) = CreateCapturingLogger();
            var dispatcher = CreateDispatcher(logger, options: CreateOptions(connectTimeoutSeconds: 1));

            await Assert.ThrowsAsync<ChatPodTimeoutException>(
                () => dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None));

            var timeoutLogs = events
                .Where(e => e.Level == Serilog.Events.LogEventLevel.Warning &&
                            e.Message.Contains("did not connect within"))
                .ToList();

            timeoutLogs.Should().NotBeEmpty("timeout must produce a Warning log");
        }

        // ─── TerminateChatSessionAsync force-delete logs Warning ──────────────

        [Fact]
        public async Task TerminateChatSessionAsync_ForceDelete_LogsWarning()
        {
            var (logger, events) = CreateCapturingLogger();
            var jobClientMock = CreateJobClientMock();
            var registry = CreateRegistry();
            string? capturedJobName = null;

            jobClientMock.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
                .Callback<V1Job, string, CancellationToken>((j, _, _) =>
                {
                    capturedJobName = j.Metadata.Name;
                    var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                    RegisterChatAgent(registry, capturedJobName!, dispatchId, "conn-force");
                })
                .Returns(Task.CompletedTask);

            // ReadJobAsync never returns terminal — watcher keeps running
            jobClientMock.Setup(c => c.ReadJobAsync(It.IsAny<string>(), TestNamespace, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new V1Job { Status = new V1JobStatus { Conditions = [] } });

            var dispatcher = CreateDispatcher(
                logger,
                jobClient: jobClientMock.Object,
                registry: registry,
                options: CreateOptions(connectTimeoutSeconds: 5));

            await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

            // Pre-cancelled token causes WatcherTask.WaitAsync to throw immediately → force-delete path
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await dispatcher.TerminateChatSessionAsync(capturedJobName!, cts.Token);

            var forceDeleteLogs = events
                .Where(e => e.Level == Serilog.Events.LogEventLevel.Warning &&
                            e.Message.Contains("grace period expired"))
                .ToList();

            forceDeleteLogs.Should().NotBeEmpty("force-delete path must produce a Warning log");
        }

        // ─── StartAsync is now a no-op (RecoverSessionsAsync removed in Spec 049) ────

        [Fact]
        public async Task StartAsync_IsNoOp_DoesNotCallK8s()
        {
            var (logger, _) = CreateCapturingLogger();
            var jobClientMock = CreateJobClientMock();

            var dispatcher = CreateDispatcher(logger, jobClient: jobClientMock.Object);

            var act = () => dispatcher.StartAsync(CancellationToken.None);
            await act.Should().NotThrowAsync("StartAsync must be a no-op");

            // Must NOT call ListJobsAsync — session recovery was removed
            jobClientMock.Verify(
                c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
    }
}
