using AwesomeAssertions;
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

    [Fact]
    public void Constructor_NullWorkItemId_Throws()
    {
        var act = () => CreateService(workItemId: null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("workItemId");
    }

    [Fact]
    public void Constructor_NullWorkItemClient_Throws()
    {
        var act = () => new WorkItemAgentService(
            "wi-1", null!, _hubManager, _hubFactory,
            CreateMinimalExecutor(), CreateMinimalConsolidationExecutor(),
            new AgentIdentity("agent-1"), _mockLifetime.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("workItemClient");
    }

    [Fact]
    public void Constructor_NullHubManager_Throws()
    {
        var act = () => new WorkItemAgentService(
            "wi-1", _workItemClient, null!, _hubFactory,
            CreateMinimalExecutor(), CreateMinimalConsolidationExecutor(),
            new AgentIdentity("agent-1"), _mockLifetime.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManager");
    }

    [Fact]
    public void Constructor_NullHubManagerFactory_Throws()
    {
        var act = () => new WorkItemAgentService(
            "wi-1", _workItemClient, _hubManager, null!,
            CreateMinimalExecutor(), CreateMinimalConsolidationExecutor(),
            new AgentIdentity("agent-1"), _mockLifetime.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManagerFactory");
    }

    [Fact]
    public void Constructor_NullExecutor_Throws()
    {
        var act = () => new WorkItemAgentService(
            "wi-1", _workItemClient, _hubManager, _hubFactory,
            null!, CreateMinimalConsolidationExecutor(),
            new AgentIdentity("agent-1"), _mockLifetime.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("executor");
    }

    [Fact]
    public void Constructor_NullConsolidationExecutor_Throws()
    {
        var act = () => new WorkItemAgentService(
            "wi-1", _workItemClient, _hubManager, _hubFactory,
            CreateMinimalExecutor(), null!,
            new AgentIdentity("agent-1"), _mockLifetime.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("consolidationExecutor");
    }

    [Fact]
    public void Constructor_NullAgentIdentity_Throws()
    {
        var act = () => new WorkItemAgentService(
            "wi-1", _workItemClient, _hubManager, _hubFactory,
            CreateMinimalExecutor(), CreateMinimalConsolidationExecutor(),
            null!, _mockLifetime.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("agentIdentity");
    }

    [Fact]
    public void Constructor_NullLifetime_Throws()
    {
        var act = () => new WorkItemAgentService(
            "wi-1", _workItemClient, _hubManager, _hubFactory,
            CreateMinimalExecutor(), CreateMinimalConsolidationExecutor(),
            new AgentIdentity("agent-1"), null!, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("lifetime");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new WorkItemAgentService(
            "wi-1", _workItemClient, _hubManager, _hubFactory,
            CreateMinimalExecutor(), CreateMinimalConsolidationExecutor(),
            new AgentIdentity("agent-1"), _mockLifetime.Object, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
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
}
