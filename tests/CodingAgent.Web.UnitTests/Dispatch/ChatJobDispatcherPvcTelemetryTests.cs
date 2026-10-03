using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using k8s.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Dispatch;

/// <summary>
/// MeterListener-based unit tests asserting that <see cref="ChatJobDispatcher"/> emits
/// <c>workdistribution.chat.pvc_utilization</c> measurements with the correct <c>pool</c>
/// tag derived from the claimed PVC's provider type.
///
/// Requirements: AC from issue #3285 — no hardcoded "kiro" literal in PvcUtilization tags.
/// </summary>
/// <remarks>
/// Must be in [Collection("Metrics")] because <see cref="ChatTelemetry.PvcUtilization"/> is a
/// static <see cref="UpDownCounter{T}"/> on the process-global
/// <see cref="WorkDistributionTelemetry.Meter"/>. Any <see cref="MeterListener"/> subscribed to
/// that meter observes Add() calls from ALL concurrent test threads. [Collection("Metrics")]
/// serializes this class against other MeterListener-based tests to prevent measurement leakage.
/// </remarks>
[Collection("Metrics")]
public sealed class ChatJobDispatcherPvcTelemetryTests
{
    private const string TestNamespace = "coding-agent";
    private const string TestSelector = "kiro,dotnet";

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static DispatchServiceOptions CreateOptions() => new()
    {
        Namespace = TestNamespace,
        KiroPvcPool = ["pvc-0"],
        OrchestratorUrl = "http://orchestrator:8080",
        AgentApiKeySecretName = "caa-secret",
        AgentApiKeyValue = "test-master-key",
        AgentServiceAccountName = "caa-agent",
        ChatPodConnectTimeoutSeconds = 5,
        ChatJobMaxDurationSeconds = 7200,
        ChatTerminationGracePeriodSeconds = 1
    };

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

    private static ChatJobDispatcher CreateDispatcher(
        IKubernetesJobClient? jobClient = null,
        IAgentRegistryService? registry = null,
        JobTemplateStore? templateStore = null)
    {
        templateStore ??= JobTemplateStore.LoadFromYaml("""
            - labels: "dotnet,kiro"
              image: "chemsorly/coding-agent:kiro-dotnet10"
              providerType: "kiro"
              maxConcurrent: 2
            """);

        return new ChatJobDispatcher(
            jobClient ?? CreateJobClientMock().Object,
            CreateHubContextMock().Object,
            templateStore,
            registry ?? new AgentRegistryService(Mock.Of<ILogger>()),
            CreateOptions(),
            Mock.Of<ILogger>());
    }

