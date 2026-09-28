using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgent.Web.UnitTests.Hubs;

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
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
            IssueOps: Mock.Of<IHubIssueOperations>(),
            LifecycleService: Mock.Of<IAgentJobLifecycleService>(),
            TokenRefreshService: Mock.Of<IAgentTokenRefreshService>(),
            GateCommentFormatter: Mock.Of<IGateCommentFormatter>(),
            Logger: Log.Logger,
            OrphanRecoveryService: Mock.Of<IAgentOrphanRecoveryService>(),
            UiContext: HubTestHelpers.CreateNoOpHubContext()));

        hub.Context = mockCtx.Object;
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    private static PipelineRun CreateRun(
        string runId = "job-1",
        string projectId = "proj-A",
        string issueProviderConfigId = "ip-1",
        PipelineRunType runType = PipelineRunType.Decomposition) => new()
        {
            RunId = runId,
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = "rp-1",
            ProjectId = projectId,
            RunType = runType
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

    // ── RequestCreateIssueForProvider — different provider, projectId empty → rejected

    [Fact]
    public async Task RequestCreateIssueForProvider_EmptyProjectId_RejectsOtherProvider()
    {
        var run = CreateRun(projectId: "", issueProviderConfigId: "ip-1");
        var otherConfig = MakeProviderConfig("ip-other");

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        _facade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { otherConfig });

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "ip-other", "title", "body", []);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*ip-other*not in the scope*");
        _facade.Verify(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()), Times.Never);
    }

    // ── RequestCreateIssueForProvider — project epic: provider outside the project → HubException

    [Fact]
    public async Task RequestCreateIssueForProvider_ProjectEpic_ProviderNotInProject_ThrowsHubException()
    {
        var run = CreateRun(projectId: "proj-A", issueProviderConfigId: "ip-1");
        SetupScopeCheck(run, "ip-foreign", epicTrackerId: "ip-1", EnabledTemplate("ip-allowed"));

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "ip-foreign", "title", "body", []);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*ip-foreign*not in the scope*");
    }

    // ── RequestCreateIssueForProvider — project epic: an enabled template's tracker is allowed

    [Fact]
    public async Task RequestCreateIssueForProvider_ProjectEpic_EnabledTemplatesTracker_CreatesIssue()
    {
        var run = CreateRun(projectId: "proj-A", issueProviderConfigId: "ip-1");
        var mockProvider = SetupScopeCheck(run, "ip-allowed", epicTrackerId: "ip-1", EnabledTemplate("ip-allowed"));

        var hub = CreateHub();
        var result = await hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "ip-allowed", "title", "body", []);

        result.Identifier.Should().Be("org/repo#100");
        mockProvider.Verify(p => p.CreateIssueAsync("title", "body",
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── RequestCreateIssueForProvider — project epic: a disabled template's tracker → HubException

    [Fact]
    public async Task RequestCreateIssueForProvider_ProjectEpic_DisabledTemplatesTracker_ThrowsHubException()
    {
        var run = CreateRun(projectId: "proj-A", issueProviderConfigId: "ip-1");
        SetupScopeCheck(run, "ip-disabled", epicTrackerId: "ip-1",
            EnabledTemplate("ip-disabled") with { Enabled = false });

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "ip-disabled", "title", "body", []);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*ip-disabled*not in the scope*");
    }

    // ── RequestCreateIssueForProvider — repo epic: another template's tracker → HubException

    [Fact]
    public async Task RequestCreateIssueForProvider_RepoEpic_OtherTemplatesTracker_ThrowsHubException()
    {
        // The run is bound to its template's tracker (ip-1), not to the project's epic tracker (ip-epics),
        // so it is a repo epic and may only create issues in its own tracker.
        var run = CreateRun(projectId: "proj-A", issueProviderConfigId: "ip-1");
        SetupScopeCheck(run, "ip-allowed", epicTrackerId: "ip-epics", EnabledTemplate("ip-allowed"));

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "ip-allowed", "title", "body", []);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*ip-allowed*not in the scope*");
    }

    // ── RequestCreateIssueForProvider — the run's project no longer exists → HubException

    [Fact]
    public async Task RequestCreateIssueForProvider_ProjectNotFound_ThrowsHubException()
    {
        var run = CreateRun(projectId: "proj-A", issueProviderConfigId: "ip-1");
        SetupScopeCheck(run, "ip-allowed", epicTrackerId: "ip-1", EnabledTemplate("ip-allowed"));
        _facade.Setup(f => f.GetProjectByIdAsync("proj-A", It.IsAny<CancellationToken>()))
               .ReturnsAsync((PipelineProject?)null);

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "ip-allowed", "title", "body", []);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*ip-allowed*not in the scope*");
    }

    // ── RequestCreateIssueForProvider — only a decomposition run may leave its tracker

    [Theory]
    [InlineData(PipelineRunType.Implementation)]
    [InlineData(PipelineRunType.Review)]
    [InlineData(PipelineRunType.DecompositionAnalysis)]
    public async Task RequestCreateIssueForProvider_OtherRunTypeOnTheEpicTracker_ThrowsHubException(PipelineRunType runType)
    {
        // The epic tracker is also a template's tracker, so that template's other runs are bound to it too
        var run = CreateRun(projectId: "proj-A", issueProviderConfigId: "ip-1", runType: runType);
        SetupScopeCheck(run, "ip-allowed", epicTrackerId: "ip-1", EnabledTemplate("ip-allowed"));

        var hub = CreateHub();
        var act = () => hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "ip-allowed", "title", "body", []);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*ip-allowed*not in the scope*");
        _facade.Verify(f => f.GetProjectByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static PipelineJobTemplate EnabledTemplate(string issueProviderId) => new()
    {
        Id = $"tmpl-{issueProviderId}",
        Name = $"template-{issueProviderId}",
        IssueProviderId = issueProviderId,
        RepoProviderId = "rp-1",
        Enabled = true
    };

    /// <summary>
    /// Sets up a run of project proj-A with the given epic tracker and templates, and an issue provider
    /// for <paramref name="targetProviderId"/> that creates org/repo#100.
    /// </summary>
    private Mock<IIssueProvider> SetupScopeCheck(
        PipelineRun run, string targetProviderId, string epicTrackerId, params PipelineJobTemplate[] templates)
    {
        var targetConfig = MakeProviderConfig(targetProviderId);
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockProvider.Setup(p => p.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "org/repo#100", Url = "https://example.com/100" });

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        _facade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { targetConfig });
        _facade.Setup(f => f.GetProjectByIdAsync("proj-A", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new PipelineProject { Id = "proj-A", Name = "A", EpicIssueProviderId = epicTrackerId });
        _facade.Setup(f => f.LoadTemplatesForProjectAsync("proj-A", It.IsAny<CancellationToken>()))
               .ReturnsAsync(templates);
        _facade.Setup(f => f.CreateIssueProvider(targetConfig))
               .Returns(mockProvider.Object);
        return mockProvider;
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

    [Fact]
    public async Task RequestUpdateComment_NonNumericCommentId_ThrowsHubException()
    {
        // After the type change, the hub parses the wire-string commentId to long before
        // calling IIssueProvider. A non-numeric commentId must surface as HubException
        // (wrapped by ExecuteWithIssueProviderAsync) rather than silently failing.
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);

        var hub = CreateHub();
        var act = () => hub.RequestUpdateComment(
            new JobId("job-1"), "issue-1", "not-a-number", "body");

        // ExecuteWithIssueProviderAsync wraps all failures as HubException,
        // so the ArgumentException from the parse guard becomes a HubException.
        await act.Should().ThrowAsync<HubException>();
    }
}
