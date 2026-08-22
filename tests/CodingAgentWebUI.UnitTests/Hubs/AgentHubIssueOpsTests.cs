using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Tests for AgentHub.IssueOps.cs methods not covered by AgentHubIssueProxyTests.
/// Covers: RequestPostComment unknown run / unknown CommentType early returns,
/// RequestLabelChange unknown run / invalid label / gated label early returns,
/// and RequestTokenRefresh delegation.
/// </summary>
public sealed class AgentHubIssueOpsTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<IGateCommentFormatter> _mockGateFormatter = new();
    private readonly Mock<IHubIssueOperations> _mockIssueOps = new();
    private readonly Mock<IAgentTokenRefreshService> _mockTokenRefresh = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private AgentHub CreateHub(string connectionId = "conn-1")
    {
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            Mock.Of<IHubConsolidationOperations>(),
            _mockIssueOps.Object,
            Mock.Of<IAgentJobLifecycleService>(),
            _mockTokenRefresh.Object,
            _mockGateFormatter.Object,
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(),
            HubTestHelpers.CreateNoOpHubContext()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = mockContext.Object;

        return hub;
    }

    private static PipelineRun CreateRun(string jobId = "job-1") => new()
    {
        RunId = jobId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1"
    };

    // ── RequestPostComment — null payload ────────────────────────────────

    [Fact]
    public async Task RequestPostComment_NullPayload_Throws()
    {
        var hub = CreateHub();
        var act = async () => await hub.RequestPostComment("job-1", CommentType.Analysis, null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── RequestPostComment — unknown run early return ────────────────────

    [Fact]
    public async Task RequestPostComment_UnknownRun_ReturnsEarlyWithoutPosting()
    {
        _mockFacade.Setup(f => f.GetRun("job-missing")).Returns((PipelineRun?)null);

        var hub = CreateHub();
        var payload = new CommentPayload { AnalysisMarkdown = "## Analysis" };

        // Must not throw — unknown run is a warning + early return
        await hub.RequestPostComment("job-missing", CommentType.Analysis, payload);

        _mockIssueOps.Verify(o => o.PostCommentViaIssueProviderAsync(
            It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    // ── RequestPostComment — Analysis CommentType ────────────────────────

    [Fact]
    public async Task RequestPostComment_AnalysisType_PostsMarkdownBody()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        var payload = new CommentPayload { AnalysisMarkdown = "## My Analysis\nLooks good." };

        await hub.RequestPostComment("job-1", CommentType.Analysis, payload);

        _mockIssueOps.Verify(o => o.PostCommentViaIssueProviderAsync(run, "## My Analysis\nLooks good."), Times.Once);
    }

    [Fact]
    public async Task RequestPostComment_AnalysisType_NullMarkdown_PostsEmptyString()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        var payload = new CommentPayload { AnalysisMarkdown = null };

        await hub.RequestPostComment("job-1", CommentType.Analysis, payload);

        _mockIssueOps.Verify(o => o.PostCommentViaIssueProviderAsync(run, string.Empty), Times.Once);
    }

    // ── RequestPostComment — unknown CommentType early return ────────────

    [Fact]
    public async Task RequestPostComment_UnknownCommentType_ReturnsEarlyWithoutPosting()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        var payload = new CommentPayload { AnalysisMarkdown = "body" };

        // Cast a value that is not a defined CommentType enum member
        await hub.RequestPostComment("job-1", (CommentType)999, payload);

        _mockIssueOps.Verify(o => o.PostCommentViaIssueProviderAsync(
            It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
        _mockGateFormatter.Verify(f => f.FormatGateComment(
            It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    // ── RequestLabelChange — null label throws ───────────────────────────

    [Fact]
    public async Task RequestLabelChange_NullLabel_Throws()
    {
        var hub = CreateHub();
        var act = async () => await hub.RequestLabelChange("job-1", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── RequestLabelChange — unknown run early return ────────────────────

    [Fact]
    public async Task RequestLabelChange_UnknownRun_ReturnsEarlyWithoutSwapping()
    {
        _mockFacade.Setup(f => f.GetRun("job-missing")).Returns((PipelineRun?)null);

        var hub = CreateHub();

        await hub.RequestLabelChange("job-missing", AgentLabels.Done);

        _mockIssueOps.Verify(o => o.SwapLabelAsync(
            It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    // ── RequestLabelChange — invalid label ───────────────────────────────

    [Fact]
    public async Task RequestLabelChange_InvalidLabel_ReturnsEarlyWithoutSwapping()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();

        await hub.RequestLabelChange("job-1", "invalid:not-a-real-label");

        _mockIssueOps.Verify(o => o.SwapLabelAsync(
            It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    // ── RequestLabelChange — gated label ─────────────────────────────────

    [Fact]
    public async Task RequestLabelChange_GatedLabel_ReturnsEarlyWithoutSwapping()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();

        // agent:epic-approved is the only dispatch-gated label — must be rejected by the hub
        await hub.RequestLabelChange("job-1", AgentLabels.EpicApproved);

        _mockIssueOps.Verify(o => o.SwapLabelAsync(
            It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    // ── RequestLabelChange — valid non-gated label swaps ─────────────────

    [Fact]
    public async Task RequestLabelChange_ValidLabel_DelegatesToSwapLabelAsync()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();

        await hub.RequestLabelChange("job-1", AgentLabels.Error);

        _mockIssueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Error), Times.Once);
    }

    [Fact]
    public async Task RequestLabelChange_EmptyString_DelegatesToSwapLabelAsync()
    {
        // Empty string passes the AgentLabels.All.Contains check because IsNullOrEmpty short-circuits
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();

        await hub.RequestLabelChange("job-1", string.Empty);

        _mockIssueOps.Verify(o => o.SwapLabelAsync(run, string.Empty), Times.Once);
    }

    // ── RequestTokenRefresh — delegates to token refresh service ─────────

    [Fact]
    public async Task RequestTokenRefresh_DelegatesToTokenRefreshService()
    {
        var expectedResponse = new TokenRefreshResponse
        {
            Token = "fresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        _mockTokenRefresh
            .Setup(s => s.RefreshTokenAsync("job-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var hub = CreateHub();

        var result = await hub.RequestTokenRefresh("job-1", ProviderKind.Repository);

        result.Should().Be(expectedResponse);
        _mockTokenRefresh.Verify(s =>
            s.RefreshTokenAsync("job-1", ProviderKind.Repository, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestTokenRefresh_IssueProviderKind_DelegatesToTokenRefreshService()
    {
        var expectedResponse = new TokenRefreshResponse
        {
            Token = "issue-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        _mockTokenRefresh
            .Setup(s => s.RefreshTokenAsync("job-2", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var hub = CreateHub();

        var result = await hub.RequestTokenRefresh("job-2", ProviderKind.Issue);

        result.Should().Be(expectedResponse);
    }
}
