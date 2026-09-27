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
            GateCommentFormatter: Mock.Of<IGateCommentFormatter>(),
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