    /// <summary>
    /// Creates a <see cref="MeterListener"/> that captures (value, pool-tag) pairs from
    /// <c>workdistribution.chat.pvc_utilization</c> on the static
    /// <see cref="WorkDistributionTelemetry.Meter"/>.
    ///
    /// Caller disposes the listener (use <c>using</c>).
    /// <see cref="MeterListener.Start"/> is called inside this helper — invoke it BEFORE the Act.
    /// </summary>
    private static (MeterListener Listener, ConcurrentBag<(long Value, string Pool)> Measurements)
        CreatePvcUtilizationListener()
    {
        var measurements = new ConcurrentBag<(long Value, string Pool)>();
        var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            // Use string literals to avoid static type initialization reentrancy:
            // this callback fires during MeterListener.Start() which may be called while
            // WorkDistributionTelemetry's .cctor() is still running.
            if (instrument.Meter.Name == "CodingAgent.WorkDistribution"
                && instrument.Name == "workdistribution.chat.pvc_utilization")
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string pool = "";
            foreach (var tag in tags)
                if (tag.Key == "pool") { pool = tag.Value?.ToString() ?? ""; break; }
            measurements.Add((value, pool));
        });

        // Start() MUST remain before the Act: the retroactive InstrumentPublished callback fires
        // for already-registered static instruments, enabling measurement capture.
        listener.Start();

        return (listener, measurements);
    }

    // ─── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When RegisterWatcher is called for a Kiro agent that holds a claimed PVC,
    /// it emits PvcUtilization +1 with pool="kiro" (derived from PoolName, not hardcoded).
    ///
    /// RegisterWatcher is private; we exercise it through DispatchChatPodAsync. The
    /// MeterListener is started before dispatch so the +1 increment emitted inside
    /// RegisterWatcher is captured and asserted directly.
    /// </summary>
    [Fact]
    public async Task RegisterWatcher_KiroAgent_EmitsPvcUtilizationIncrement_WithPoolTagKiro()
    {
        // Arrange — wire up a job client that registers a chat agent on CreateJobAsync,
        // so that PollForAgentConnectionAsync finds the agent and calls RegisterWatcher.
        var jobClientMock = CreateJobClientMock();
        var registry = new AgentRegistryService(Mock.Of<ILogger>());

        jobClientMock
            .Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                var msg = new AgentRegistrationMessage
                {
                    AgentId = j.Metadata.Name,
                    Hostname = "test-host",
                    Labels = ["chat=true", $"chat-session-id={dispatchId}"]
                };
                registry.Register(msg, "conn-increment-tag");
            })
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(jobClient: jobClientMock.Object, registry: registry);

        // Listener BEFORE Act — must be started before DispatchChatPodAsync so the
        // RegisterWatcher +1 increment is captured.
        var (listener, measurements) = CreatePvcUtilizationListener();
        using var _ = listener;

        // Act — DispatchChatPodAsync → PollForAgentConnectionAsync → RegisterWatcher,
        // which emits PvcUtilization +1 with pool derived from template.ProviderType ("kiro").
        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        // Assert: the increment site emitted pool="kiro", not a hardcoded literal.
        measurements.Should().Contain(m => m.Value == 1L && m.Pool == "kiro",
            "RegisterWatcher must emit PvcUtilization +1 with pool='kiro' for a Kiro agent with a claimed PVC");
    }

    /// <summary>
    /// CleanupSession emits PvcUtilization -1 with pool="kiro" for a Kiro agent with a
    /// claimed PVC.
    /// </summary>
    // TODO [WARNING]: This test is substantially duplicate of RegisterWatcher_KiroAgent_EmitsPvcUtilizationIncrement_WithPoolTagKiro:
    // both construct the same Kiro WatcherEntry and call CleanupSession with the same assertions.
    // Neither provides additional coverage over the other. Consider merging the two or removing
    // one to reduce test-suite noise and maintenance burden.
    [Fact]
    public void CleanupSession_KiroAgent_EmitsPvcUtilizationDecrement_WithPoolTagKiro()
    {
        // Arrange
        var dispatcher = CreateDispatcher();

        var (listener, measurements) = CreatePvcUtilizationListener();
        using var _ = listener;

        var agentKey = new AgentId("agent-pvc-kiro-cleanup");
        using var cts = new CancellationTokenSource();
        var identity = new ChatJobDispatcher.WatcherIdentity(
            agentKey, "agent-pvc-kiro-cleanup", "dotnet,kiro", "pvc-0", "kiro");
        var entry = new ChatJobDispatcher.WatcherEntry(identity, DateTimeOffset.UtcNow, cts);

        // Act — CAS gate on entry.Cleaned (0→1); freshly constructed entries start at 0.
        dispatcher.CleanupSession(agentKey, entry, "dotnet_kiro", "completed");

        // Assert
        measurements.Should().Contain(m => m.Value == -1L && m.Pool == "kiro",
            "CleanupSession must emit PvcUtilization -1 with pool='kiro' for a Kiro agent");
    }

    /// <summary>
    /// Acceptance-criteria test (issue #3285 AC3):
    /// CleanupSession emits PvcUtilization -1 with pool="opencode" for a non-Kiro entry —
    /// the pool tag must be derived from entry.PoolName, not from a hardcoded literal.
    ///
    /// Note: today ClaimPvcForKiroAgent returns null for non-Kiro providers, so opencode
    /// agents never acquire a PVC through the normal dispatch path. This test constructs a
    /// synthetic WatcherEntry (ClaimedPvc != null, PoolName = "opencode") to verify that the
    /// pool-tag emission path correctly uses PoolName rather than a hardcoded "kiro" string.
    /// </summary>
    [Fact]
    public void CleanupSession_NonKiroEntry_EmitsPvcUtilizationDecrement_WithCorrectPoolTag()
    {
        // Arrange
        var dispatcher = CreateDispatcher();

        // Listener BEFORE Act.
        var (listener, measurements) = CreatePvcUtilizationListener();
        using var _ = listener;

        var agentKey = new AgentId("agent-pvc-opencode-test");
        using var cts = new CancellationTokenSource();
        // PoolName = "opencode" is the non-Kiro pool tag under test.
        // ClaimedPvc is non-null to satisfy the if (entry.ClaimedPvc is not null) guard.
        var identity = new ChatJobDispatcher.WatcherIdentity(
            agentKey, "agent-pvc-opencode-test", "dotnet,opencode", "pvc-0", "opencode");
        var entry = new ChatJobDispatcher.WatcherEntry(identity, DateTimeOffset.UtcNow, cts);

        // Act — CleanupSession uses CAS on entry.Cleaned (0→1); freshly constructed entries start at 0.
        dispatcher.CleanupSession(agentKey, entry, "dotnet_opencode", "completed");

        // Assert: pool tag must be "opencode", not "kiro"
        measurements.Should().Contain(m => m.Value == -1L && m.Pool == "opencode",
            "CleanupSession must emit PvcUtilization -1 with pool='opencode' for a non-Kiro entry — " +
            "pool tag must be derived from entry.PoolName, not a hardcoded 'kiro' literal");
        measurements.Should().NotContain(m => m.Pool == "kiro",
            "a non-Kiro entry with PoolName='opencode' must never emit pool='kiro'");
    }

    /// <summary>
    /// ForceDeleteAndCleanupAsync emits PvcUtilization -1 with pool="kiro" for a Kiro agent
    /// with a claimed PVC.
    ///
    /// The method is private; we exercise it directly by constructing the WatcherEntry and
    /// calling the equivalent inline cleanup that ForceDeleteAndCleanupAsync performs after
    /// taking the Cleaned CAS. We verify that the pool tag reading path from entry.PoolName
    /// produces the correct value.
    /// </summary>
    [Fact]
    public async Task ForceDeleteAndCleanupAsync_KiroAgent_EmitsPvcUtilizationDecrement_WithPoolTagKiro()
    {
        // Arrange — use a stalled watcher so ForceDeleteAndCleanupAsync wins the cleanup race.
        var jobClientMock = CreateJobClientMock();
        var registryMock = new Mock<IAgentRegistryService>();
        string? capturedJobName = null;

        var stalledWatcher = new Mock<IChatSessionWatcher>();
        stalledWatcher
            .Setup(w => w.WatchJobUntilTerminalAsync(
                It.IsAny<string>(),
                It.IsAny<ChatJobDispatcher.WatcherEntry>(),
                It.IsAny<Func<AgentId, CancellationToken, Task>>(),
                It.IsAny<Action<AgentId, ChatJobDispatcher.WatcherEntry, string, string>>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, ChatJobDispatcher.WatcherEntry,
                     Func<AgentId, CancellationToken, Task>,
                     Action<AgentId, ChatJobDispatcher.WatcherEntry, string, string>,
                     CancellationToken>(
                (_, _, _, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct)
                    .ContinueWith(_ => Task.CompletedTask, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnCanceled, TaskScheduler.Default)
                    .Unwrap());

        jobClientMock
            .Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), TestNamespace, It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((j, _, _) =>
            {
                capturedJobName = j.Metadata.Name;
                var dispatchId = j.Metadata.Labels.TryGetValue("caa/chat-session-id", out var did) ? did : "";
                var agentEntry = new AgentEntry
                {
                    AgentId = capturedJobName!,
                    ConnectionId = "conn-forcedel",
                    Hostname = "test-host",
                    Labels = [$"chat=true", $"chat-session-id={dispatchId}"],
                    Status = AgentStatus.Idle,
                    RegisteredAt = DateTimeOffset.UtcNow
                };
                registryMock.Setup(r => r.GetAgentsByLabel("chat-session-id", dispatchId))
                    .Returns(new List<AgentEntry> { agentEntry });
                registryMock.Setup(r => r.GetByAgentIdAsync(It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(agentEntry);
                registryMock.Setup(r => r.Deregister(It.IsAny<AgentId>())).Returns(true);
            })
            .Returns(Task.CompletedTask);

        var options = new DispatchServiceOptions
        {
            Namespace = TestNamespace,
            KiroPvcPool = ["pvc-0"],
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "caa-secret",
            AgentApiKeyValue = "test-master-key",
            AgentServiceAccountName = "caa-agent",
            ChatPodConnectTimeoutSeconds = 5,
            ChatJobMaxDurationSeconds = 7200,
            ChatTerminationGracePeriodSeconds = 1
        };

        var dispatcher = new ChatJobDispatcher(
            jobClientMock.Object,
            CreateHubContextMock().Object,
            JobTemplateStore.LoadFromYaml("""
                - labels: "dotnet,kiro"
                  image: "chemsorly/coding-agent:kiro-dotnet10"
                  providerType: "kiro"
                  maxConcurrent: 2
                """),
            registryMock.Object,
            options,
            Mock.Of<ILogger>(),
            heartbeatTracker: null,
            sessionWatcher: stalledWatcher.Object);

        // Listener BEFORE DispatchChatPodAsync: captures both the RegisterWatcher +1 increment
        // (emitted during dispatch) and the ForceDeleteAndCleanupAsync -1 decrement (emitted
        // after termination). Starting after DispatchChatPodAsync would miss the +1.
        var (listener, measurements) = CreatePvcUtilizationListener();
        using var _ = listener;

        await dispatcher.DispatchChatPodAsync(TestSelector, null, null, CancellationToken.None);

        // Assert the RegisterWatcher +1 increment was captured with the correct pool tag.
        measurements.Should().Contain(m => m.Value == 1L && m.Pool == "kiro",
            "RegisterWatcher must emit PvcUtilization +1 with pool='kiro' for a Kiro agent");

        // Act — cancel immediately so TerminateChatSessionAsync drives ForceDeleteAndCleanupAsync.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        try { await dispatcher.TerminateChatSessionAsync(capturedJobName!, cts.Token); }
        catch (OperationCanceledException) { /* expected */ }

        // TODO [WARNING]: Task.Delay(500) is a fixed timing barrier that may be insufficient
        // on a heavily loaded CI runner, making this assertion non-deterministically flaky.
        // ForceDeleteAndCleanupAsync runs on a Task.Run continuation; if that continuation
        // has not emitted its PvcUtilization measurement within 500 ms, the assertion below
        // will fail spuriously. Prefer a polling loop with a timeout (e.g. polling measurements
        // every 10 ms up to 5 s) or inject a completion callback to get a deterministic signal.
        // ForceDeleteAndCleanupAsync runs on a Task.Run continuation; allow it to settle.
        await Task.Delay(500);

        // Assert the ForceDeleteAndCleanupAsync -1 decrement was captured with the correct pool tag.
        measurements.Should().Contain(m => m.Value == -1L && m.Pool == "kiro",
            "ForceDeleteAndCleanupAsync must emit PvcUtilization -1 with pool='kiro' for a Kiro agent");
    }
}
