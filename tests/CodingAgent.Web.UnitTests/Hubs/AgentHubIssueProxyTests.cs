using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

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
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            Mock.Of<IHubConsolidationOperations>(),
            _mockIssueOps.Object,
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            _mockGateFormatter.Object,
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(), HubTestHelpers.CreateNoOpHubContext()));

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
            .Setup(f => f.GetProviderConfigByIdAsync(configId, ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _mockFacade.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        return (config, mockProvider);
    }

    // ── RequestPostComment — GateRejection and GateWontDo paths ──────────

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

        _mockIssueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.Done), Times.Once);
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
        _mockFacade.Setup(f => f.GetWorkItemIssueMetadataAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssue("job-1", "title", "body", new[] { "label" });
        await act.Should().ThrowAsync<HubException>().WithMessage("*No active run or work item*");
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
        _mockFacade.Setup(f => f.GetWorkItemIssueMetadataAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

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
        mockProvider.Setup(p => p.UpdateCommentAsync(It.IsAny<IssueIdentifier>(), 1L, "updated body", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hub = CreateHub();
        await hub.RequestUpdateComment("job-1", "issue-1", "1", "updated body");

        mockProvider.Verify(p => p.UpdateCommentAsync(It.IsAny<IssueIdentifier>(), 1L, "updated body", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestUpdateComment_ProviderThrows_WrapsAsHubException()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        mockProvider.Setup(p => p.UpdateCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Permission denied"));

        var hub = CreateHub();
        var act = () => hub.RequestUpdateComment("job-1", "issue-1", "1", "body");
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
        _mockFacade
            .Setup(f => f.GetProviderConfigByIdAsync("missing-config", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var hub = CreateHub();
        var act = () => hub.RequestGetIssue("job-1", "42");
        await act.Should().ThrowAsync<HubException>().WithMessage("*missing-config*not found*");
        // TODO: [WARNING] Add a Verify call here to confirm GetProviderConfigByIdAsync was actually
        // invoked with the config ID from the run (IssueProviderConfigId = "missing-config"). Without it,
        // if the production code stopped calling GetProviderConfigByIdAsync or passed a different ID,
        // Moq would silently return null (its default for reference types) and the test would still pass
        // for the wrong reason. Add:
        //   _mockFacade.Verify(f => f.GetProviderConfigByIdAsync("missing-config", ProviderKind.Issue,
        //       It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestCreateIssueForProvider_ProviderInProjectTemplates_Succeeds()
    {
        // Test A: provider belongs to the run's project via a template — allowed
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "own-cfg",
            RepoProviderConfigId = "repo-cfg-1",
            ProjectId = "proj-1"
        };
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

        // The project has a template whose IssueProviderId matches the requested provider
        var template = new PipelineJobTemplate { Id = "tmpl-1", Name = "Cross Repo", IssueProviderId = "cross-repo-cfg", RepoProviderId = "repo-cfg-2" };
        _mockFacade.Setup(f => f.LoadTemplatesForProjectAsync("proj-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        var hub = CreateHub();
        var result = await hub.RequestCreateIssueForProvider("job-1", "cross-repo-cfg", "title", "body", new[] { "bug" });

        result.Should().Be(expected);
        _mockFacade.Verify(f => f.LoadTemplatesForProjectAsync("proj-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestCreateIssueForProvider_OwnProvider_FastPathNoTemplateLookup()
    {
        // Test B: requesting run's own IssueProviderConfigId — fast path, no template lookup
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "own-cfg",
            RepoProviderConfigId = "repo-cfg-1",
            ProjectId = "proj-1"
        };
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var config = new ProviderConfig { Id = "own-cfg", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Own" };
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var expected = new CreatedIssueResult { Identifier = "org/repo#99", Url = "https://example.com/99" };
        mockProvider.Setup(p => p.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { config });
        _mockFacade.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        var hub = CreateHub();
        var result = await hub.RequestCreateIssueForProvider("job-1", "own-cfg", "title", "body", new[] { "bug" });

        result.Should().Be(expected);
        // Fast path: own provider must not trigger template lookup
        _mockFacade.Verify(f => f.LoadTemplatesForProjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestCreateIssueForProvider_ProviderNotInProject_ThrowsHubException()
    {
        // Test C: provider exists system-wide but is not in the run's project — rejected
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "own-cfg",
            RepoProviderConfigId = "repo-cfg-1",
            ProjectId = "proj-1"
        };
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        // The provider exists in the system
        var config = new ProviderConfig { Id = "other-project-cfg", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Other" };
        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { config });

        // But the project's templates only reference own-cfg, not other-project-cfg
        var template = new PipelineJobTemplate { Id = "tmpl-1", Name = "Own Template", IssueProviderId = "own-cfg", RepoProviderId = "repo-cfg-1" };
        _mockFacade.Setup(f => f.LoadTemplatesForProjectAsync("proj-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider("job-1", "other-project-cfg", "title", "body", new[] { "bug" });
        await act.Should().ThrowAsync<HubException>().WithMessage("*not part of the run's project*");
        _mockFacade.Verify(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()), Times.Never);
    }

    [Fact]
    public async Task RequestCreateIssueForProvider_NullProjectId_AnyProviderAccepted()
    {
        // Test D: run.ProjectId is null — backward-compat fallback, any system-wide provider is accepted
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "own-cfg",
            RepoProviderConfigId = "repo-cfg-1",
            ProjectId = null   // legacy run — no project assigned
        };
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var config = new ProviderConfig { Id = "any-other-cfg", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Any" };
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var expected = new CreatedIssueResult { Identifier = "any/repo#5", Url = "https://example.com/5" };
        mockProvider.Setup(p => p.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { config });
        _mockFacade.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        var hub = CreateHub();
        var result = await hub.RequestCreateIssueForProvider("job-1", "any-other-cfg", "title", "body", Array.Empty<string>());

        result.Should().Be(expected);
        // Null ProjectId must never trigger template lookup
        _mockFacade.Verify(f => f.LoadTemplatesForProjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── ResolveIssueProviderForRunAsync — Warning logged before HubException ──

    /// <summary>
    /// AC1: When run is not found, a Warning is emitted before throwing HubException,
    /// so the cross-replica state miss is visible in API logs.
    /// Moq verification targets the typed generic overload Warning(string, T) —
    /// NOT the extension method (which cannot be intercepted by Moq).
    /// </summary>
    // TODO: Moq generic overload resolution for Serilog ILogger.Warning is fragile. If Serilog
    // declares both Warning(string, object) and Warning<T>(string, T), Moq may match the non-generic
    // object overload instead of Warning<string>, causing Verify to fail with "never invoked" even
    // though the warning was emitted — or vice versa. If these tests produce false-green results
    // after a Serilog upgrade or call-site type change, review which overload is being bound.
    [Fact]
    public async Task RequestGetIssue_RunNotFound_LogsWarningBeforeThrowingHubException()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetWorkItemIssueMetadataAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var hub = CreateHub();
        var act = () => hub.RequestGetIssue("job-1", "42");

        await act.Should().ThrowAsync<HubException>().WithMessage("*No active run or work item*");

        // Warning(string messageTemplate, T propertyValue) — "... {JobId} ..." with string jobId
        // TODO: [WARNING] The assertion predicate was relaxed from
        //   s.Contains("no active run") && s.Contains("{JobId}") + Times.Once
        // to just s.Contains("{JobId}") + Times.AtLeastOnce because the DB fallback path now emits
        // two Warning(string, string) calls before throwing. The current predicate matches any
        // Warning call that has a {JobId} parameter, including the "attempting DB fallback" line.
        // If the terminal "no active run or work item" warning were accidentally removed, this
        // test would still pass because the first warning also contains {JobId}. Consider tightening
        // to Times.Exactly(2) and verifying that at least one call contains "no active run or work item"
        // (or splitting into two focused log-assertion tests) to restore the original contract.
        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("{JobId}")),
                It.Is<string>(s => s == "job-1")),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// AC1: When issue provider config is not found, a Warning is emitted before throwing HubException,
    /// so the missing-config failure is visible in API logs.
    /// Moq verification targets the typed generic overload Warning(string, T0, T1).
    /// </summary>
    // TODO: Same Moq generic overload resolution fragility as the run-not-found test above.
    // The two-arg Warning(string, T0, T1) overload matching depends on Serilog's ILogger generic
    // interface structure. A Serilog upgrade or parameter type change could silently affect
    // which overload is bound. Argument order is correct (configId first, jobId second) but
    // should be re-validated if tests start producing unexpected verification failures.
    [Fact]
    public async Task RequestGetIssue_ProviderConfigNotFound_LogsWarningBeforeThrowingHubException()
    {
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "missing-config",
            RepoProviderConfigId = "repo-cfg-1"
        };
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade
            .Setup(f => f.GetProviderConfigByIdAsync("missing-config", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null); // no matching config

        var hub = CreateHub();
        var act = () => hub.RequestGetIssue("job-1", "42");

        await act.Should().ThrowAsync<HubException>().WithMessage("*missing-config*");

        // Error(string messageTemplate, T0 propertyValue0, T1 propertyValue1) —
        // "... {IssueProviderConfigId} ... {JobId}" with string configId, string jobId
        _mockLogger.Verify(
            l => l.Error(
                It.Is<string>(s => s.Contains("{IssueProviderConfigId}") && s.Contains("{JobId}")),
                It.Is<string>(s => s == "missing-config"),
                It.Is<string>(s => s == "job-1")),
            Times.Once);
        // TODO: [WARNING] Add a Verify call here to confirm GetProviderConfigByIdAsync was actually
        // invoked with the config ID from the run (IssueProviderConfigId = "missing-config"). Without it,
        // if the production code stopped calling GetProviderConfigByIdAsync or passed a different ID,
        // Moq would silently return null (its default for reference types) and the test would still pass
        // for the wrong reason. Add:
        //   _mockFacade.Verify(f => f.GetProviderConfigByIdAsync("missing-config", ProviderKind.Issue,
        //       It.IsAny<CancellationToken>()), Times.Once);
    }
    /// </summary>
    [Fact]
    public async Task RequestListOpenIssues_RunNotFound_LogsWarningBeforeThrowingHubException()
    {
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetWorkItemIssueMetadataAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var hub = CreateHub();
        var act = () => hub.RequestListOpenIssues("job-1", 1, 25, null);

        await act.Should().ThrowAsync<HubException>();

        // TODO: [WARNING] Same weakened assertion as RequestGetIssue_RunNotFound_LogsWarning — the
        // predicate s.Contains("{JobId}") + Times.AtLeastOnce matches any Warning with a {JobId}
        // parameter, including the "attempting DB fallback" line. The terminal "no active run or
        // work item" warning could be silently dropped without this test failing. Tighten to
        // Times.Exactly(2) or add a check for the specific terminal message (e.g.
        // s.Contains("no active run or work item")) to restore the original contract strength.
        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("{JobId}")),
                It.Is<string>(s => s == "job-1")),
            Times.AtLeastOnce);
    }

    // ── ResolveIssueProviderForRunAsync — DB fallback ─────────────────────

    /// <summary>
    /// When GetRun returns null but the WorkItem exists in the database, the DB fallback must
    /// succeed and the operation must complete as if the run were in memory.
    /// Covers cross-replica state misses and mid-run kiro-cli sub-process restarts.
    /// </summary>
    [Fact]
    public async Task RequestGetIssue_RunNull_DbFallbackSucceeds_ReturnsIssueDetail()
    {
        // No in-memory run
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        // DB fallback returns metadata with IssueProviderConfigId
        _mockFacade.Setup(f => f.GetWorkItemIssueMetadataAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("org/repo#42", "issue-cfg-1"));

        var (_, mockProvider) = SetupIssueProvider("issue-cfg-1");
        var expected = new IssueDetail
        {
            Identifier = "42",
            Title = "Issue via DB fallback",
            Description = "Resolved from WorkItem metadata",
            Labels = Array.Empty<string>()
        };
        mockProvider.Setup(p => p.GetIssueAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var hub = CreateHub();
        var result = await hub.RequestGetIssue("job-1", "42");

        result.Should().Be(expected, "DB fallback must resolve the provider and complete the operation");
        // TODO: [WARNING] Both the Setup and the Verify use It.IsAny<JobId>() — if the production
        // code passes a different job ID to GetWorkItemIssueMetadataAsync (e.g. a corrupted value
        // or a hardcoded default), the test still passes. Tighten to
        //   It.Is<JobId>(j => j.Value == "job-1")
        // in both Setup and Verify to lock in that the correct JobId is forwarded to the fallback.
        _mockFacade.Verify(
            f => f.GetWorkItemIssueMetadataAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "GetWorkItemIssueMetadataAsync must be called exactly once when GetRun returns null");
    }

    /// <summary>
    /// When both GetRun and the DB fallback return null, a HubException must be thrown with a
    /// message indicating that neither the run nor the WorkItem could be found.
    /// </summary>
    [Fact]
    public async Task RequestGetIssue_RunNull_DbFallbackReturnsNull_ThrowsHubException()
    {
        // No in-memory run
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns((PipelineRun?)null);

        // DB fallback also returns null — no WorkItem exists
        _mockFacade.Setup(f => f.GetWorkItemIssueMetadataAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var hub = CreateHub();
        var act = () => hub.RequestGetIssue("job-1", "42");

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*No active run or work item*",
                "the error message must reflect that both the run and the DB fallback were exhausted");

        _mockFacade.Verify(
            f => f.GetWorkItemIssueMetadataAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "GetWorkItemIssueMetadataAsync must be attempted before throwing");
    }

    // ── Outer catch with context — RequestListOpenIssues / Closed / Comments ──

    [Fact]
    public async Task RequestListOpenIssues_ProviderThrows_WrapsWithContextualHubException()
    {
        // Arrange: provider resolves successfully but throws during list call.
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        mockProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("upstream 503"));

        var hub = CreateHub();
        var act = () => hub.RequestListOpenIssues("job-1", 1, 25, null);

        // HubException wraps the provider error AND includes job/page context
        var ex = await act.Should().ThrowAsync<HubException>();
        ex.WithMessage("*RequestListOpenIssues*job-1*");
        ex.Which.InnerException.Should().BeOfType<HubException>(
            "inner exception is the HubException from ExecuteWithIssueProviderAsync which already logs at Error");
    }

    [Fact]
    public async Task RequestListClosedIssues_ProviderThrows_WrapsWithContextualHubException()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        mockProvider.Setup(p => p.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("upstream 503"));

        var hub = CreateHub();
        var act = () => hub.RequestListClosedIssues("job-1", 1, 25, null, null);

        var ex = await act.Should().ThrowAsync<HubException>();
        ex.WithMessage("*RequestListClosedIssues*job-1*");
    }

    [Fact]
    public async Task RequestListComments_ProviderThrows_WrapsWithContextualHubException()
    {
        var run = CreateRun();
        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        var (_, mockProvider) = SetupIssueProvider();
        mockProvider.Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("upstream 503"));

        var hub = CreateHub();
        var act = () => hub.RequestListComments("job-1", "42");

        var ex = await act.Should().ThrowAsync<HubException>();
        ex.WithMessage("*RequestListComments*job-1*");
    }
}
