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

    // ── Helpers ──────────────────────────────────────────────────────────

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
