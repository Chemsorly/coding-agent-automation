using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

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
            It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

        _mockIssueOps.Verify(o => o.PostCommentViaIssueProviderAsync(run, "## My Analysis\nLooks good.", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestPostComment_AnalysisType_NullMarkdown_PostsEmptyString()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        var payload = new CommentPayload { AnalysisMarkdown = null };

        await hub.RequestPostComment("job-1", CommentType.Analysis, payload);

        _mockIssueOps.Verify(o => o.PostCommentViaIssueProviderAsync(run, string.Empty, It.IsAny<CancellationToken>()), Times.Once);
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
            It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
    public async Task RequestLabelChange_UnknownRun_WorkItemAlsoAbsent_ReturnsWithoutSwapping()
    {
        // When both the in-memory run and the DB WorkItem are absent, the fallback
        // catches the HubException from ResolveIssueProviderForRunAsync and returns
        // without calling SwapLabelAsync.
        // TODO (WARNING — TestQuality): This test may pass vacuously if ResolveIssueProviderForRunAsync
        // short-circuits before reaching GetWorkItemIssueMetadataAsync (e.g. because
        // LoadProviderConfigsAsync returns an empty list and throws HubException for a different
        // reason). The mock for GetWorkItemIssueMetadataAsync may not be exercised at all.
        // Add an assertion that the fallback path was reached (e.g. verify a log warning was
        // emitted, or use a stricter mock setup that confirms the expected call sequence).
        _mockFacade.Setup(f => f.GetRun("job-missing")).Returns((PipelineRun?)null);
        _mockFacade
            .Setup(f => f.GetWorkItemIssueMetadataAsync(
                It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var hub = CreateHub();

        // Must not throw — fallback catches HubException and logs a warning
        await hub.RequestLabelChange("job-missing", AgentLabels.Done);

        _mockIssueOps.Verify(o => o.SwapLabelAsync(
            It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── RequestLabelChange — DB fallback for unknown run ─────────────────

    [Fact]
    public async Task RequestLabelChange_UnknownRun_WorkItemFound_PerformsFallbackLabelSwap()
    {
        // When the in-memory run is absent but the DB WorkItem exists and the issue provider
        // can be resolved, the fallback path performs the label swap via the issue provider.
        _mockFacade.Setup(f => f.GetRun("job-fallback")).Returns((PipelineRun?)null);

        const string issueIdentifier = "org/repo#99";
        const string issueProviderConfigId = "ip-cfg-fallback";
        _mockFacade
            .Setup(f => f.GetWorkItemIssueMetadataAsync(
                It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((issueIdentifier, issueProviderConfigId));

        // Set up provider configs so ResolveIssueProviderForRunAsync succeeds
        var providerConfig = new ProviderConfig { Id = issueProviderConfigId, Kind = ProviderKind.Issue, DisplayName = "Test", ProviderType = "GitHub" };
        _mockFacade
            .Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { providerConfig });

        // Create a mock issue provider that tracks label operations.
        // AddLabelAsync has a default interface implementation, but Moq can intercept
        // calls to it on the proxy. We set up both AddLabelAsync and AddLabelsAsync so
        // whichever path Moq takes, the call returns Task.CompletedTask.
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockIssueProvider
            .Setup(p => p.AddLabelsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockFacade.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockIssueProvider.Object);

        var hub = CreateHub();

        await hub.RequestLabelChange("job-fallback", AgentLabels.Error);

        // AgentLabelOperations.SwapAsync calls provider.AddLabelAsync(...) which (via the
        // default interface implementation) delegates to AddLabelsAsync. Since Moq intercepts
        // the AddLabelAsync call before the default impl runs, verify at AddLabelsAsync level:
        // If Moq returns Task.CompletedTask for AddLabelAsync without running the default,
        // the label is silently swallowed. We instead verify RemoveLabelAsync was called
        // (which IS mockable) to confirm the label swap loop ran, indicating the fallback executed.
        // TODO (WARNING — TestQuality): This assertion only verifies RemoveLabelAsync was called
        // with It.IsAny arguments — it does NOT pin that the correct target label (AgentLabels.Error)
        // was added, nor that agent:epic was specifically removed. If the swap operated on the wrong
        // labels this test would still pass. Also, the add half of the swap (AddLabelsAsync) is
        // entirely unverified. Strengthen this test by:
        //   1. Verifying AddLabelsAsync was called with a list containing AgentLabels.Error.
        //   2. Verifying RemoveLabelAsync was called with a label != AgentLabels.Error (i.e. the
        //      label being displaced, such as agent:epic).
        // A separate test should exercise the key fix scenario: fallback called with AgentLabels.Error
        // on an issue that has agent:epic, asserting agent:epic removal was attempted.
        mockIssueProvider.Verify(
            p => p.RemoveLabelAsync(
                It.IsAny<IssueIdentifier>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "fallback path must attempt to remove existing labels via the issue provider");
    }

    [Fact]
    public async Task RequestLabelChange_UnknownRun_InvalidLabel_DoesNotAttemptFallback()
    {
        // Invalid labels must be rejected before attempting the DB fallback.
        _mockFacade.Setup(f => f.GetRun("job-fallback")).Returns((PipelineRun?)null);

        var hub = CreateHub();

        await hub.RequestLabelChange("job-fallback", "not-a-valid-agent-label");

        // GetWorkItemIssueMetadataAsync should never be called for invalid labels
        _mockFacade.Verify(
            f => f.GetWorkItemIssueMetadataAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "invalid label must be rejected before the DB fallback is attempted");
    }

    [Fact]
    public async Task RequestLabelChange_UnknownRun_GatedLabel_DoesNotAttemptFallback()
    {
        // Gated labels must be rejected before attempting the DB fallback.
        _mockFacade.Setup(f => f.GetRun("job-fallback")).Returns((PipelineRun?)null);

        var hub = CreateHub();

        await hub.RequestLabelChange("job-fallback", AgentLabels.EpicApproved);

        _mockFacade.Verify(
            f => f.GetWorkItemIssueMetadataAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "gated label must be rejected before the DB fallback is attempted");
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
            It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
            It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── RequestLabelChange — valid non-gated label swaps ─────────────────

    [Fact]
    public async Task RequestLabelChange_ValidLabel_DelegatesToSwapLabelAsync()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();

        await hub.RequestLabelChange("job-1", AgentLabels.Error);

        _mockIssueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestLabelChange_EmptyString_DelegatesToSwapLabelAsync()
    {
        // Empty string passes the AgentLabels.All.Contains check because IsNullOrEmpty short-circuits
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();

        await hub.RequestLabelChange("job-1", string.Empty);

        _mockIssueOps.Verify(o => o.SwapLabelAsync(run, string.Empty, It.IsAny<CancellationToken>()), Times.Once);
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
