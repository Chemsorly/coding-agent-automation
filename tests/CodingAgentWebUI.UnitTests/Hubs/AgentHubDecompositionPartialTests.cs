using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Tests for AgentHub.Decomposition.cs covering:
/// - RequestCreateIssue: null-arg guards throw
/// - RequestCreateIssueForProvider: run-not-found, provider-not-found, scope-check variants
/// - RequestListOpenIssues / RequestGetIssue / RequestListComments / RequestUpdateComment: null-arg guards
/// </summary>
public sealed class AgentHubDecompositionPartialTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();

    private AgentHub CreateHub(string connectionId = "conn-1")
    {
        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns(connectionId);

        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: Mock.Of<IChatNotifier>(),
            ChangeNotifier: Mock.Of<IChangeNotifier>(),
            ModelFetchService: null!,  // ModelFetchService is sealed — pass null
            ConsolidationService: Mock.Of<IConsolidationService>(),
            BadgeService: new ConsolidationBadgeService(),
            IssueOps: Mock.Of<IHubIssueOperations>(),
            LifecycleService: Mock.Of<IAgentJobLifecycleService>(),
            TokenRefreshService: Mock.Of<IAgentTokenRefreshService>(),
            GateCommentFormatter: Mock.Of<IGateCommentFormatter>(),
            OrphanRecoveryService: Mock.Of<IAgentOrphanRecoveryService>(),
            Logger: Log.Logger,
            UiContext: HubTestHelpers.CreateNoOpHubContext()));

        hub.Context = mockCtx.Object;
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    private static PipelineRun CreateRun(
        string runId = "job-1",
        string projectId = "proj-A",
        string issueProviderConfigId = "ip-1") => new()
    {
        RunId = runId,
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = issueProviderConfigId,
        RepoProviderConfigId = "rp-1",
        ProjectId = projectId
    };

    private static ProviderConfig MakeProviderConfig(string id) => new()
    {
        Id = id,
        DisplayName = id,
        ProviderType = "GitHub",
        Kind = ProviderKind.Issue
    };

    // ── RequestCreateIssueForProvider — run not found → HubException ──────

    [Fact]
    public async Task RequestCreateIssueForProvider_RunNotFound_ThrowsHubException()
    {
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider(
            new JobId("ghost"), "ip-1", "title", "body", []);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*No active run*");
    }

    // ── RequestCreateIssueForProvider — provider config not found → HubException

    [Fact]
    public async Task RequestCreateIssueForProvider_ProviderConfigNotFound_ThrowsHubException()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        _facade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(Array.Empty<ProviderConfig>());

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "missing-provider", "title", "body", []);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*missing-provider*not found*");
    }

    // ── RequestCreateIssueForProvider — same provider as run → skips scope check

    [Fact]
    public async Task RequestCreateIssueForProvider_SameProviderAsRun_SkipsScopeCheck()
    {
        var run = CreateRun(issueProviderConfigId: "ip-1");
        var providerConfig = MakeProviderConfig("ip-1");
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockProvider.Setup(p => p.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "org/repo#99", Url = "https://example.com/99" });

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        _facade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { providerConfig });
        _facade.Setup(f => f.CreateIssueProvider(providerConfig))
               .Returns(mockProvider.Object);

        var hub = CreateHub();
        var result = await hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "ip-1", "title", "body", []);

        result.Identifier.Should().Be("org/repo#99");
    }

    // ── RequestCreateIssueForProvider — different provider, projectId empty → no scope check

    [Fact]
    public async Task RequestCreateIssueForProvider_EmptyProjectId_SkipsScopeCheck()
    {
        var run = CreateRun(projectId: "", issueProviderConfigId: "ip-1");
        var otherConfig = MakeProviderConfig("ip-other");
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockProvider.Setup(p => p.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "org/repo#100", Url = "https://example.com/100" });

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        _facade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { otherConfig });
        _facade.Setup(f => f.CreateIssueProvider(otherConfig))
               .Returns(mockProvider.Object);

        var hub = CreateHub();
        var result = await hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "ip-other", "title", "body", []);

        result.Identifier.Should().Be("org/repo#100");
        _facade.Verify(f => f.LoadTemplatesForProjectAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── RequestCreateIssueForProvider — provider not in project scope → HubException

    [Fact]
    public async Task RequestCreateIssueForProvider_ProviderNotInProjectScope_ThrowsHubException()
    {
        var run = CreateRun(projectId: "proj-A", issueProviderConfigId: "ip-1");
        var otherConfig = MakeProviderConfig("ip-foreign");

        var templates = new List<PipelineJobTemplate>
        {
            new()
            {
                Id = "tmpl-1",
                Name = "template",
                IssueProviderId = "ip-allowed",
                RepoProviderId = "rp-1"
            }
        };

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        _facade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { otherConfig });
        _facade.Setup(f => f.LoadTemplatesForProjectAsync("proj-A", It.IsAny<CancellationToken>()))
               .ReturnsAsync(templates);

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "ip-foreign", "title", "body", []);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*ip-foreign*not part of the run's project*");
    }

    // ── RequestCreateIssue — null arg guards ──────────────────────────────

    [Fact]
    public async Task RequestCreateIssue_NullTitle_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestCreateIssue(new JobId("job-1"), null!, "body", []);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestCreateIssue_NullBody_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestCreateIssue(new JobId("job-1"), "title", null!, []);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestCreateIssue_NullLabels_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestCreateIssue(new JobId("job-1"), "title", "body", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── RequestListOpenIssues — unknown run → HubException ────────────────

    [Fact]
    public async Task RequestListOpenIssues_UnknownRun_ThrowsHubException()
    {
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(Array.Empty<ProviderConfig>());

        var hub = CreateHub();
        var act = () => hub.RequestListOpenIssues(new JobId("ghost"), 1, 10, null);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*No active run*");
    }

    // ── RequestGetIssue — null identifier → ArgumentNullException ─────────

    [Fact]
    public async Task RequestGetIssue_NullIdentifier_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestGetIssue(new JobId("job-1"), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── RequestListComments — null identifier → ArgumentNullException ──────

    [Fact]
    public async Task RequestListComments_NullIdentifier_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestListComments(new JobId("job-1"), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── RequestUpdateComment — null arg guards ────────────────────────────

    [Fact]
    public async Task RequestUpdateComment_NullIssueId_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestUpdateComment(new JobId("job-1"), null!, "comment-1", "body");
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestUpdateComment_NullCommentId_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestUpdateComment(new JobId("job-1"), "issue-1", null!, "body");
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestUpdateComment_NullBody_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.RequestUpdateComment(new JobId("job-1"), "issue-1", "comment-1", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
