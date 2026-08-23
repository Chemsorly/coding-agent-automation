using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Metrics;

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
    [InlineData(0, "deps.WorkItemId")]
    [InlineData(1, "deps.WorkItemClient")]
    [InlineData(2, "deps.ConnectionManager")]
    [InlineData(3, "deps.WorkItemExecutor")]
    [InlineData(4, "deps.CompletionReporter")]
    [InlineData(5, "deps.Lifetime")]
    [InlineData(6, "deps.Logger")]
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

        var act = () => new WorkItemAgentService(new WorkItemAgentServiceDependencies(
            (string)args[0]!,
            (IWorkItemLifecycleClient)args[1]!,
            (IAgentConnectionManager)args[2]!,
            (IWorkItemExecutor)args[3]!,
            (IJobCompletionReporter)args[4]!,
            new AgentId("agent-1"),
            (IHostApplicationLifetime)args[5]!,
            (Serilog.ILogger)args[6]!));

        act.Should().Throw<ArgumentNullException>().WithParameterName(expectedParamName);
        // TODO: These expectedParamName values (e.g. "deps.WorkItemId") are coupled to the internal
        // ThrowIfNull(deps.X) call expression in the WorkItemAgentService constructor. If the
        // constructor parameter is renamed (e.g. deps → dependencies), or if validation is moved
        // into the record constructor using nameof, these assertions will fail for the wrong reason
        // or pass incorrectly. They reflect an implementation detail rather than the public API
        // contract ("passing null workItemId throws"). Consider using nameof-based param names if
        // the validation is ever moved to the record constructor.
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

        var service = new WorkItemAgentService(new WorkItemAgentServiceDependencies(
            "wi-rejected", client, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"), _mockLifetime.Object, _mockLogger.Object));

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

        var service = new WorkItemAgentService(new WorkItemAgentServiceDependencies(
            "wi-terminal", client, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"), _mockLifetime.Object, _mockLogger.Object));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);

        // Wait for the service to call StopApplication (signals lifecycle complete)
        var completed = await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(stopCalled.Task, "Service should call StopApplication within timeout");

        await service.StopAsync(CancellationToken.None);

        _mockLifetime.Verify(l => l.StopApplication(), Times.AtLeastOnce);
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

        var service = new WorkItemAgentService(new WorkItemAgentServiceDependencies(
            "job-fail", client, mockConnectionManager,
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"), _mockLifetime.Object, _mockLogger.Object));

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

        var service = new WorkItemAgentService(new WorkItemAgentServiceDependencies(
            "job-pipeline-fail", client, failingConnectionManager,
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"), _mockLifetime.Object, _mockLogger.Object));

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

    // ── ForceFlush Return Value Handling ─────────────────────────────────

    // TODO: Add a complementary negative-case test that verifies the flush-timeout Warning is
    // NOT emitted when MeterProvider.ForceFlush() returns true (the success path). Without this,
    // a regression that inverts the condition (e.g. "if (metricsFlushed)" instead of
    // "if (!metricsFlushed)") would go undetected — the existing test only asserts
    // Times.AtLeastOnce under the failure condition and never asserts Times.Never under success.

    /// <summary>
    /// Behavioural test: WorkItemAgentService must log a Warning when MeterProvider.ForceFlush()
    /// returns false (i.e., the flush timed out or failed).
    ///
    /// The test injects a real MeterProvider whose underlying reader always returns false from
    /// OnCollect, which causes ForceFlush to return false. It then runs a complete work-item
    /// lifecycle (terminal 410-Gone assignment so the lifecycle exits immediately) and asserts
    /// that a Warning log was emitted. This exercises the actual runtime code path rather than
    /// scanning source text.
    /// </summary>
    [Fact]
    public async Task WorkItemAgentService_LogsWarning_WhenMeterProviderForceFlushTimesOut()
    {
        // Arrange: terminal assignment (410 Gone) → lifecycle exits immediately, so the
        // finally block (which calls ForceFlush) runs quickly in the test.
        // TODO: handler and httpClient are IDisposable but are created without 'using'. Add
        // 'using var' to both declarations for consistency with the disposal pattern used in
        // surrounding tests and to prevent resource leaks if the test scaffolding changes.
        var handler = new FakeHandler(System.Net.HttpStatusCode.Gone);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkItemHttpClient(httpClient, _mockLogger.Object);

        // Build a real ServiceProvider that contains a MeterProvider backed by a reader that
        // always returns false from OnCollect. MeterProvider.ForceFlush() propagates that false
        // back to the caller, which is the condition under test.
        var services = new ServiceCollection();
        services.AddOpenTelemetry().WithMetrics(m => m
            .AddMeter(CodingAgentWebUI.Pipeline.Telemetry.PipelineTelemetry.SourceName)
            .AddReader(new AlwaysFailMetricReader()));
        using var serviceProvider = services.BuildServiceProvider();

        var stopCalled = new TaskCompletionSource<bool>();
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        // TODO: service is declared as plain 'var' and is not disposed deterministically. Declare with
        // 'using var service = ...' to ensure Dispose() is called before serviceProvider is disposed.
        // Without deterministic disposal, the background task could access the already-disposed
        // serviceProvider after the 'using var serviceProvider' scope exits, causing ObjectDisposedException.
        var service = new WorkItemAgentService(new WorkItemAgentServiceDependencies(
            "wi-flush-timeout",
            client,
            Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("agent-1"),
            _mockLifetime.Object,
            _mockLogger.Object,
            ServiceProvider: serviceProvider));

        // Act: run the full lifecycle (terminates immediately on 410 Gone)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        var completed = await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        completed.Should().Be(stopCalled.Task, "Service should call StopApplication within timeout");
        // TODO: StopAsync is called with CancellationToken.None. If ExecuteAsync does not exit
        // (e.g., because AlwaysFailMetricReader.OnCollect blocks for its full timeoutMilliseconds
        // before returning false), this call will block indefinitely and hang the CI run.
        // Pass a bounded token (e.g., cts.Token or a fresh short-timeout token) to bound the stop call.
        await service.StopAsync(CancellationToken.None);

        // Assert: a Warning was logged because ForceFlush returned false.
        // The warning tells operators to check OTEL_EXPORTER_OTLP_ENDPOINT and the Secret.
        // TODO: The predicate was narrowed to "flush timed out" only (removed the || msg.Contains("OTLP")
        // fallback) to avoid false positives from the unrelated "MeterProvider not available" and
        // "TracerProvider not available" warning paths, which also contain "OTLP" in their text.
        // TODO: Times.AtLeastOnce does not guard against double-emission regressions (e.g., if a
        // retry path calls ForceFlush a second time, the Warning would be logged twice and the test
        // would still pass). Consider Times.Once to make the test sensitive to duplicate warnings.
        _mockLogger.Verify(l => l.Warning(
            It.Is<string>(msg => msg.Contains("flush timed out")),
            It.IsAny<string>()),
            Times.AtLeastOnce,
            "WorkItemAgentService must log a Warning when MeterProvider.ForceFlush() returns false so " +
            "that silent flush timeouts (which cause metrics like pipeline_decomposition_duration_seconds " +
            "to be missing from Grafana) are observable in pod logs.");
    }

    /// <summary>
    /// Structural test: the two-argument <c>AddOtlpExporter</c> overload in <c>Program.cs</c>
    /// must set <see cref="MetricReaderTemporalityPreference.Cumulative"/> on the reader options
    /// supplied by the SDK.
    ///
    /// This verifies the fix for the Grafana scrape gap: without the explicit
    /// <c>TemporalityPreference = Cumulative</c> assignment, Grafana Cloud's Prometheus-compatible
    /// OTLP receiver may silently drop histograms and counters depending on its configuration.
    /// The test captures the <see cref="MetricReaderTemporalityPreference"/> value actually
    /// applied inside the callback and asserts it is <see cref="MetricReaderTemporalityPreference.Cumulative"/>.
    ///
    /// This is falsifiable: replacing the assignment with <c>Delta</c> (or removing it)
    /// causes the assertion to fail.
    /// </summary>
    [Fact]
    public void OtlpMetrics_WhenConfiguredWithCumulativeTemporality_ReaderOptionsCumulativeIsApplied()
    {
        // Arrange: capture the TemporalityPreference that the two-argument AddOtlpExporter
        // callback actually sets on the reader options. The SDK invokes this callback when
        // the MeterProvider is first resolved from the DI container.
        // appliedPreference is captured AFTER the assignment so the assertion is falsifiable:
        // removing or changing the assignment line causes the captured value to differ from Cumulative.
        var appliedPreference = MetricReaderTemporalityPreference.Delta; // sentinel — must be overwritten
        var callbackInvoked = false;

        var services = new ServiceCollection();
        services.AddOpenTelemetry().WithMetrics(m => m
            .AddMeter(CodingAgentWebUI.Pipeline.Telemetry.PipelineTelemetry.SourceName)
            // This is the exact call from Program.cs — the configuration under test.
            // The callback must set readerOptions.TemporalityPreference = Cumulative.
            // Removing or changing this assignment causes the assertion below to fail.
            .AddOtlpExporter((_, readerOptions) =>
            {
                callbackInvoked = true;
                readerOptions.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                // Capture AFTER the assignment so the assertion verifies what was written,
                // not the SDK default that existed at callback entry.
                appliedPreference = readerOptions.TemporalityPreference;
            }));

        // Act: resolve MeterProvider — the SDK invokes the AddOtlpExporter callback here.
        using var sp = services.BuildServiceProvider();
        _ = sp.GetRequiredService<MeterProvider>();

        // Assert: the callback must have been invoked and set Cumulative.
        callbackInvoked.Should().BeTrue("AddOtlpExporter callback must be invoked when MeterProvider is built");
        appliedPreference.Should().Be(MetricReaderTemporalityPreference.Cumulative,
            "Program.cs must configure MetricReaderTemporalityPreference.Cumulative on the OTLP exporter's " +
            "reader options. Without this, histograms and counters may be silently dropped by " +
            "Grafana Cloud's OTLP receiver, leaving pipeline_decomposition_* metrics absent from Grafana.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private WorkItemAgentService CreateService(string workItemId)
    {
        return new WorkItemAgentService(new WorkItemAgentServiceDependencies(
            workItemId, _workItemClient, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("test-agent"), _mockLifetime.Object, _mockLogger.Object));
    }

    private WorkItemExecutorRouter CreateMinimalWorkItemExecutor()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockHttpFactory = new Mock<IHttpClientFactory>();
        var mockQgValidator = new Mock<CodingAgentWebUI.Pipeline.Interfaces.IQualityGateValidator>();
        var pipelineExecutor = new LocalPipelineExecutor(new LocalPipelineExecutorDependencies(
            mockOrchestrator.Object, mockHttpFactory.Object,
            new PipelineConfiguration(), mockQgValidator.Object, _mockLogger.Object,
            AgentIdentity: new AgentId("test-agent")));
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

    /// <summary>
    /// A <see cref="MetricReader"/> whose <see cref="OnCollect"/> always returns <c>false</c>,
    /// simulating a flush timeout. When registered as the sole reader on a <see cref="MeterProvider"/>,
    /// <c>meterProvider.ForceFlush()</c> will return <c>false</c>, which is the condition that
    /// must trigger a Warning log in <see cref="WorkItemAgentService"/>.
    /// </summary>
    private sealed class AlwaysFailMetricReader : MetricReader
    {
        protected override bool OnCollect(int timeoutMilliseconds) => false;
    }
}
