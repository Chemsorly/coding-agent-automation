using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Log-forging regression tests (CodeQL cs/log-forging) for <see cref="AgentHub"/>.
/// The <c>agentId</c> query parameter is caller-controlled, so CR/LF must be escaped before it
/// is written to a log entry — otherwise a crafted value forges extra log lines.
/// </summary>
public sealed class AgentHubLogSanitizationTests
{
    private const string ForgedAgentId = "agent-1\r\n[ERR] forged entry";
    private const string EscapedAgentId = "agent-1\\r\\n[ERR] forged entry";

    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ILogger> _logger = new();

    [Fact]
    public async Task OnConnectedAsync_AgentIdWithNewlines_LogsEscapedAgentId()
    {
        var hub = CreateHub(BuildContext("conn-1", ForgedAgentId));

        await hub.OnConnectedAsync();

        _logger.Verify(l => l.Information(It.IsAny<string>(), EscapedAgentId, "conn-1"), Times.Once);
    }

    [Fact]
    public async Task SubscribeToRun_UnassignedAgentIdWithNewlines_LogsEscapedAgentId()
    {
        var jobId = Guid.NewGuid().ToString();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        var hub = CreateHub(BuildContext("conn-1", ForgedAgentId));

        var act = () => hub.SubscribeToRun(jobId);

        await act.Should().ThrowAsync<HubException>();
        _logger.Verify(l => l.Warning(It.IsAny<string>(), EscapedAgentId, jobId), Times.Once);
    }

    // ── SubscribeToRun — duplicate agentId query params ──────────────────

    /// <summary>
    /// When a client supplies agentId more than once (e.g. ?agentId=agent-1&amp;agentId=extra),
    /// the old ToString() produced "agent-1,extra". The ownership check compared that string
    /// to the run's AgentId ("agent-1"), found a mismatch, and threw HubException.
    /// The fix (FirstOrDefault) reads only "agent-1", so the ownership check matches and
    /// the subscription is allowed.
    /// </summary>
    [Fact]
    public async Task SubscribeToRun_DuplicateAgentIdQueryParams_ResolvesByFirstValue()
    {
        var jobId = Guid.NewGuid().ToString();
        // The run is assigned to "agent-1".
        var run = new PipelineRun
        {
            RunId = jobId,
            AgentId = "agent-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "t",
            IssueProviderConfigId = "i",
            RepoProviderConfigId = "r"
        };
        // First call: ownership check (must return run with AgentId == "agent-1" to pass).
        // Second call: snapshot push guard (return null to skip the snapshot block and avoid
        // needing to mock IHubContext.Clients.Client).
        _facade.SetupSequence(f => f.GetRun(It.IsAny<JobId>()))
            .Returns(run)
            .Returns((PipelineRun?)null);
        // Return an empty backlog so the lines-push block is skipped.
        _facade.Setup(f => f.GetOutputBacklogAsync(It.IsAny<JobId>()))
            .ReturnsAsync((IReadOnlyList<string>)Array.Empty<string>());

        // Build a context with ?agentId=agent-1&agentId=extra — duplicate params.
        // Pre-fix: query["agentId"].ToString() == "agent-1,extra" → mismatch with run.AgentId == "agent-1" → HubException.
        // Post-fix: query["agentId"].FirstOrDefault() == "agent-1" → match → subscription allowed.
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?agentId=agent-1&agentId=extra");

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));

        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns("conn-1");
        mockCtx.Setup(c => c.Features).Returns(features);

        var hub = CreateHub(mockCtx.Object);
        var mockGroups = new Mock<IGroupManager>();
        mockGroups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        hub.Groups = mockGroups.Object;

        var act = () => hub.SubscribeToRun(jobId);
        await act.Should().NotThrowAsync(
            "SubscribeToRun must resolve the first agentId value from a duplicate-param query string and allow the subscription");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private AgentHub CreateHub(HubCallerContext context)
    {
        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: Mock.Of<IChatNotifier>(),
            ChangeNotifier: Mock.Of<IChangeNotifier>(),
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
            IssueOps: Mock.Of<IHubIssueOperations>(),
            LifecycleService: Mock.Of<IAgentJobLifecycleService>(),
            TokenRefreshService: Mock.Of<IAgentTokenRefreshService>(),
            Logger: _logger.Object,
            OrphanRecoveryService: Mock.Of<IAgentOrphanRecoveryService>(),
            UiContext: HubTestHelpers.CreateNoOpHubContext()));
        hub.Context = context;
        return hub;
    }

    private static HubCallerContext BuildContext(string connectionId, string agentId)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = QueryString.Create("agentId", agentId);

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));

        var mock = new Mock<HubCallerContext>();
        mock.Setup(c => c.ConnectionId).Returns(connectionId);
        mock.Setup(c => c.Features).Returns(features);
        return mock.Object;
    }

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public TestHttpContextFeature(HttpContext httpContext) => HttpContext = httpContext;
        public HttpContext? HttpContext { get; set; }
    }
}
