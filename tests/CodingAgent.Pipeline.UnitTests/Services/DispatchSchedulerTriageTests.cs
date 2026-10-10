using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using static CodingAgent.Pipeline.Services.DispatchScheduler;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// The triage turn of <see cref="DispatchScheduler"/>: the Decomposition tier, its own queue, no concurrency cap,
/// and the run bound to the tracker the <c>agent:triage</c> issue lives in.
/// </summary>
public class DispatchSchedulerTriageTests
{
    private readonly Mock<IDispatchRunCreator> _orchestration = new();
    private readonly Mock<IDispatchOrchestrationService> _dispatchOrchestration = new();
    private readonly List<TriageDispatchOrchestrationRequest> _triageRequests = [];
    private readonly List<string> _dispatchOrder = [];
    private readonly DispatchScheduler _scheduler;

    public DispatchSchedulerTriageTests()
    {
        _orchestration.Setup(o => o.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>())).Returns(false);
        _orchestration.Setup(o => o.GetAllActiveRuns()).Returns(new List<PipelineRun>());

        _dispatchOrchestration
            .Setup(d => d.PrepareTriageDistributionRequestAsync(It.IsAny<TriageDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TriageDispatchOrchestrationRequest req, CancellationToken _) =>
            {
                _triageRequests.Add(req);
                _dispatchOrder.Add($"triage:{req.IssueIdentifier}");
                return Distribution(req.IssueIdentifier);
            });
        _dispatchOrchestration
            .Setup(d => d.PrepareDecompositionDistributionRequestAsync(It.IsAny<DecompositionDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DecompositionDispatchOrchestrationRequest req, CancellationToken _) =>
            {
                _dispatchOrder.Add($"epic:{req.EpicIdentifier}");
                return Distribution(req.EpicIdentifier);
            });
        _dispatchOrchestration
            .Setup(d => d.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ImplementationDispatchOrchestrationRequest req, CancellationToken _) =>
            {
                _dispatchOrder.Add($"issue:{req.IssueIdentifier}");
                return Distribution(req.IssueIdentifier);
            });
        _dispatchOrchestration
            .Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, true, null));

        _scheduler = new DispatchScheduler(
            _orchestration.Object, _dispatchOrchestration.Object, dependencyChecker: null,
            new ProviderCacheManager(new Mock<IProviderFactory>().Object, Serilog.Core.Logger.None), Serilog.Core.Logger.None);
    }

    private static JobDistributionRequest Distribution(string identifier) => new()
    {
        IssueIdentifier = identifier,
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Triage,
        AgentSelector = "",
        TimeoutSeconds = 300
    };

    private static PipelineJobTemplate Template(bool triageEnabled = true) => new()
    {
        Id = "t1",
        Name = "Template t1",
        IssueProviderId = "ip-1",
        RepoProviderId = "rp-1",
        BrainProviderId = "brain-1",
        ImplementationEnabled = true,
        DecompositionEnabled = true,
        TriageEnabled = triageEnabled,
    };

    private static PipelineProject Project => new() { Id = "p1", Name = "Shop" };

    private static IssueSummary Issue(string id) => new() { Identifier = id, Title = $"Issue {id}", Labels = [] };

    private static DispatchRoundRobinRequest Request(
        PipelineJobTemplate template,
        Dictionary<string, List<TriageCandidate>> triageQueues,
        Dictionary<string, List<IssueSummary>>? issueQueues = null,
        Dictionary<string, List<EpicCandidate>>? decompositionQueues = null,
        PipelineConfiguration? config = null,
        int activeDecompositionCount = 0,
        HashSet<(IssueIdentifier, ProviderConfigId)>? active = null) => new()
        {
            PollableTemplates = [template],
            FlattenedTemplates = [(template, Project)],
            Config = config ?? new PipelineConfiguration { MaxConcurrentDecompositions = 5 },
            MaxRunsPerCycle = 10,
            ActiveIssueIdentifiers = active ?? [],
            IssueQueues = issueQueues ?? new Dictionary<string, List<IssueSummary>>(),
            PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
            DecompositionQueues = decompositionQueues ?? new Dictionary<string, List<EpicCandidate>>(),
            TriageQueues = triageQueues,
            ActiveDecompositionCount = activeDecompositionCount,
            ReportStatus = _ => { },
            ReportIssue = _ => { },
            NotifyChange = () => { }
        };

    [Theory]
    [InlineData(true, true, (int)DispatchTurn.Decomposition)]
    [InlineData(false, true, (int)DispatchTurn.Triage)]
    [InlineData(false, false, (int)DispatchTurn.Triage)]
    public void TrySelectHighestPriorityQueue_TriageComesAfterDecompositionAndBeforeIssues(
        bool hasDecomp, bool hasIssues, int expected)
    {
        var (found, turn) = TrySelectHighestPriorityQueue(hasIssues, hasPrs: false, hasDecomp, hasTriage: true);

        found.Should().BeTrue();
        turn.Should().Be((DispatchTurn)expected);
    }

    [Fact]
    public async Task FairRoundRobin_TriageCandidate_IsPreparedBoundToItsTrackerForTheProject()
    {
        var template = Template();
        var queues = new Dictionary<string, List<TriageCandidate>> { ["t1"] = [new TriageCandidate(Issue("42"), "ep-1")] };

        var result = await _scheduler.DispatchFairRoundRobinAsync(Request(template, queues), CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(1);
        var request = _triageRequests.Should().ContainSingle().Subject;
        request.IssueIdentifier.Value.Should().Be("42");
        request.IssueProviderId.Value.Should().Be("ep-1", "the run is bound to the tracker the issue lives in");
        request.RepoProviderId.Value.Should().Be("rp-1");
        request.BrainProviderId.Should().Be("brain-1");
        request.InitiatedBy.Should().Be(InitiatedByConstants.LoopTriage);
        request.Project.Id.Should().Be("p1");
    }

    [Fact]
    public async Task FairRoundRobin_DecompositionCapReached_TriageStillDispatches()
    {
        var template = Template();
        var queues = new Dictionary<string, List<TriageCandidate>>
        {
            ["t1"] = [new TriageCandidate(Issue("1"), "ip-1"), new TriageCandidate(Issue("2"), "ip-1")]
        };
        var decomposition = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = [new EpicCandidate(Issue("epic"), PipelineRunType.DecompositionAnalysis, "ip-1")]
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            Request(template, queues, decompositionQueues: decomposition,
                config: new PipelineConfiguration { MaxConcurrentDecompositions = 1 }, activeDecompositionCount: 1),
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(2, "triage has no concurrency cap of its own");
        _dispatchOrder.Should().Equal("triage:1", "triage:2");
    }

    [Fact]
    public async Task FairRoundRobin_DispatchesDecompositionThenTriageThenIssues()
    {
        var template = Template();
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            Request(template,
                new Dictionary<string, List<TriageCandidate>> { ["t1"] = [new TriageCandidate(Issue("bug"), "ip-1")] },
                issueQueues: new Dictionary<string, List<IssueSummary>> { ["t1"] = [Issue("feature")] },
                decompositionQueues: new Dictionary<string, List<EpicCandidate>>
                {
                    ["t1"] = [new EpicCandidate(Issue("epic"), PipelineRunType.DecompositionAnalysis, "ip-1")]
                }),
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(3);
        _dispatchOrder.Should().Equal("epic:epic", "triage:bug", "issue:feature");
    }

    [Fact]
    public async Task FairRoundRobin_TemplateWithoutTriage_DoesNotDispatchItsTriageQueue()
    {
        var queues = new Dictionary<string, List<TriageCandidate>> { ["t1"] = [new TriageCandidate(Issue("42"), "ip-1")] };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            Request(Template(triageEnabled: false), queues), CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(0);
        _triageRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task FairRoundRobin_IssueAlreadyActive_IsSkipped()
    {
        var queues = new Dictionary<string, List<TriageCandidate>>
        {
            ["t1"] = [new TriageCandidate(Issue("42"), "ep-1"), new TriageCandidate(Issue("43"), "ep-1")]
        };
        var active = new HashSet<(IssueIdentifier, ProviderConfigId)> { ("42", "ep-1") };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            Request(Template(), queues, active: active), CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(1);
        _triageRequests.Should().ContainSingle().Which.IssueIdentifier.Value.Should().Be("43");
    }

    [Fact]
    public async Task FairRoundRobin_PrepareReturnsNull_CountsNothingAndLeavesTheIssueInactive()
    {
        _dispatchOrchestration
            .Setup(d => d.PrepareTriageDistributionRequestAsync(It.IsAny<TriageDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobDistributionRequest?)null);
        var queues = new Dictionary<string, List<TriageCandidate>> { ["t1"] = [new TriageCandidate(Issue("42"), "ep-1")] };
        var active = new HashSet<(IssueIdentifier, ProviderConfigId)>();

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            Request(Template(), queues, active: active), CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(0);
        active.Should().BeEmpty();
    }
}
