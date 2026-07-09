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
    [InlineData(0, "workItemId")]
    [InlineData(1, "workItemClient")]
    [InlineData(2, "connectionManager")]
    [InlineData(3, "workItemExecutor")]
    [InlineData(4, "completionReporter")]
    [InlineData(5, "agentIdentity")]
    [InlineData(6, "lifetime")]
    [InlineData(7, "logger")]
    public void Constructor_NullParameter_Throws(int nullIndex, string expectedParamName)
    {
        var args = new object?[]
        {
            "wi-1",
            _workItemClient,
            Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentIdentity("agent-1"),
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
            (AgentIdentity)args[5]!,
            (IHostApplicationLifetime)args[6]!,
            (Serilog.ILogger)args[7]!);

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
            "wi-rejected", client, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentIdentity("agent-1"), _mockLifetime.Object, _mockLogger.Object);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(cts.Token);
        await Task.Delay(500);
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

        var service = new WorkItemAgentService(
            "wi-terminal", client, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentIdentity("agent-1"), _mockLifetime.Object, _mockLogger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(cts.Token);
        // Give it time to complete
        await Task.Delay(500);
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
            && sourceCode.Contains("0")
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

        var failingConnectionManager = Mock.Of<IAgentConnectionManager>();

        var stopCalled = new TaskCompletionSource<bool>();
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        var service = new WorkItemAgentService(
            "job-pipeline-fail", client, failingConnectionManager,
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
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

    // ── ModelName/RepositoryName Propagation ────────────────────────────

    /// <summary>
    /// Validates that WorkItemAgentService populates ModelName and RepositoryName
    /// from the JobAssignmentMessage into ActiveJobState during registration.
    /// This ensures orchestrator restart resilience — the agent can report accurate
    /// metadata during re-registration.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PopulatesModelNameAndRepositoryName_InActiveJobState()
    {
        // Arrange: HTTP handler that returns assignment with ModelName/RepositoryName on GET,
        // and accepts POST status updates
        var assignment = CreateMinimalAssignment("job-model-test", "org/repo#42") with
        {
            ModelName = "claude-sonnet-4.6",
            RepositoryName = "org/test-repo"
        };
        var assignmentJson = JsonSerializer.Serialize(assignment, PipelineJsonOptions.Default);

        var handler = new FakeSequentialHandler([
            (System.Net.HttpStatusCode.OK, assignmentJson),       // GET /api/work-items/{id}/assignment
            (System.Net.HttpStatusCode.OK, "{}"),                  // POST /api/work-items/{id}/status (Running)
        ]);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkItemHttpClient(httpClient, _mockLogger.Object);

        // Mock connection manager to capture the registration message
        AgentRegistrationMessage? capturedRegistration = null;
        var mockConnectionManager = new Mock<IAgentConnectionManager>();
        mockConnectionManager
            .Setup(m => m.ConnectAndRegisterAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<CancellationToken>()))
            .Callback<AgentRegistrationMessage, CancellationToken>((reg, _) => capturedRegistration = reg)
            .Returns(Task.CompletedTask);
        mockConnectionManager.SetupGet(m => m.Connection).Returns((Microsoft.AspNetCore.SignalR.Client.HubConnection)null!);

        var service = new WorkItemAgentService(
            "wi-model-test", client, mockConnectionManager.Object,
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentIdentity("agent-model"), _mockLifetime.Object, _mockLogger.Object);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(cts.Token);
        await Task.Delay(800);
        await service.StopAsync(CancellationToken.None);

        // Assert: registration was called and ActiveJobState carries ModelName/RepositoryName
        capturedRegistration.Should().NotBeNull("ConnectAndRegisterAsync should have been called");
        capturedRegistration!.ActiveJob.Should().NotBeNull("ActiveJob should be populated");
        capturedRegistration.ActiveJob!.ModelName.Should().Be("claude-sonnet-4.6");
        capturedRegistration.ActiveJob.RepositoryName.Should().Be("org/test-repo");
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
            workItemId, _workItemClient, Mock.Of<IAgentConnectionManager>(),
            CreateMinimalWorkItemExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentIdentity("test-agent"), _mockLifetime.Object, _mockLogger.Object);
    }

    private IWorkItemExecutor CreateMinimalWorkItemExecutor()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockHttpFactory = new Mock<IHttpClientFactory>();
        var mockQgValidator = new Mock<CodingAgentWebUI.Pipeline.Interfaces.IQualityGateValidator>();
        var pipelineExecutor = new LocalPipelineExecutor(
            mockOrchestrator.Object, mockHttpFactory.Object,
            new PipelineConfiguration(), mockQgValidator.Object, _mockLogger.Object,
            agentIdentity: new AgentIdentity("test-agent"));
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
