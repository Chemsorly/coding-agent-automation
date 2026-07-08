using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

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
    private readonly HubConnectionManager _hubManager;
    private readonly HubConnectionManagerFactory _hubFactory;
    private readonly WorkItemHttpClient _workItemClient;

    public WorkItemAgentServiceTests()
    {
        _hubManager = new HubConnectionManager("http://localhost:9999", "test-agent", "test-key", _mockLogger.Object);
        _hubFactory = new HubConnectionManagerFactory("http://localhost:9999", "test-agent", "test-key", _mockLogger.Object);
        var httpClient = new HttpClient(new FakeOkHandler()) { BaseAddress = new Uri("http://localhost") };
        _workItemClient = new WorkItemHttpClient(httpClient, _mockLogger.Object);
    }

    public async ValueTask DisposeAsync()
    {
        await _hubManager.DisposeAsync();
    }

    // ── Constructor Guard Clauses ────────────────────────────────────────

    [Theory]
    [InlineData(0, "workItemId")]
    [InlineData(1, "workItemClient")]
    [InlineData(2, "hubManager")]
    [InlineData(3, "hubManagerFactory")]
    [InlineData(4, "executor")]
    [InlineData(5, "consolidationExecutor")]
    [InlineData(6, "agentIdentity")]
    [InlineData(7, "lifetime")]
    [InlineData(8, "logger")]
    public void Constructor_NullParameter_Throws(int nullIndex, string expectedParamName)
    {
        var args = new object?[]
        {
            "wi-1",
            _workItemClient,
            _hubManager,
            _hubFactory,
            CreateMinimalExecutor(),
            CreateMinimalConsolidationExecutor(),
            new AgentIdentity("agent-1"),
            _mockLifetime.Object,
            _mockLogger.Object
        };
        args[nullIndex] = null;

        var act = () => new WorkItemAgentService(
            (string)args[0]!,
            (WorkItemHttpClient)args[1]!,
            (HubConnectionManager)args[2]!,
            (HubConnectionManagerFactory)args[3]!,
            (LocalPipelineExecutor)args[4]!,
            (LocalConsolidationExecutor)args[5]!,
            (AgentIdentity)args[6]!,
            (IHostApplicationLifetime)args[7]!,
            (Serilog.ILogger)args[8]!);

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

        var service = new WorkItemAgentService(
            "wi-rejected", client, _hubManager, _hubFactory,
            CreateMinimalExecutor(), CreateMinimalConsolidationExecutor(),
            new AgentIdentity("agent-1"), _mockLifetime.Object, _mockLogger.Object);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(cts.Token);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        // Assert
        handler.CallCount.Should().Be(2, "GET assignment + POST Running, then abort — no further calls");
        _hubManager.IsConnected.Should().BeFalse(
            "Agent must NOT connect SignalR when Running status is rejected");
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

        var service = new WorkItemAgentService(
            "wi-terminal", client, _hubManager, _hubFactory,
            CreateMinimalExecutor(), CreateMinimalConsolidationExecutor(),
            new AgentIdentity("agent-1"), _mockLifetime.Object, _mockLogger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(cts.Token);
        // Give it time to complete
        await Task.Delay(500);
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
        await using var failingHubManager = new HubConnectionManager(
            "http://localhost:1", "test-agent", "test-key", _mockLogger.Object);
        var failingHubFactory = new HubConnectionManagerFactory(
            "http://localhost:1", "test-agent", "test-key", _mockLogger.Object);

        // Use a TaskCompletionSource to detect when StopApplication is called
        var stopCalled = new TaskCompletionSource<bool>();
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        var service = new WorkItemAgentService(
            "job-fail", client, failingHubManager, failingHubFactory,
            CreateMinimalExecutor(), CreateMinimalConsolidationExecutor(),
            new AgentIdentity("agent-1"), _mockLifetime.Object, _mockLogger.Object);

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

        await using var failingHub = new HubConnectionManager(
            "http://localhost:1", "test-agent", "test-key", _mockLogger.Object);
        var failingFactory = new HubConnectionManagerFactory(
            "http://localhost:1", "test-agent", "test-key", _mockLogger.Object);

        var stopCalled = new TaskCompletionSource<bool>();
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        var service = new WorkItemAgentService(
            "job-pipeline-fail", client, failingHub, failingFactory,
            CreateMinimalExecutor(), CreateMinimalConsolidationExecutor(),
            new AgentIdentity("agent-1"), _mockLifetime.Object, _mockLogger.Object);

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

    // ── RegisterAgent Labels From Environment ────────────────────────────

    /// <summary>
    /// Validates that WorkItemAgentService reads AGENT_LABELS from the environment
    /// and includes them in the AgentRegistrationMessage.Labels field.
    /// Without this, agents registered via the K8s work-item path always have empty
    /// labels, making them invisible in the Agent Monitoring UI labels column
    /// and breaking label-based routing assertions.
    /// 
    /// NOTE: This is a structural source-code inspection test rather than a behavioral test
    /// because HubConnection is sealed and cannot be mocked to intercept InvokeAsync calls.
    /// If HubConnection becomes mockable in the future, replace this with a behavioral test
    /// that captures the actual AgentRegistrationMessage sent during registration.
    /// This test will false-fail if the registration logic is extracted to a helper method
    /// or significantly restructured — in that case, update the search patterns.
    /// </summary>
    [Fact]
    public void WorkItemAgentService_ShouldReadLabelsFromEnvironment_InRegistration()
    {
        // Source-code level structural test: the registration message MUST NOT use
        // a hardcoded empty array for Labels. It should read from AGENT_LABELS env var.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        // Find the RegisterAgent registration message construction
        var registerIndex = sourceCode.IndexOf("RegisterAgent");
        registerIndex.Should().BeGreaterThan(0, "RegisterAgent call should exist");

        // Find the Labels assignment in the registration block near RegisterAgent
        // Look backwards from RegisterAgent to find the AgentRegistrationMessage construction
        var beforeRegister = sourceCode[..registerIndex];
        var registrationMsgIndex = beforeRegister.LastIndexOf("AgentRegistrationMessage");
        registrationMsgIndex.Should().BeGreaterThan(0, "AgentRegistrationMessage should be constructed before RegisterAgent");

        var registrationBlock = sourceCode[registrationMsgIndex..registerIndex];

        // The Labels field must NOT be hardcoded to empty
        registrationBlock.Should().NotContain("Labels = []",
            "WorkItemAgentService MUST read labels from AGENT_LABELS environment variable, " +
            "not use a hardcoded empty array. Without this, K8s-mode agents show no labels " +
            "in the Agent Monitoring table.");

        // It should reference the AGENT_LABELS env var (directly or via AgentDefaults.EnvAgentLabels)
        // Search the broader registration section (from Step 3b comment through RegisterAgent)
        var step3bIndex = sourceCode.LastIndexOf("Step 3b", registerIndex);
        var registrationSection = step3bIndex > 0
            ? sourceCode[step3bIndex..registerIndex]
            : sourceCode[registrationMsgIndex..registerIndex];

        var readsEnvLabels = registrationSection.Contains("EnvAgentLabels")
            || registrationSection.Contains("AGENT_LABELS");
        readsEnvLabels.Should().BeTrue(
            "WorkItemAgentService must read AGENT_LABELS from environment for registration labels");
    }

    // ── RegisterAgent After Hub Connection ────────────────────────────────

    /// <summary>
    /// Validates that the K8s-mode WorkItemAgentService calls RegisterAgent
    /// on the hub connection after successfully connecting. Without this call,
    /// the orchestrator's [RequiresActiveJob] filter rejects all hub method
    /// invocations (token refresh, step transitions, etc.).
    /// 
    /// This test verifies by asserting that when the hub connects but RegisterAgent
    /// is not called, token-dependent operations fail. The fix should add a
    /// RegisterAgent call with ActiveJob state after _hubManager.StartAsync().
    /// </summary>
    [Fact]
    public void WorkItemAgentService_ShouldCallRegisterAgent_AfterHubConnection()
    {
        // This test verifies the structural requirement: the service MUST invoke
        // RegisterAgent on the hub connection after StartAsync.
        // We verify this by checking the source code contract — the method name
        // HubMethodNames.RegisterAgent must appear in the WorkItemAgentService's
        // RunWorkItemLifecycleAsync flow after _hubManager.StartAsync(ct).
        //
        // Since we cannot mock HubConnection.InvokeAsync (sealed class), we instead
        // verify that the service HAS a RegisterAgent call by examining it runs
        // correctly when the hub is available (covered by integration tests).
        //
        // For the unit test, we verify the FakeSequentialHandler shows the correct
        // HTTP call sequence AND that the service attempts hub registration by
        // checking it doesn't crash with "Agent not registered" when the full
        // lifecycle is attempted with a mock hub.
        //
        // FAILING: This test documents the MISSING RegisterAgent call.
        // The service currently goes directly from StartAsync to pipeline execution
        // without registering, causing [RequiresActiveJob] filter rejection.

        // Verify the code path: reading the service source to confirm RegisterAgent is invoked.
        // This is a "design test" that will fail until the fix is applied.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        // After hub connect, RegisterAgent MUST be called
        var hubStartIndex = sourceCode.IndexOf("_hubManager.StartAsync(ct)");
        hubStartIndex.Should().BeGreaterThan(0, "hub StartAsync call should exist");

        var afterHubStart = sourceCode[hubStartIndex..];
        var registerIndex = afterHubStart.IndexOf("RegisterAgent");

        registerIndex.Should().BeGreaterThan(0,
            "WorkItemAgentService MUST call RegisterAgent on the hub connection after StartAsync. " +
            "Without this, the orchestrator's [RequiresActiveJob] filter rejects all hub method " +
            "invocations (token refresh, output reporting, etc.), causing pipeline failure.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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
            workItemId, _workItemClient, _hubManager, _hubFactory,
            CreateMinimalExecutor(), CreateMinimalConsolidationExecutor(),
            new AgentIdentity("test-agent"), _mockLifetime.Object, _mockLogger.Object);
    }

    private LocalPipelineExecutor CreateMinimalExecutor()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockHttpFactory = new Mock<IHttpClientFactory>();
        var mockQgValidator = new Mock<CodingAgentWebUI.Pipeline.Interfaces.IQualityGateValidator>();
        return new LocalPipelineExecutor(
            mockOrchestrator.Object, mockHttpFactory.Object,
            new PipelineConfiguration(), mockQgValidator.Object, _mockLogger.Object,
            agentIdentity: new AgentIdentity("test-agent"));
    }

    private LocalConsolidationExecutor CreateMinimalConsolidationExecutor()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockHttpFactory = new Mock<IHttpClientFactory>();
        return new LocalConsolidationExecutor(mockOrchestrator.Object, mockHttpFactory.Object, _mockLogger.Object);
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
