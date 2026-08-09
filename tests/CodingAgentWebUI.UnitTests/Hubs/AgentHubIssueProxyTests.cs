using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Tests for AgentHub issue-provider proxy methods (RequestCreateIssue, RequestListOpenIssues,
/// RequestListClosedIssues, RequestGetIssue, RequestListComments, RequestUpdateComment,
/// RequestCreateIssueForProvider) and the RequestPostComment GateRejection/GateWontDo paths.
/// These methods route through ExecuteWithIssueProviderAsync and were previously uncovered.
/// </summary>
public sealed class AgentHubIssueProxyTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<IGateCommentFormatter> _mockGateFormatter = new();
    private readonly Mock<IHubIssueOperations> _mockIssueOps = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private AgentHub CreateHub(string connectionId = "conn-1")
    {
        var hub = new AgentHub(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            null!,
            Mock.Of<IConsolidationService>(),
            new ConsolidationBadgeService(),
            _mockIssueOps.Object,
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            _mockGateFormatter.Object,
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>());

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

    private (ProviderConfig Config, Mock<IIssueProvider> Provider) SetupIssueProvider(string configId = "issue-cfg-1")
    {
        var config = new ProviderConfig
        {
            Id = configId,
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test"
        };
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockFacade
            .Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { config });
        _mockFacade.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        return (config, mockProvider);
    }

    // ── RequestPostComment — GateRejection and GateWontDo paths ──────────

    // TODO: [WARNING] These tests and others in this file call _mockFacade.Setup(f => f.GetRun("job-1"))
    // using raw string literals, relying on the implicit string→JobId conversion for Moq matching.
    // Since JobId is a record struct with value equality on .Value, Moq matches correctly — but if
    // GetRun's parameter type reverted to string, these tests would still compile and pass, making
    // the type constraint non-load-bearing. Consider using _mockFacade.Setup(f => f.GetRun(new JobId("job-1")))
    // to enforce the type boundary at the test level.
    // See: review-findings.md [WARNING] AgentHubIssueProxyTests.cs:130
    [Fact]
    public async Task RequestPostComment_GateRejection_FormatsAndPosts()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockGateFormatter.Setup(f => f.FormatGateComment(It.IsAny<string?>(), false)).Returns("formatted-rejection");

        var hub = CreateHub();
        var payload = new CommentPayload { AssessmentJson = "{}" };
        await hub.RequestPostComment("job-1", CommentType.GateRejection, payload);

        _mockGateFormatter.Verify(f => f.FormatGateComment("{}", false), Times.Once);
        _mockIssueOps.Verify(o => o.PostCommentViaIssueProviderAsync(run, "formatted-rejection"), Times.Once);
    }

    [Fact]
    public async Task RequestPostComment_GateWontDo_FormatsAndPosts()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockGateFormatter.Setup(f => f.FormatGateComment(It.IsAny<string?>(), true)).Returns("formatted-wontdo");

        var hub = CreateHub();
        var payload = new CommentPayload { AssessmentJson = "{\"verdict\":\"wont-do\"}" };
        await hub.RequestPostComment("job-1", CommentType.GateWontDo, payload);

        _mockGateFormatter.Verify(f => f.FormatGateComment("{\"verdict\":\"wont-do\"}", true), Times.Once);
        _mockIssueOps.Verify(o => o.PostCommentViaIssueProviderAsync(run, "formatted-wontdo"), Times.Once);
    }

    // ── RequestLabelChange — logs before swap ─────────────────────────────

    [Fact]
    public async Task RequestLabelChange_ValidLabel_LogsAndSwaps()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var hub = CreateHub();
        await hub.RequestLabelChange("job-1", AgentLabels.Done);

        _mockIssueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done, It.IsAny<LabelTargetKind>()), Times.Once);
    }

    // ── RequestCreateIssue ────────────────────────────────────────────────

    [Fact]
    public async Task RequestCreateIssue_NullTitle_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestCreateIssue("job-1", null!, "body", new[] { "label" });
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestCreateIssue_NullBody_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestCreateIssue("job-1", "title", null!, new[] { "label" });
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestCreateIssue_NullLabels_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestCreateIssue("job-1", "title", "body", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestCreateIssue_NoRun_ThrowsHubException()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssue("job-1", "title", "body", new[] { "label" });
        await act.Should().ThrowAsync<HubException>().WithMessage("*No active run*");
    }

    [Fact]
    public async Task RequestCreateIssue_Success_ReturnsCreatedIssue()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        var expected = new CreatedIssueResult { Identifier = "org/repo#99", Url = "https://github.com/org/repo/issues/99" };
        mockProvider.Setup(p => p.CreateIssueAsync("title", "body", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var hub = CreateHub();
        var result = await hub.RequestCreateIssue("job-1", "title", "body", new[] { "enhancement" });

        result.Should().Be(expected);
    }

    [Fact]
    public async Task RequestCreateIssue_ProviderThrows_WrapsAsHubException()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        mockProvider.Setup(p => p.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API rate limit"));

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssue("job-1", "title", "body", Array.Empty<string>());
        await act.Should().ThrowAsync<HubException>().WithMessage("*create issue*");
    }

    // ── RequestListOpenIssues ─────────────────────────────────────────────

    [Fact]
    public async Task RequestListOpenIssues_Success_ReturnsList()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        var expected = new PagedResult<IssueSummary>
        {
            Items = new List<IssueSummary>(),
            Page = 1,
            PageSize = 25,
            HasMore = false
        };
        mockProvider.Setup(p => p.ListOpenIssuesAsync(1, 25, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var hub = CreateHub();
        var result = await hub.RequestListOpenIssues("job-1", 1, 25, null);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task RequestListOpenIssues_NoRun_ThrowsHubException()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var hub = CreateHub();
        var act = () => hub.RequestListOpenIssues("job-1", 1, 25, null);
        await act.Should().ThrowAsync<HubException>();
    }

    // ── RequestListClosedIssues ───────────────────────────────────────────

    [Fact]
    public async Task RequestListClosedIssues_Success_ReturnsList()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        var expected = new PagedResult<IssueSummary>
        {
            Items = new List<IssueSummary>(),
            Page = 1,
            PageSize = 25,
            HasMore = false
        };
        mockProvider.Setup(p => p.ListClosedIssuesAsync(1, 25, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var hub = CreateHub();
        var result = await hub.RequestListClosedIssues("job-1", 1, 25, null, null);

        result.Should().Be(expected);
    }

    // ── RequestGetIssue ───────────────────────────────────────────────────

    [Fact]
    public async Task RequestGetIssue_NullIdentifier_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestGetIssue("job-1", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestGetIssue_Success_ReturnsDetail()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        var expected = new IssueDetail
        {
            Identifier = "42",
            Title = "Test",
            Description = "Test description",
            Labels = Array.Empty<string>()
        };
        mockProvider.Setup(p => p.GetIssueAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var hub = CreateHub();
        var result = await hub.RequestGetIssue("job-1", "42");

        result.Should().Be(expected);
    }

    [Fact]
    public async Task RequestGetIssue_ProviderThrows_WrapsAsHubException()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        mockProvider.Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Not found"));

        var hub = CreateHub();
        var act = () => hub.RequestGetIssue("job-1", "99");
        await act.Should().ThrowAsync<HubException>().WithMessage("*get issue*");
    }

    // ── RequestListComments ───────────────────────────────────────────────

    [Fact]
    public async Task RequestListComments_NullIdentifier_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestListComments("job-1", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestListComments_Success_ReturnsComments()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        IReadOnlyList<IssueComment> comments = new List<IssueComment>
        {
            new() { Id = "c1", Author = "user1", Body = "First comment", CreatedAt = DateTime.UtcNow }
        };
        mockProvider.Setup(p => p.ListCommentsAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(comments);

        var hub = CreateHub();
        var result = await hub.RequestListComments("job-1", "42");

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("c1");
    }

    // ── RequestUpdateComment ──────────────────────────────────────────────

    [Fact]
    public async Task RequestUpdateComment_NullIssueId_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestUpdateComment("job-1", null!, "comment-1", "body");
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestUpdateComment_NullCommentId_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestUpdateComment("job-1", "issue-1", null!, "body");
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestUpdateComment_NullBody_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestUpdateComment("job-1", "issue-1", "comment-1", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestUpdateComment_Success_CallsProvider()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        mockProvider.Setup(p => p.UpdateCommentAsync(It.IsAny<IssueIdentifier>(), "comment-1", "updated body", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub();
        await hub.RequestUpdateComment("job-1", "issue-1", "comment-1", "updated body");

        mockProvider.Verify(p => p.UpdateCommentAsync(It.IsAny<IssueIdentifier>(), "comment-1", "updated body", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestUpdateComment_ProviderThrows_WrapsAsHubException()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        mockProvider.Setup(p => p.UpdateCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Permission denied"));

        var hub = CreateHub();
        var act = () => hub.RequestUpdateComment("job-1", "issue-1", "comment-1", "body");
        await act.Should().ThrowAsync<HubException>().WithMessage("*update comment*");
    }

    // ── RequestCreateIssueForProvider ─────────────────────────────────────

    [Fact]
    public async Task RequestCreateIssueForProvider_NullConfigId_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider("job-1", null!, "title", "body", new[] { "label" });
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestCreateIssueForProvider_NoRun_ThrowsHubException()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider("job-1", "cfg-x", "title", "body", new[] { "label" });
        await act.Should().ThrowAsync<HubException>().WithMessage("*No active run*");
    }

    [Fact]
    public async Task RequestCreateIssueForProvider_ConfigNotFound_ThrowsHubException()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>()); // no matching config

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider("job-1", "nonexistent-cfg", "title", "body", new[] { "label" });
        await act.Should().ThrowAsync<HubException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task RequestCreateIssueForProvider_Success_ReturnsCreatedIssue()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var config = new ProviderConfig { Id = "cross-repo-cfg", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Cross" };
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var expected = new CreatedIssueResult { Identifier = "other/repo#10", Url = "https://example.com/10" };
        mockProvider.Setup(p => p.CreateIssueAsync("title", "body", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { config });
        _mockFacade.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        var hub = CreateHub();
        var result = await hub.RequestCreateIssueForProvider("job-1", "cross-repo-cfg", "title", "body", new[] { "bug" });

        result.Should().Be(expected);
    }

    [Fact]
    public async Task RequestCreateIssueForProvider_ProviderThrows_WrapsAsHubException()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var config = new ProviderConfig { Id = "cfg-fail", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Fail" };
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockProvider.Setup(p => p.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Service unavailable"));

        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { config });
        _mockFacade.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider("job-1", "cfg-fail", "title", "body", new[] { "bug" });
        await act.Should().ThrowAsync<HubException>().WithMessage("*Failed to create issue*");
    }

    // ── ExecuteWithIssueProviderAsync — no provider config found ─────────

    [Fact]
    public async Task RequestGetIssue_NoProviderConfig_ThrowsHubException()
    {
        // Use a run with a config ID that has no matching provider config
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "missing-config",
            RepoProviderConfigId = "repo-cfg-1"
        };
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>()); // no matching config

        var hub = CreateHub();
        var act = () => hub.RequestGetIssue("job-1", "42");
        await act.Should().ThrowAsync<HubException>().WithMessage("*missing-config*not found*");
    }
}
