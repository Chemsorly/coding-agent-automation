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
/// Tests that <see cref="AgentHub.RequestCreateIssue"/> and
/// <see cref="AgentHub.RequestCreateIssueForProvider"/> filter agent-supplied labels
/// before forwarding them to the issue provider.
///
/// Acceptance criteria (issue #3159):
/// - <c>agent:epic-approved</c>, <c>agent:epic</c>, and status labels are dropped.
/// - <c>agent:next</c>, <c>agent:generated</c>, and non-agent labels are kept.
/// </summary>
public sealed class AgentHubCreateIssueLabelFilteringTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ILogger> _logger = new();

    private AgentHub CreateHub(string connectionId = "conn-1")
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

        var ctx = new Mock<HubCallerContext>();
        ctx.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = ctx.Object;
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    private static PipelineRun CreateRun(
        string jobId = "job-1",
        string issueProviderConfigId = "ip-1") => new()
        {
            RunId = jobId,
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = "rp-1"
        };

    private (ProviderConfig Config, Mock<IIssueProvider> Provider) SetupProvider(
        string configId = "ip-1")
    {
        var config = new ProviderConfig
        {
            Id = configId,
            DisplayName = configId,
            ProviderType = "GitHub",
            Kind = ProviderKind.Issue
        };
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _facade.Setup(f => f.GetProviderConfigByIdAsync(
                configId, ProviderKind.Issue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(config);
        _facade.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        return (config, mockProvider);
    }

    // ── RequestCreateIssue ────────────────────────────────────────────────

    [Fact]
    public async Task RequestCreateIssue_DropsEpicApproved_AndKeepsNonAgentAndAllowedLabels()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        var (_, mockProvider) = SetupProvider();

        IReadOnlyList<string>? capturedLabels = null;
        mockProvider.Setup(p => p.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, labels, _) =>
                capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "2", Url = "https://example.com/2" });

        var hub = CreateHub();
        await hub.RequestCreateIssue(
            new JobId("job-1"), "Title", "Body",
            [AgentLabels.EpicApproved, AgentLabels.Next, AgentLabels.Generated, "backend"]);

        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().NotContain(AgentLabels.EpicApproved);
        capturedLabels.Should().Contain(AgentLabels.Next);
        capturedLabels.Should().Contain(AgentLabels.Generated);
        capturedLabels.Should().Contain("backend");
    }

    [Fact]
    public async Task RequestCreateIssue_DropsEpic_InProgress_Done_StatusLabels()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        var (_, mockProvider) = SetupProvider();

        IReadOnlyList<string>? capturedLabels = null;
        mockProvider.Setup(p => p.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, labels, _) =>
                capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "3", Url = "https://example.com/3" });

        var hub = CreateHub();
        await hub.RequestCreateIssue(
            new JobId("job-1"), "Title", "Body",
            [AgentLabels.Epic, AgentLabels.InProgress, AgentLabels.Done, AgentLabels.Next, "feature"]);

        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().NotContain(AgentLabels.Epic);
        capturedLabels.Should().NotContain(AgentLabels.InProgress);
        capturedLabels.Should().NotContain(AgentLabels.Done);
        capturedLabels.Should().Contain(AgentLabels.Next);
        capturedLabels.Should().Contain("feature");
    }

    [Fact]
    public async Task RequestCreateIssue_KeepsOnlyAgentNextAndAgentGenerated_WhenOnlyAgentLabels()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        var (_, mockProvider) = SetupProvider();

        IReadOnlyList<string>? capturedLabels = null;
        mockProvider.Setup(p => p.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, labels, _) =>
                capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "4", Url = "https://example.com/4" });

        var hub = CreateHub();
        await hub.RequestCreateIssue(
            new JobId("job-1"), "Title", "Body",
            [AgentLabels.EpicApproved, AgentLabels.Epic, AgentLabels.Done, AgentLabels.Next, AgentLabels.Generated]);

        capturedLabels.Should().NotBeNull();
        // TODO: This is the only test that asserts an exact label count. The other tests use individual
        // Contain/NotContain assertions which would not catch an unexpected extra label leaking through.
        // Consider adding HaveCount assertions to the other test methods (e.g. DropsEpicApproved should
        // assert HaveCount(3)) so that any new filter gap is caught by count mismatch rather than only
        // by explicit NotContain enumerations.
        capturedLabels.Should().HaveCount(2);
        capturedLabels.Should().Contain(AgentLabels.Next);
        capturedLabels.Should().Contain(AgentLabels.Generated);
    }

    [Fact]
    public async Task RequestCreateIssue_EmptyLabelList_PassesEmpty()
    {
        var run = CreateRun();
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);
        var (_, mockProvider) = SetupProvider();

        IReadOnlyList<string>? capturedLabels = null;
        mockProvider.Setup(p => p.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, labels, _) =>
                capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "5", Url = "https://example.com/5" });

        var hub = CreateHub();
        await hub.RequestCreateIssue(new JobId("job-1"), "Title", "Body", []);

        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().BeEmpty();
        // TODO: No test currently covers the case where the agent supplies only disallowed agent labels
        // and no agent:next through RequestCreateIssue (as opposed to CreateSubIssuesStep). The hub
        // should forward an empty list without injecting any labels. Consider adding a test case such as:
        // input [agent:epic-approved, agent:done] → output [] to explicitly cover this boundary.
    }

    // ── RequestCreateIssueForProvider ─────────────────────────────────────

    [Fact]
    public async Task RequestCreateIssueForProvider_DropsEpicApproved_AndKeepsAllowedLabels()
    {
        var run = CreateRun(issueProviderConfigId: "ip-1");
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);

        var providerConfig = new ProviderConfig { Id = "ip-1", DisplayName = "ip-1", ProviderType = "GitHub", Kind = ProviderKind.Issue };
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        // Same provider as run — skips scope check.
        _facade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { providerConfig });
        _facade.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockProvider.Object);

        IReadOnlyList<string>? capturedLabels = null;
        mockProvider.Setup(p => p.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, labels, _) =>
                capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "10", Url = "https://example.com/10" });

        var hub = CreateHub();
        await hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "ip-1", "Title", "Body",
            [AgentLabels.EpicApproved, AgentLabels.Next, AgentLabels.Generated, "backend"]);

        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().NotContain(AgentLabels.EpicApproved);
        capturedLabels.Should().Contain(AgentLabels.Next);
        capturedLabels.Should().Contain(AgentLabels.Generated);
        capturedLabels.Should().Contain("backend");
    }

    [Fact]
    public async Task RequestCreateIssueForProvider_DropsAllAgentStatusLabels()
    {
        var run = CreateRun(issueProviderConfigId: "ip-1");
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(run);

        var providerConfig = new ProviderConfig { Id = "ip-1", DisplayName = "ip-1", ProviderType = "GitHub", Kind = ProviderKind.Issue };
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _facade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { providerConfig });
        _facade.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(mockProvider.Object);

        IReadOnlyList<string>? capturedLabels = null;
        mockProvider.Setup(p => p.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, labels, _) =>
                capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "11", Url = "https://example.com/11" });

        var hub = CreateHub();
        await hub.RequestCreateIssueForProvider(
            new JobId("job-1"), "ip-1", "Title", "Body",
            [AgentLabels.Epic, AgentLabels.EpicApproved, AgentLabels.InProgress, AgentLabels.Done,
             AgentLabels.Error, AgentLabels.Cancelled, AgentLabels.WontDo,
             AgentLabels.Next, AgentLabels.Generated, "bug"]);

        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().NotContain(AgentLabels.Epic);
        capturedLabels.Should().NotContain(AgentLabels.EpicApproved);
        capturedLabels.Should().NotContain(AgentLabels.InProgress);
        capturedLabels.Should().NotContain(AgentLabels.Done);
        capturedLabels.Should().NotContain(AgentLabels.Error);
        capturedLabels.Should().NotContain(AgentLabels.Cancelled);
        capturedLabels.Should().NotContain(AgentLabels.WontDo);
        capturedLabels.Should().Contain(AgentLabels.Next);
        capturedLabels.Should().Contain(AgentLabels.Generated);
        capturedLabels.Should().Contain("bug");
    }
}
