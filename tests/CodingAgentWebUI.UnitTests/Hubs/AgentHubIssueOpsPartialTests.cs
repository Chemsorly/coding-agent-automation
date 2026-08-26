using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Tests for AgentHub.IssueOps.cs covering:
/// - RequestPostComment: unknown run, all CommentType branches, unknown type, null payload
/// - RequestLabelChange: unknown run, invalid label, gated label, valid label
/// - RequestTokenRefresh: delegation to token refresh service
/// </summary>
public sealed class AgentHubIssueOpsPartialTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IHubIssueOperations> _issueOps = new();
    private readonly Mock<IAgentTokenRefreshService> _tokenRefreshService = new();
    private readonly Mock<IGateCommentFormatter> _gateCommentFormatter = new();

    private AgentHub CreateHub(string connectionId = "conn-1")
    {
        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns(connectionId);

        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: Mock.Of<IChatNotifier>(),
            ChangeNotifier: Mock.Of<IChangeNotifier>(),
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
            IssueOps: _issueOps.Object,
            LifecycleService: Mock.Of<IAgentJobLifecycleService>(),
            TokenRefreshService: _tokenRefreshService.Object,
            GateCommentFormatter: _gateCommentFormatter.Object,
            Logger: Log.Logger,
            OrphanRecoveryService: Mock.Of<IAgentOrphanRecoveryService>(),
            UiContext: HubTestHelpers.CreateNoOpHubContext()));

        hub.Context = mockCtx.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

        return hub;
    }

    private static PipelineRun CreateRun(string runId = "job-1") => new()
    {
        RunId = runId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test issue",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1"
    };

    // ── RequestPostComment — null run (returns silently) ──────────────────

    [Fact]
    public async Task RequestPostComment_UnknownRun_ReturnsSilently()
    {
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);

        var hub = CreateHub();
        var act = () => hub.RequestPostComment(
            new JobId("ghost-job"), CommentType.Analysis,
            new CommentPayload { AnalysisMarkdown = "some text" });

        await act.Should().NotThrowAsync("unknown run must be silently ignored");
        _issueOps.Verify(o => o.PostCommentViaIssueProviderAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    // ── RequestPostComment — CommentType.Analysis ─────────────────────────

    [Fact]
    public async Task RequestPostComment_AnalysisType_UsesAnalysisMarkdown()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        _issueOps.Setup(o => o.PostCommentViaIssueProviderAsync(run, It.IsAny<string>()))
                 .ReturnsAsync((string?)null);

        var hub = CreateHub();
        await hub.RequestPostComment(
            new JobId("job-1"), CommentType.Analysis,
            new CommentPayload { AnalysisMarkdown = "# Analysis\nContent here" });

        _issueOps.Verify(
            o => o.PostCommentViaIssueProviderAsync(run, "# Analysis\nContent here"),
            Times.Once);
    }

    [Fact]
    public async Task RequestPostComment_AnalysisType_NullMarkdown_UsesEmptyString()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        _issueOps.Setup(o => o.PostCommentViaIssueProviderAsync(run, It.IsAny<string>()))
                 .ReturnsAsync((string?)null);

        var hub = CreateHub();
        await hub.RequestPostComment(
            new JobId("job-1"), CommentType.Analysis,
            new CommentPayload { AnalysisMarkdown = null });

        _issueOps.Verify(
            o => o.PostCommentViaIssueProviderAsync(run, string.Empty),
            Times.Once);
    }

    // ── RequestPostComment — CommentType.GateRejection ───────────────────

    [Fact]
    public async Task RequestPostComment_GateRejection_UsesFormatterWithIsWontDoFalse()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        _gateCommentFormatter.Setup(f => f.FormatGateComment(It.IsAny<string?>(), false))
                              .Returns("formatted-rejection");
        _issueOps.Setup(o => o.PostCommentViaIssueProviderAsync(run, "formatted-rejection"))
                 .ReturnsAsync((string?)null);

        var hub = CreateHub();
        await hub.RequestPostComment(
            new JobId("job-1"), CommentType.GateRejection,
            new CommentPayload { AssessmentJson = "{}" });

        _gateCommentFormatter.Verify(f => f.FormatGateComment("{}", false), Times.Once);
        _issueOps.Verify(o => o.PostCommentViaIssueProviderAsync(run, "formatted-rejection"), Times.Once);
    }

    // ── RequestPostComment — CommentType.GateWontDo ──────────────────────

    [Fact]
    public async Task RequestPostComment_GateWontDo_UsesFormatterWithIsWontDoTrue()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        _gateCommentFormatter.Setup(f => f.FormatGateComment(It.IsAny<string?>(), true))
                              .Returns("formatted-wontdo");
        _issueOps.Setup(o => o.PostCommentViaIssueProviderAsync(run, "formatted-wontdo"))
                 .ReturnsAsync((string?)null);

        var hub = CreateHub();
        await hub.RequestPostComment(
            new JobId("job-1"), CommentType.GateWontDo,
            new CommentPayload { AssessmentJson = "{wont}" });

        _gateCommentFormatter.Verify(f => f.FormatGateComment("{wont}", true), Times.Once);
        _issueOps.Verify(o => o.PostCommentViaIssueProviderAsync(run, "formatted-wontdo"), Times.Once);
    }

    // ── RequestPostComment — unknown CommentType → silent return ──────────

    [Fact]
    public async Task RequestPostComment_UnknownCommentType_ReturnsSilently()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);

        var hub = CreateHub();
        var act = () => hub.RequestPostComment(
            new JobId("job-1"), (CommentType)999,
            new CommentPayload());

        await act.Should().NotThrowAsync("unknown comment type must be silently ignored");
        _issueOps.Verify(o => o.PostCommentViaIssueProviderAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RequestPostComment_NullPayload_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestPostComment(new JobId("job-1"), CommentType.Analysis, null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── RequestLabelChange — unknown run → silent return ──────────────────

    [Fact]
    public async Task RequestLabelChange_UnknownRun_ReturnsSilently()
    {
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);

        var hub = CreateHub();
        var act = () => hub.RequestLabelChange(new JobId("ghost"), "agent:done");

        await act.Should().NotThrowAsync("unknown run must be silently ignored");
        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    // ── RequestLabelChange — invalid label → silent return ────────────────

    [Fact]
    public async Task RequestLabelChange_InvalidLabel_ReturnsSilently()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);

        var hub = CreateHub();
        var act = () => hub.RequestLabelChange(new JobId("job-1"), "hacker:label");

        await act.Should().NotThrowAsync("invalid label must be silently rejected");
        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    // ── RequestLabelChange — gated label → silent return ─────────────────

    [Fact]
    public async Task RequestLabelChange_GatedLabel_ReturnsSilently()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);

        var hub = CreateHub();
        var act = () => hub.RequestLabelChange(new JobId("job-1"), AgentLabels.EpicApproved);

        await act.Should().NotThrowAsync("gated label must be silently rejected");
        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never);
    }

    // ── RequestLabelChange — valid, non-gated label → delegates to issueOps

    [Fact]
    public async Task RequestLabelChange_ValidLabel_DelegatesToIssueOps()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(run, AgentLabels.Done))
                 .Returns(Task.CompletedTask);

        var hub = CreateHub();
        await hub.RequestLabelChange(new JobId("job-1"), AgentLabels.Done);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done), Times.Once);
    }

    [Fact]
    public async Task RequestLabelChange_NullLabel_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestLabelChange(new JobId("job-1"), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── RequestTokenRefresh — delegates to service ────────────────────────

    [Fact]
    public async Task RequestTokenRefresh_IssueProvider_DelegatesToService()
    {
        var expected = new TokenRefreshResponse
        {
            Token = "fresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        _tokenRefreshService
            .Setup(s => s.RefreshTokenAsync("job-1", ProviderKind.Issue, CancellationToken.None))
            .ReturnsAsync(expected);

        var hub = CreateHub();
        var result = await hub.RequestTokenRefresh(new JobId("job-1"), ProviderKind.Issue);

        result.Should().Be(expected);
        _tokenRefreshService.Verify(
            s => s.RefreshTokenAsync("job-1", ProviderKind.Issue, CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task RequestTokenRefresh_AgentProvider_DelegatesToService()
    {
        var expected = new TokenRefreshResponse
        {
            Token = "agent-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        _tokenRefreshService
            .Setup(s => s.RefreshTokenAsync("job-2", ProviderKind.Agent, CancellationToken.None))
            .ReturnsAsync(expected);

        var hub = CreateHub();
        var result = await hub.RequestTokenRefresh(new JobId("job-2"), ProviderKind.Agent);

        result.Token.Should().Be("agent-token");
    }
}
