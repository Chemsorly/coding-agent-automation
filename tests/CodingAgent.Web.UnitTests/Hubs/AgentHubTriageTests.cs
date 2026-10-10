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
/// The agent hub's triage operations: recording a reported result against the run's own triage, refusing
/// results from other runs, refusing issue creation for triage runs, and letting a triage list the open
/// issues of its project's trackers.
/// </summary>
public sealed class AgentHubTriageTests
{
    private const string ValidResult = """
        { "verdict": "cause_found", "summary": "s", "investigated": [ { "check": "c", "where": "w", "result": "r" } ] }
        """;

    private static readonly Guid ProjectId = Guid.NewGuid();

    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ITriageStore> _store = new();
    private readonly Mock<ILogger> _logger = new();

    private HubTriageOperations Ops() => new(_facade.Object, _store.Object, _logger.Object);

    private AgentHub CreateHub(IHubTriageOperations? triageOps)
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
            UiContext: HubTestHelpers.CreateNoOpHubContext(),
            TriageOps: triageOps));
        var ctx = new Mock<HubCallerContext>();
        ctx.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = ctx.Object;
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    private void SetupRecord(WorkItemTaskType taskType, string tracker, string identifier, Guid? projectId = null) =>
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemRunRecord
            {
                TaskType = taskType,
                IssueIdentifier = identifier,
                IssueProviderConfigId = tracker,
                ProjectId = projectId ?? ProjectId,
            });

    private static PipelineRun Run(PipelineRunType runType, string tracker = "t-1", string? projectId = null) => new()
    {
        RunId = "job-1",
        IssueIdentifier = "431",
        IssueTitle = "Order confirmation spins forever",
        IssueUrl = "https://tracker/431",
        IssueProviderConfigId = tracker,
        RepoProviderConfigId = "rp-1",
        RunType = runType,
        ProjectId = projectId ?? ProjectId.ToString(),
    };

    // ── RecordResultAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task RecordResult_OperatorTriage_UsesTheKeyFromTheWorkItemRecord()
    {
        var triageId = Guid.NewGuid();
        SetupRecord(WorkItemTaskType.Triage, TriageConstants.ProviderConfigId, TriageConstants.IssueIdentifierFor(triageId));
        TriageResultReport? report = null;
        _store.Setup(s => s.RecordResultAsync(It.IsAny<TriageResultReport>(), It.IsAny<CancellationToken>()))
            .Callback<TriageResultReport, CancellationToken>((r, _) => report = r)
            .ReturnsAsync(new TriageRecord
            {
                Id = triageId, ProjectId = ProjectId.ToString(), Source = TriageSource.Operator, Title = "t",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

        await Ops().RecordResultAsync(new JobId("job-1"), ValidResult, CancellationToken.None);

        report!.Source.Should().Be(TriageSource.Operator);
        report.KeyProviderConfigId.Should().Be(TriageConstants.ProviderConfigId);
        report.KeyIdentifier.Should().Be(TriageConstants.IssueIdentifierFor(triageId));
        report.WorkItemId.Should().Be("job-1");
        report.ProjectId.Should().Be(ProjectId.ToString("D"));
        report.Result.Verdict.Should().Be(TriageVerdict.CauseFound);
    }

    [Fact]
    public async Task RecordResult_TrackerTriage_PassesTheIssueTitleAndUrlFromTheRun()
    {
        SetupRecord(WorkItemTaskType.Triage, "t-1", "431");
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(Run(PipelineRunType.Triage));
        TriageResultReport? report = null;
        _store.Setup(s => s.RecordResultAsync(It.IsAny<TriageResultReport>(), It.IsAny<CancellationToken>()))
            .Callback<TriageResultReport, CancellationToken>((r, _) => report = r)
            .ReturnsAsync(new TriageRecord
            {
                Id = Guid.NewGuid(), ProjectId = ProjectId.ToString(), Source = TriageSource.Issue, Title = "t",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

        await Ops().RecordResultAsync(new JobId("job-1"), ValidResult, CancellationToken.None);

        report!.Source.Should().Be(TriageSource.Issue);
        report.KeyProviderConfigId.Should().Be("t-1");
        report.KeyIdentifier.Should().Be("431");
        report.IssueTitle.Should().Be("Order confirmation spins forever");
        report.IssueUrl.Should().Be("https://tracker/431");
    }

    [Theory]
    [InlineData(WorkItemTaskType.Implementation)]
    [InlineData(WorkItemTaskType.Decomposition)]
    public async Task RecordResult_FromAnotherRunType_IsRefused(WorkItemTaskType taskType)
    {
        // An implementation run on the same issue must not be able to write a triage's result
        SetupRecord(taskType, "t-1", "431");

        var act = () => Ops().RecordResultAsync(new JobId("job-1"), ValidResult, CancellationToken.None);

        await act.Should().ThrowAsync<HubException>().WithMessage("*not a triage run*");
        _store.Verify(s => s.RecordResultAsync(It.IsAny<TriageResultReport>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordResult_UnknownJob_IsRefused()
    {
        _facade.Setup(f => f.GetWorkItemRunRecordAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemRunRecord?)null);

        var act = () => Ops().RecordResultAsync(new JobId("job-1"), ValidResult, CancellationToken.None);

        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task RecordResult_UnreadableResult_IsRefused()
    {
        SetupRecord(WorkItemTaskType.Triage, "t-1", "431");

        var act = () => Ops().RecordResultAsync(new JobId("job-1"), """{ "verdict": "maybe" }""", CancellationToken.None);

        await act.Should().ThrowAsync<HubException>().WithMessage("*could not be read*");
    }

    [Fact]
    public async Task ReportTriageResult_WithoutTriageOperations_IsRefused()
    {
        var act = () => CreateHub(triageOps: null).ReportTriageResult(new JobId("job-1"), ValidResult);

        await act.Should().ThrowAsync<HubException>();
    }

    // ── Issue creation ────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestCreateIssue_ForATriageRun_IsRefused()
    {
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(Run(PipelineRunType.Triage));

        var act = () => CreateHub(Ops()).RequestCreateIssue(new JobId("job-1"), "t", "b", []);

        await act.Should().ThrowAsync<HubException>().WithMessage("*triage run*");
        _facade.Verify(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()), Times.Never);
    }

    [Fact]
    public async Task RequestCreateIssueForProvider_ForATriageRunWithoutInMemoryRun_FailsClosed()
    {
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        SetupRecord(WorkItemTaskType.Triage, TriageConstants.ProviderConfigId, "triage:x");

        var act = () => CreateHub(Ops()).RequestCreateIssueForProvider(new JobId("job-1"), "t-2", "t", "b", []);

        await act.Should().ThrowAsync<HubException>().WithMessage("*triage run*");
    }

    [Fact]
    public async Task RequestListOpenIssuesForProvider_ATriageMayListItsProjectsTrackers_ButNoOthers()
    {
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(Run(PipelineRunType.Triage, tracker: TriageConstants.ProviderConfigId));
        _facade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "t-2", DisplayName = "t-2", ProviderType = "GitHub", Kind = ProviderKind.Issue },
                new() { Id = "t-other", DisplayName = "t-other", ProviderType = "GitHub", Kind = ProviderKind.Issue },
            });
        _facade.Setup(f => f.LoadTemplatesForProjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "tpl-1", Name = "api", IssueProviderId = "t-2", RepoProviderId = "rp-2", Enabled = true },
            });
        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 50, HasMore = false });
        _facade.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(provider.Object);
        var hub = CreateHub(Ops());

        var allowed = await hub.RequestListOpenIssuesForProvider(new JobId("job-1"), "t-2", 1, 50, null);
        var refused = () => hub.RequestListOpenIssuesForProvider(new JobId("job-1"), "t-other", 1, 50, null);

        allowed.Items.Should().BeEmpty();
        await refused.Should().ThrowAsync<HubException>();
    }
}
