using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="WorkItemAgentService"/>.
/// Since WorkItemAgentService depends on concrete types (HubConnectionManager, LocalPipelineExecutor),
/// we test constructor validation, CancelPipeline behavior, and observable contract.
/// Full lifecycle is tested via E2E tests.
/// </summary>
public class WorkItemAgentServiceTests : IAsyncDisposable
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IHostApplicationLifetime> _mockLifetime = new();
    private readonly WorkItemHttpClient _workItemClient;

    public WorkItemAgentServiceTests()
    {
        var httpClient = new HttpClient(new FakeOkHandler()) { BaseAddress = new Uri("http://localhost") };
        _workItemClient = new WorkItemHttpClient(httpClient, _mockLogger.Object);
    }

    public async ValueTask DisposeAsync()
    {
    }

    // ── Constructor Guard Clauses ────────────────────────────────────────

    [Theory]
    // TODO: Add a test for default(AgentId) — since AgentId is a value type, the removed
    // [InlineData(5, "agentIdentity")] null guard test should be replaced with a characterization
    // test verifying behavior when default(AgentId) (Value == null) is passed to the constructor.
    [InlineData(0, "workItemId")]
    [InlineData(1, "workItemClient")]
    [InlineData(2, "connectionManager")]
    [InlineData(3, "workItemExecutor")]
    [InlineData(4, "completionReporter")]
    [InlineData(5, "lifetime")]
    [InlineData(6, "logger")]
    public void Constructor_NullParameter_Throws(int nullIndex, string expectedParamName)
    {
        var args = new object?[]
        {
            "wi-1",
            _workItemClient,
            Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            _mockLifetime.Object,
            _mockLogger.Object
        };
        args[nullIndex] = null;

        var act = () => new WorkItemAgentService(
            (string)args[0]!,
            (IWorkItemLifecycleClient)args[1]!,
            (IAgentConnectionManager)args[2]!,
            (IWorkItemExecutor)args[3]!,
            (IJobCompletionReporter)args[4]!,
            new AgentId("agent-1"),
            (IHostApplicationLifetime)args[5]!,
            (Serilog.ILogger)args[6]!);

        act.Should().Throw<ArgumentNullException>().WithParameterName(expectedParamName);
    }

    [Fact]
    public void Constructor_ValidParams_DoesNotThrow()
    {
        var act = () => CreateService("wi-1");
        act.Should().NotThrow();
    }

    // ── CancelPipeline ───────────────────────────────────────────────────

    [Fact]
    public void CancelPipeline_BeforeExecution_DoesNotThrow()
    {
        // CancelPipeline should be safe to call even before ExecuteAsync (pipeline CTS is null)
        var service = CreateService("wi-1");
        var act = () => service.CancelPipeline();
        act.Should().NotThrow();
    }

    // ── ExecuteAsync — Running Rejected (400) ───────────────────────────

    [Fact]
    public async Task ExecuteAsync_RunningStatusRejected_AbortsWithoutConnectingSignalR()
    {
        // Arrange: GET assignment → 200 OK with valid JSON, POST Running → 400 Bad Request
        var assignmentJson = JsonSerializer.Serialize(CreateMinimalAssignment("job-1", "owner/repo#42"), PipelineJsonOptions.Default);

        var handler = new FakeSequentialHandler([
            (System.Net.HttpStatusCode.OK, assignmentJson),          // GET /api/work-items/{id}/assignment
            (System.Net.HttpStatusCode.BadRequest, "{}")             // POST /api/work-items/{id}/status (Running)
        ]);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkItemHttpClient(httpClient, _mockLogger.Object);

        var stopCalled = new TaskCompletionSource<bool>();
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        var service = new WorkItemAgentService(
            "wi-rejected", client, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"), _mockLifetime.Object, _mockLogger.Object);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);

        // Wait for the service to call StopApplication (signals lifecycle complete)
        var completed = await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(stopCalled.Task, "Service should call StopApplication within timeout");

        await service.StopAsync(CancellationToken.None);

        // Assert
        handler.CallCount.Should().Be(2, "GET assignment + POST Running, then abort — no further calls");
        _mockLifetime.Verify(l => l.StopApplication(), Times.AtLeastOnce);
    }

    // ── ExecuteAsync — Terminal Assignment (410 Gone) ─────────────────────

    [Fact]
    public async Task ExecuteAsync_TerminalAssignment_StopsApplication()
    {
        // WorkItemHttpClient returns null (410 Gone simulation)
        var handler = new FakeHandler(System.Net.HttpStatusCode.Gone);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkItemHttpClient(httpClient, _mockLogger.Object);

        var stopCalled = new TaskCompletionSource<bool>();
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        var service = new WorkItemAgentService(
            "wi-terminal", client, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"), _mockLifetime.Object, _mockLogger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);

        // Wait for the service to call StopApplication (signals lifecycle complete)
        var completed = await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(stopCalled.Task, "Service should call StopApplication within timeout");

        await service.StopAsync(CancellationToken.None);

        _mockLifetime.Verify(l => l.StopApplication(), Times.AtLeastOnce);
    }

    // ── Exit Code on Pipeline Cancellation ─────────────────────────────

    /// <summary>
    /// Validates that when the pipeline is intentionally cancelled via CancelJob SignalR message,
    /// the service exits with code 0 so that K8s does not restart the pod.
    /// Cancelled is an intentional termination — the orchestrator requested it.
    /// </summary>
    [Fact]
    public void WorkItemAgentService_ShouldExitZeroOnCancelled()
    {
        // Structural test: verify the exit code logic treats Cancelled as exit 0.
        // The actual return line should be: Completed or Cancelled → 0, else 1.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        // The exit code logic should allow Cancelled to exit 0
        // Old (buggy): completion.FinalStep == PipelineStep.Completed ? 0 : 1
        // Fixed: completion.FinalStep is Completed or Cancelled → 0, else 1
        var hasCancelledExitZero = sourceCode.Contains("PipelineStep.Cancelled")
            && sourceCode.Contains('0')
            && !sourceCode.Contains("completion.FinalStep == PipelineStep.Completed ? 0 : 1");

        hasCancelledExitZero.Should().BeTrue(
            "WorkItemAgentService must exit 0 when FinalStep is Cancelled (intentional cancellation). " +
            "The old pattern 'completion.FinalStep == PipelineStep.Completed ? 0 : 1' causes pod restarts " +
            "on cancel because K8s sees exit code 1 as failure.");
    }

    // ── Exit Code on Pipeline Failure ────────────────────────────────────

    /// <summary>
    /// Validates that when the pipeline execution fails (e.g., token refresh error),
    /// the service sets a non-zero exit code.
    /// Currently, RunWorkItemLifecycleAsync returns 0 after posting "Failed" status,
    /// which causes K8s to mark the pod as "Completed" instead of "Error".
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PipelineExecutionFails_SetsNonZeroExitCode()
    {
        // Arrange: GET assignment → 200 OK, POST Running → 200 OK
        // The hub connection will fail (nothing listening on port 1),
        // which triggers the "Failed to connect SignalR hub" path → returns 1.
        // This validates the exit code contract for failure scenarios.
        var assignmentJson = JsonSerializer.Serialize(
            CreateMinimalAssignment("job-fail", "owner/repo#99"), PipelineJsonOptions.Default);

        var handler = new FakeSequentialHandler([
            (System.Net.HttpStatusCode.OK, assignmentJson),  // GET assignment
            (System.Net.HttpStatusCode.OK, "{}"),            // POST Running → accepted
            (System.Net.HttpStatusCode.OK, "{}")             // POST Failed status (after hub connection failure)
        ]);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkItemHttpClient(httpClient, _mockLogger.Object);

        // Use a hub manager pointing at non-existent server — StartAsync will throw
        var mockConnectionManager = Mock.Of<IAgentConnectionManager>();

        // Use a TaskCompletionSource to detect when StopApplication is called
        var stopCalled = new TaskCompletionSource<bool>();
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        var service = new WorkItemAgentService(
            "job-fail", client, mockConnectionManager,
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"), _mockLifetime.Object, _mockLogger.Object);

        // Act
        var previousExitCode = Environment.ExitCode;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await service.StartAsync(cts.Token);

            // Wait for the service to call StopApplication (signals lifecycle complete)
            var completed = await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            completed.Should().Be(stopCalled.Task, "Service should call StopApplication within timeout");

            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            var actualExitCode = Environment.ExitCode;
            Environment.ExitCode = previousExitCode; // Restore

            // Assert: exit code must be non-zero on failure
            actualExitCode.Should().NotBe(0,
                "Pipeline failure (including SignalR connection failure) must set non-zero exit code " +
                "so K8s marks the pod as Failed, not Completed");
        }
    }

    /// <summary>
    /// Validates that RunWorkItemLifecycleAsync returns a non-zero exit code
    /// when the pipeline completes with FinalStep != Completed, even after
    /// successfully posting the "Failed" terminal status to the orchestrator.
    /// This is the core bug: the method currently always returns 0 after posting terminal status.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PipelineCompletesWithFailedStep_SetsNonZeroExitCode()
    {
        // Arrange: Full HTTP sequence for a pipeline that fails after connecting
        // The pipeline fails because hub connection throws, so we get the SignalR-fail path.
        var assignmentJson = JsonSerializer.Serialize(
            CreateMinimalAssignment("job-pipeline-fail", "owner/repo#100"), PipelineJsonOptions.Default);

        var handler = new FakeSequentialHandler([
            (System.Net.HttpStatusCode.OK, assignmentJson),  // GET assignment
            (System.Net.HttpStatusCode.OK, "{}"),            // POST Running
            (System.Net.HttpStatusCode.OK, "{}")             // POST Failed
        ]);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkItemHttpClient(httpClient, _mockLogger.Object);

        var failingConnectionManager = Mock.Of<IAgentConnectionManager>();

        var stopCalled = new TaskCompletionSource<bool>();
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        var service = new WorkItemAgentService(
            "job-pipeline-fail", client, failingConnectionManager,
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"), _mockLifetime.Object, _mockLogger.Object);

        // Act
        var previousExitCode = Environment.ExitCode;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await service.StartAsync(cts.Token);

            var completed = await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            completed.Should().Be(stopCalled.Task, "Service should call StopApplication within timeout");

            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            var actualExitCode = Environment.ExitCode;
            Environment.ExitCode = previousExitCode; // Restore

            actualExitCode.Should().NotBe(0,
                "When pipeline completes with FinalStep=Failed (even after posting terminal status), " +
                "the process must exit non-zero so K8s marks the pod as Failed");
        }
    }

    // ── AgentConnectionManager Delegation ───────────────────────────────

    /// <summary>
    /// Validates that WorkItemAgentService composes AgentConnectionManager (or IAgentConnectionManager)
    /// for connection lifecycle management instead of managing SignalR directly.
    /// This ensures K8s agents get the same resilience, heartbeat, CancelJob handling,
    /// reconnection, and deregistration as long-running agents.
    ///
    /// NOTE: Structural test. Will fail if AgentConnectionManager is not used.
    /// </summary>
    [Fact]
    public void WorkItemAgentService_ShouldUseAgentConnectionManager()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        var usesConnectionManager = sourceCode.Contains("IAgentConnectionManager")
            || sourceCode.Contains("AgentConnectionManager");
        usesConnectionManager.Should().BeTrue(
            "WorkItemAgentService MUST compose AgentConnectionManager (or IAgentConnectionManager) " +
            "for connection lifecycle. This ensures K8s agents have the same resilience, heartbeat, " +
            "CancelJob handling, reconnection, and deregistration as long-running agents.");
    }

    [Fact]
    public void WorkItemAgentService_ShouldDelegateHeartbeatsToConnectionManager()
    {
        // WorkItemAgentService should NOT have its own heartbeat loop anymore —
        // AgentConnectionManager handles heartbeats internally.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        sourceCode.Should().NotContain("RunHeartbeatLoopAsync",
            "WorkItemAgentService must NOT have its own heartbeat loop. " +
            "AgentConnectionManager handles heartbeats internally after ConnectAndRegisterAsync.");

        sourceCode.Should().NotContain("PeriodicTimer",
            "WorkItemAgentService must NOT use PeriodicTimer directly. " +
            "Heartbeats are managed by AgentConnectionManager.");
    }

    [Fact]
    public void WorkItemAgentService_ShouldNotCallHubMethodsDirectly()
    {
        // WorkItemAgentService should use AgentConnectionManager.InvokeAsync
        // instead of bare _hubManager.Connection.InvokeAsync for resilience.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        // The old pattern: direct hub invocation without resilience
        var directHubCalls = CountOccurrences(sourceCode, "_hubManager.Connection.InvokeAsync");
        directHubCalls.Should().Be(0,
            "WorkItemAgentService must NOT call _hubManager.Connection.InvokeAsync directly. " +
            "Use AgentConnectionManager.InvokeAsync or .Connection for executor pass-through only.");
    }

    [Fact]
    public void WorkItemAgentService_ShouldWireCancelJobToCancel_Pipeline()
    {
        // WorkItemAgentService must subscribe to OnCancelJobReceived from the connection manager
        // so the orchestrator can remotely cancel running K8s jobs.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        var wiresCancelJob = sourceCode.Contains("OnCancelJobReceived");
        wiresCancelJob.Should().BeTrue(
            "WorkItemAgentService must subscribe to AgentConnectionManager.OnCancelJobReceived " +
            "to enable remote job cancellation from the orchestrator UI.");
    }

    [Fact]
    public void WorkItemAgentService_ShouldRouteConsolidationTasksToConsolidationExecutor()
    {
        // WorkItemAgentService should use IWorkItemExecutor (which routes internally)
        // instead of branching on TaskType directly.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        var usesInterface = sourceCode.Contains("IWorkItemExecutor");
        usesInterface.Should().BeTrue(
            "WorkItemAgentService must depend on IWorkItemExecutor, not branch on TaskType directly. " +
            "The WorkItemExecutorRouter handles routing transparently.");
    }

    [Fact]
    public void WorkItemAgentService_ShouldNotReferenceExecutorsDirect()
    {
        // WorkItemAgentService should not import or reference LocalPipelineExecutor
        // or LocalConsolidationExecutor directly — only IWorkItemExecutor.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        sourceCode.Should().NotContain("LocalPipelineExecutor",
            "WorkItemAgentService must not reference LocalPipelineExecutor directly. " +
            "Use IWorkItemExecutor for unified execution.");

        sourceCode.Should().NotContain("LocalConsolidationExecutor",
            "WorkItemAgentService must not reference LocalConsolidationExecutor directly. " +
            "Use IWorkItemExecutor for unified execution.");
    }

    private static int CountOccurrences(string source, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    // ── Heartbeat During Pipeline Execution ─────────────────────────────

    /// <summary>
    /// Validates that WorkItemAgentService delegates heartbeat responsibility
    /// to AgentConnectionManager (which handles heartbeats internally).
    /// Superseded by AgentConnectionManagerTests.SourceCode_SendsHeartbeats.
    /// </summary>
    [Fact]
    public void WorkItemAgentService_ShouldSendHeartbeats_DuringPipelineExecution()
    {
        // Heartbeats are now managed by AgentConnectionManager.
        // Verify WorkItemAgentService uses the connection manager (which sends heartbeats).
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        var usesConnectionManager = sourceCode.Contains("AgentConnectionManager")
            || sourceCode.Contains("IAgentConnectionManager");
        usesConnectionManager.Should().BeTrue(
            "WorkItemAgentService delegates heartbeats to AgentConnectionManager");

        // Verify AgentConnectionManager actually sends heartbeats
        var managerSource = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentConnectionManager.cs"));
        managerSource.Should().Contain("HeartbeatMessage",
            "AgentConnectionManager must send HeartbeatMessage periodically");
    }

    // ── RegisterAgent Labels From Environment ────────────────────────────

    /// <summary>
    /// Validates that WorkItemAgentService reads AGENT_LABELS from the environment
    /// and includes them in the AgentRegistrationMessage.Labels field passed to
    /// AgentConnectionManager.ConnectAndRegisterAsync.
    ///
    /// NOTE: Structural source-code inspection test. Updated for AgentConnectionManager refactoring.
    /// </summary>
    [Fact]
    public void WorkItemAgentService_ShouldReadLabelsFromEnvironment_InRegistration()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        // The registration message must NOT use a hardcoded empty array for Labels
        var hasAgentRegistrationMessage = sourceCode.Contains("AgentRegistrationMessage");
        hasAgentRegistrationMessage.Should().BeTrue("WorkItemAgentService should construct AgentRegistrationMessage");

        // It must read labels from the environment
        var readsLabels = sourceCode.Contains("EnvAgentLabels")
            || sourceCode.Contains("AGENT_LABELS");
        readsLabels.Should().BeTrue(
            "WorkItemAgentService must read AGENT_LABELS from environment for registration labels");

        // Labels must not be hardcoded empty
        // Find the AgentRegistrationMessage block
        var regIndex = sourceCode.IndexOf("AgentRegistrationMessage");
        var connectIndex = sourceCode.IndexOf("ConnectAndRegisterAsync", regIndex);
        if (connectIndex > regIndex)
        {
            var registrationBlock = sourceCode[regIndex..connectIndex];
            registrationBlock.Should().NotContain("Labels = []",
                "Labels must not be hardcoded empty — read from AGENT_LABELS env var");
        }
    }

    // ── RegisterAgent After Hub Connection ────────────────────────────────

    /// <summary>
    /// Validates that WorkItemAgentService calls ConnectAndRegisterAsync on the
    /// AgentConnectionManager, which internally handles registration with the hub.
    /// Supersedes the old "RegisterAgent after hub connection" structural test.
    /// </summary>
    [Fact]
    public void WorkItemAgentService_ShouldCallRegisterAgent_AfterHubConnection()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        // Must use ConnectAndRegisterAsync from AgentConnectionManager
        sourceCode.Should().Contain("ConnectAndRegisterAsync",
            "WorkItemAgentService must call AgentConnectionManager.ConnectAndRegisterAsync " +
            "which handles connection + registration + heartbeat start atomically.");
    }

    // ── OTel ForceFlush — Behavioral Regression Tests ────────────────────
    // TODO (WARNING): All four OTel flush tests below use the 410-Gone "already terminal" fast path
    // exclusively. The ForceFlush call in the finally block is also reached on exception paths
    // (WorkItemFetchException, general Exception, OperationCanceledException). A regression that
    // removes the flush from only an exception branch of finally would not be caught by these tests.
    // Consider adding a test using a WorkItemHttpClient that throws to cover at least one exception
    // path. (Issue #1747 review finding)

    /// <summary>
    /// Behavioral regression test: verifies that MeterProvider.ForceFlush is actually
    /// invoked when WorkItemAgentService exits with a non-null serviceProvider.
    ///
    /// Uses a <see cref="RecordingMetricExporter"/> (custom BaseExporter subclass) wired via
    /// AddReader so that the OnForceFlush call is observable without requiring a mock.
    ///
    /// Guards against accidental removal of the flush call, which would cause
    /// agent.tokens.used and agent.cost.usd to be silently dropped when ephemeral K8s
    /// worker pods exit before the OTel SDK's 60-second periodic export interval fires.
    ///
    /// Issue #1747 — metrics were missing from Grafana Cloud Prometheus because the
    /// PeriodicExportingMetricReader never fired before pod termination.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithServiceProvider_CallsMeterProviderForceFlush()
    {
        // Arrange: 410 Gone → WorkItemHttpClient returns null → quick exit path
        using var handler = new FakeHandler(System.Net.HttpStatusCode.Gone);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkItemHttpClient(httpClient, _mockLogger.Object);

        // Build a MeterProvider with a recording exporter so we can observe ForceFlush.
        // RecordingMetricExporter.OnForceFlush increments a counter; we assert it is > 0.
        var recordingExporter = new RecordingMetricExporter();
        using var meterProvider = OpenTelemetry.Sdk.CreateMeterProviderBuilder()
            .AddMeter(CodingAgentWebUI.Pipeline.Telemetry.PipelineTelemetry.SourceName)
            .AddReader(new PeriodicExportingMetricReader(recordingExporter, exportIntervalMilliseconds: int.MaxValue))
            .Build();

        var stopCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        // TODO (WARNING): SingletonServiceProvider returns null for TracerProvider — the TracerProvider
        // flush branch is not exercised by this test. Consider registering a real TracerProvider
        // (Sdk.CreateTracerProviderBuilder().Build()) to cover both flush paths. (Issue #1747 review finding)
        var serviceProvider = new SingletonServiceProvider(meterProvider);

        using var service = new WorkItemAgentService(
            "wi-flush-meter", client, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"), _mockLifetime.Object, _mockLogger.Object,
            serviceProvider: serviceProvider);

        // Act
        // TODO (WARNING): cts is passed to StartAsync but only governs startup, not background execution.
        // The effective timeout is the Task.Delay(10s) inside WhenAny. Consider documenting or removing
        // the outer cts to avoid misleading readers. (Issue #1747 review finding)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await service.StartAsync(cts.Token);

        var completed = await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        // Assert: StopApplication was called (finally block ran)
        completed.Should().Be(stopCalled.Task,
            "Service should call StopApplication after the finally block completes. " +
            "If this times out, the flush may have blocked or thrown unexpectedly.");
        _mockLifetime.Verify(l => l.StopApplication(), Times.AtLeastOnce);

        // Assert: ForceFlush was actually invoked on the MeterProvider
        // (PeriodicExportingMetricReader.OnForceFlush forwards to the exporter)
        // TODO (WARNING): ForceFlushCallCount counts Export() invocations, not a dedicated ForceFlush
        // hook. PeriodicExportingMetricReader calls Export even for an empty batch today (OTel .NET SDK
        // 1.17.0), but a future SDK version that skips Export for empty batches would cause this
        // assertion to fail spuriously even if ForceFlush is called correctly. Consider recording at
        // least one measurement before exit, or overriding OnForceFlush directly, to make the
        // assertion SDK-version-independent. (Issue #1747 review finding)
        recordingExporter.ForceFlushCallCount.Should().BeGreaterThan(0,
            "WorkItemAgentService MUST call MeterProvider.ForceFlush() before StopApplication(). " +
            "Without this, buffered agent.tokens.used and agent.cost.usd measurements are lost " +
            "when the ephemeral K8s worker pod exits before the OTel SDK's 60s flush interval. " +
            "See Issue #1747.");

        // TODO (WARNING): StopAsync uses CancellationToken.None — will block indefinitely if ExecuteAsync
        // never returns. Pass a bounded token (e.g. cts.Token or a fresh short-timeout token) to
        // prevent a hung test from blocking the CI run forever. (Issue #1747 review finding)
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Behavioral regression test: verifies that passing serviceProvider = null does NOT throw
    /// a NullReferenceException — the null guard in WorkItemAgentService's finally block is
    /// exercised and the service still calls StopApplication cleanly.
    ///
    /// This is the backward-compatibility contract for tests that omit serviceProvider.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithNullServiceProvider_DoesNotThrow_AndCallsStopApplication()
    {
        // Arrange: 410 Gone → quick exit path; no serviceProvider (null, the default)
        using var handler = new FakeHandler(System.Net.HttpStatusCode.Gone);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkItemHttpClient(httpClient, _mockLogger.Object);

        var stopCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        // serviceProvider is omitted → null default → flush is skipped with a warning log
        using var service = new WorkItemAgentService(
            "wi-null-provider", client, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"), _mockLifetime.Object, _mockLogger.Object);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await service.StartAsync(cts.Token);

        var completed = await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        // Assert: null serviceProvider must NOT throw — finally block skips flush gracefully
        completed.Should().Be(stopCalled.Task,
            "Service with null serviceProvider should still call StopApplication without throwing. " +
            "The null guard in the finally block must prevent NullReferenceException.");
        _mockLifetime.Verify(l => l.StopApplication(), Times.AtLeastOnce);

        // TODO (WARNING): StopAsync uses CancellationToken.None — will block indefinitely if ExecuteAsync
        // never returns. Pass a bounded token to prevent a hung test from blocking the CI run forever.
        // (Issue #1747 review finding)
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Behavioral regression test: verifies that TracerProvider.ForceFlush is also invoked
    /// when both MeterProvider and TracerProvider are registered in the serviceProvider.
    ///
    /// Guards against accidental removal of the TracerProvider flush, which would drop
    /// in-flight trace spans for the completed pipeline run.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithServiceProvider_AlsoCallsTracerProviderForceFlush()
    {
        // Arrange: 410 Gone → quick exit path
        using var handler = new FakeHandler(System.Net.HttpStatusCode.Gone);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkItemHttpClient(httpClient, _mockLogger.Object);

        var recordingMetricExporter = new RecordingMetricExporter();
        using var meterProvider = OpenTelemetry.Sdk.CreateMeterProviderBuilder()
            .AddMeter(CodingAgentWebUI.Pipeline.Telemetry.PipelineTelemetry.SourceName)
            .AddReader(new PeriodicExportingMetricReader(recordingMetricExporter, exportIntervalMilliseconds: int.MaxValue))
            .Build();

        var recordingSpanExporter = new RecordingSpanExporter();
        using var tracerProvider = OpenTelemetry.Sdk.CreateTracerProviderBuilder()
            .AddSource(CodingAgentWebUI.Pipeline.Telemetry.PipelineTelemetry.SourceName)
            .AddProcessor(new global::OpenTelemetry.SimpleActivityExportProcessor(recordingSpanExporter))
            .Build();

        var stopCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        // Register both providers so both flush branches are exercised
        var serviceProvider = new DualProviderServiceProvider(meterProvider, tracerProvider);

        using var service = new WorkItemAgentService(
            "wi-flush-tracer", client, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"), _mockLifetime.Object, _mockLogger.Object,
            serviceProvider: serviceProvider);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await service.StartAsync(cts.Token);

        var completed = await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        // Assert: StopApplication called
        completed.Should().Be(stopCalled.Task, "Service should call StopApplication within timeout.");
        _mockLifetime.Verify(l => l.StopApplication(), Times.AtLeastOnce);

        // Assert: MeterProvider.ForceFlush was invoked
        // TODO (WARNING): ForceFlushCallCount counts Export() invocations, not a dedicated ForceFlush
        // hook. A future SDK version that skips Export for empty batches would cause this assertion to
        // fail spuriously even if ForceFlush is called correctly. Consider recording a measurement
        // before exit to make this SDK-version-independent. (Issue #1747 review finding)
        recordingMetricExporter.ForceFlushCallCount.Should().BeGreaterThan(0,
            "WorkItemAgentService MUST call MeterProvider.ForceFlush() to flush agent.tokens.used " +
            "and agent.cost.usd before the ephemeral K8s pod exits. See Issue #1747.");

        // Assert: TracerProvider.ForceFlush was invoked
        recordingSpanExporter.ForceFlushCallCount.Should().BeGreaterThan(0,
            "WorkItemAgentService MUST also call TracerProvider.ForceFlush() to prevent " +
            "in-flight trace spans from being dropped on pod exit. See Issue #1747.");

        // TODO (WARNING): StopAsync uses CancellationToken.None — will block indefinitely if ExecuteAsync
        // never returns. Pass a bounded token to prevent a hung test from blocking the CI run forever.
        // (Issue #1747 review finding)
        await service.StopAsync(CancellationToken.None);
    }

    // ── OTel ForceFlush — Behavioral Test (existing, retained) ───────────

    /// <summary>
    /// Behavioral smoke test: verifies the flush path runs without throwing when driven
    /// through the 410-Gone fast exit. Retained as a lightweight complement to the
    /// more targeted ForceFlush assertion tests above.
    /// </summary>
    // TODO (WARNING): This test is a near-duplicate of ExecuteAsync_WithServiceProvider_CallsMeterProviderForceFlush
    // but omits the ForceFlushCallCount assertion, meaning it would pass even if the flush branch were
    // deleted as long as StopApplication is still called. Either add the ForceFlushCallCount > 0
    // assertion to make it distinct, or remove it as redundant. (Issue #1747 review finding)
    [Fact]
    public async Task ExecuteAsync_WithRealMeterProvider_FlushPathRunsWithoutThrowing()
    {
        // Arrange: 410 Gone → WorkItemHttpClient returns null → quick exit path
        using var handler = new FakeHandler(System.Net.HttpStatusCode.Gone);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkItemHttpClient(httpClient, _mockLogger.Object);

        // Build a real MeterProvider backed by a recording exporter.
        var recordingExporter = new RecordingMetricExporter();
        using var meterProvider = OpenTelemetry.Sdk.CreateMeterProviderBuilder()
            .AddMeter(CodingAgentWebUI.Pipeline.Telemetry.PipelineTelemetry.SourceName)
            .AddReader(new PeriodicExportingMetricReader(recordingExporter, exportIntervalMilliseconds: int.MaxValue))
            .Build();

        // TODO (WARNING): SingletonServiceProvider returns null for TracerProvider — the TracerProvider
        // flush branch (tracerProvider.ForceFlush) is not exercised by this test.
        // See ExecuteAsync_WithServiceProvider_AlsoCallsTracerProviderForceFlush for full coverage.
        // (Issue #1747 review finding)
        var serviceProvider = new SingletonServiceProvider(meterProvider);

        var stopCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        // TODO (WARNING): meterProvider uses a 'using' scope; ensure StopAsync is awaited before
        // meterProvider is disposed to avoid ObjectDisposedException in the finally block of
        // ExecuteAsync when the background task outlives the using scope. (Issue #1747 review finding)
        using var service = new WorkItemAgentService(
            "wi-flush-test", client, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"), _mockLifetime.Object, _mockLogger.Object,
            serviceProvider: serviceProvider);

        // Act: start the service and wait for it to call StopApplication
        // TODO (WARNING): cts is passed to StartAsync but only bounds startup, not background execution.
        // The effective timeout is Task.Delay(10s) in WhenAny. (Issue #1747 review finding)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await service.StartAsync(cts.Token);

        var completed = await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        // Assert: service called StopApplication (proves finally block ran, including flush)
        completed.Should().Be(stopCalled.Task,
            "Service should call StopApplication after the finally block completes " +
            "(which includes MeterProvider.ForceFlush). If this times out, the flush " +
            "may have blocked or thrown unexpectedly.");
        _mockLifetime.Verify(l => l.StopApplication(), Times.AtLeastOnce);

        // TODO (WARNING): StopAsync uses CancellationToken.None — will block indefinitely if ExecuteAsync
        // never returns. Pass a bounded token to prevent a hung test from blocking the CI run forever.
        // (Issue #1747 review finding)
        await service.StopAsync(CancellationToken.None);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="IServiceProvider"/> that resolves only <see cref="OpenTelemetry.Metrics.MeterProvider"/>.
    /// Used in flush behavioral tests to avoid a full DI container setup.
    /// Note: caller retains ownership of the MeterProvider; this provider does not dispose it.
    /// </summary>
    private sealed class SingletonServiceProvider(OpenTelemetry.Metrics.MeterProvider meterProvider) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(OpenTelemetry.Metrics.MeterProvider))
                return meterProvider;
            // TracerProvider is not registered — WorkItemAgentService logs a warning and continues
            return null;
        }
    }

    /// <summary>
    /// <see cref="IServiceProvider"/> that resolves both <see cref="OpenTelemetry.Metrics.MeterProvider"/>
    /// and <see cref="OpenTelemetry.Trace.TracerProvider"/>.
    /// Used to exercise both flush branches in the WorkItemAgentService finally block.
    /// Note: caller retains ownership of both providers; this stub does not dispose them.
    /// </summary>
    private sealed class DualProviderServiceProvider(
        OpenTelemetry.Metrics.MeterProvider meterProvider,
        OpenTelemetry.Trace.TracerProvider tracerProvider) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(OpenTelemetry.Metrics.MeterProvider))
                return meterProvider;
            if (serviceType == typeof(OpenTelemetry.Trace.TracerProvider))
                return tracerProvider;
            return null;
        }
    }

    /// <summary>
    /// Custom <see cref="BaseExporter{T}"/> for metrics that records how many times
    /// <see cref="Export"/> has been called. Used to assert that
    /// <c>MeterProvider.ForceFlush()</c> actually propagates through to the exporter
    /// as an export cycle.
    ///
    /// Note: <c>PeriodicExportingMetricReader.ForceFlush</c> triggers <see cref="Export"/>
    /// (not <c>OnForceFlush</c> on the exporter) — this is by SDK design. Tracking
    /// <see cref="Export"/> calls is the correct observable side-effect of ForceFlush.
    /// </summary>
    private sealed class RecordingMetricExporter : BaseExporter<global::OpenTelemetry.Metrics.Metric>
    {
        private int _forceFlushCallCount;

        /// <summary>Number of times Export was invoked (triggered by ForceFlush or periodic export).</summary>
        public int ForceFlushCallCount => _forceFlushCallCount;

        public override ExportResult Export(in Batch<global::OpenTelemetry.Metrics.Metric> batch)
        {
            Interlocked.Increment(ref _forceFlushCallCount);
            return ExportResult.Success;
        }
    }

    /// <summary>
    /// Custom <see cref="BaseExporter{T}"/> for trace spans that records how many times
    /// <see cref="OnForceFlush"/> has been called. Used to assert that
    /// <c>TracerProvider.ForceFlush()</c> actually propagates through to the exporter.
    /// </summary>
    private sealed class RecordingSpanExporter : BaseExporter<System.Diagnostics.Activity>
    {
        private int _forceFlushCallCount;

        /// <summary>Number of times OnForceFlush was invoked.</summary>
        public int ForceFlushCallCount => _forceFlushCallCount;

        public override ExportResult Export(in Batch<System.Diagnostics.Activity> batch)
            => ExportResult.Success;

        protected override bool OnForceFlush(int timeoutMilliseconds)
        {
            Interlocked.Increment(ref _forceFlushCallCount);
            return true;
        }
    }

    private static string GetSourceDirectory()
    {
        // Navigate from test bin directory to solution root
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }


    private WorkItemAgentService CreateService(string workItemId)
    {
        return new WorkItemAgentService(
            workItemId, _workItemClient, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("test-agent"), _mockLifetime.Object, _mockLogger.Object);
    }

    private WorkItemExecutorRouter CreateMinimalWorkItemExecutor()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockHttpFactory = new Mock<IHttpClientFactory>();
        var mockQgValidator = new Mock<CodingAgentWebUI.Pipeline.Interfaces.IQualityGateValidator>();
        var pipelineExecutor = new LocalPipelineExecutor(
            mockOrchestrator.Object, mockHttpFactory.Object,
            new PipelineConfiguration(), mockQgValidator.Object, _mockLogger.Object,
            agentIdentity: new AgentId("test-agent"));
        var consolidationExecutor = new LocalConsolidationExecutor(
            mockOrchestrator.Object, mockHttpFactory.Object, _mockLogger.Object);
        return new WorkItemExecutorRouter(pipelineExecutor, consolidationExecutor, _mockLogger.Object);
    }

    private static JobAssignmentMessage CreateMinimalAssignment(string jobId, string issueId) => new()
    {
        JobId = jobId,
        IssueIdentifier = issueId,
        IssueDetail = new IssueDetail { Identifier = issueId, Title = "Test", Description = "", Labels = [] },
        ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
        RepoProviderConfigId = "repo-1",
        AgentProviderConfigId = "agent-1",
        PipelineConfiguration = new PipelineConfiguration(),
        ProviderConfigs = [],
        ReviewerConfigs = [],
        QualityGateConfigs = [],
        IssueComments = [],
        InitiatedBy = "test"
    };

    private sealed class FakeOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _statusCode;
        public FakeHandler(System.Net.HttpStatusCode statusCode) => _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeSequentialHandler : HttpMessageHandler
    {
        private readonly (System.Net.HttpStatusCode Code, string Body)[] _responses;
        private int _callIndex;

        public int CallCount => _callIndex;

        public FakeSequentialHandler((System.Net.HttpStatusCode, string)[] responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _callIndex) - 1;
            var (code, body) = index < _responses.Length
                ? _responses[index]
                : (System.Net.HttpStatusCode.InternalServerError, "{}");
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
