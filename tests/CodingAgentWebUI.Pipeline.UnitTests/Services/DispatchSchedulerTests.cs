using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using static CodingAgentWebUI.Pipeline.Services.DispatchScheduler;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="DispatchScheduler"/> — verifies fair round-robin dispatch,
/// budget enforcement, empty queue handling, and processedCount accuracy.
/// </summary>
public class DispatchSchedulerTests
{
    private readonly Mock<IDispatchRunCreator> _mockOrchestration;
    private readonly Mock<IDispatchOrchestrationService> _mockDispatchOrchestration;
    private readonly ProviderCacheManager _cacheManager;
    private readonly DispatchScheduler _scheduler;

    // Track which queue type each dispatch went to
    private int _issueDispatchCount;
    private int _prDispatchCount;
    private int _decompDispatchCount;

    public DispatchSchedulerTests()
    {
        _mockOrchestration = new Mock<IDispatchRunCreator>();
        _mockDispatchOrchestration = new Mock<IDispatchOrchestrationService>();
        var mockFactory = new Mock<IProviderFactory>();

        _cacheManager = new ProviderCacheManager(mockFactory.Object, Serilog.Core.Logger.None);

        _mockOrchestration.Setup(o => o.IsIssueBeingProcessed(It.IsAny<string>(), It.IsAny<ProviderConfigId>()))
            .Returns(false);
        _mockOrchestration.Setup(o => o.GetAllActiveRuns())
            .Returns(new List<PipelineRun>());

        // Track dispatches by distinguishing issue vs PR vs decomp via the method called
        _mockDispatchOrchestration
            .Setup(d => d.PrepareDistributionRequestAsync(
                It.IsAny<ImplementationDispatchOrchestrationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ImplementationDispatchOrchestrationRequest req, CancellationToken ct) =>
            {
                Interlocked.Increment(ref _issueDispatchCount);
                return CreateMinimalJobDistributionRequest(req.IssueIdentifier);
            });

        _mockDispatchOrchestration
            .Setup(d => d.PrepareReviewDistributionRequestAsync(
                It.IsAny<ReviewDispatchRequest>(), It.IsAny<PipelineProject>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReviewDispatchRequest req, PipelineProject proj, CancellationToken ct) =>
            {
                Interlocked.Increment(ref _prDispatchCount);
                return CreateMinimalJobDistributionRequest(req.PrIdentifier);
            });

        _mockDispatchOrchestration
            .Setup(d => d.PrepareDecompositionDistributionRequestAsync(
                It.IsAny<DecompositionDispatchOrchestrationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DecompositionDispatchOrchestrationRequest req, CancellationToken ct) =>
            {
                Interlocked.Increment(ref _decompDispatchCount);
                return CreateMinimalJobDistributionRequest(req.EpicIdentifier);
            });

        _mockDispatchOrchestration
            .Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        _scheduler = new DispatchScheduler(
            _mockOrchestration.Object,
            _mockDispatchOrchestration.Object,
            workDistributor: null,
            dependencyChecker: null,
            _cacheManager,
            Serilog.Core.Logger.None);
    }

    #region Static Helper Tests

    [Fact]
    public void NextTurn_Issues_ReturnsPullRequests()
    {
        DispatchScheduler.NextTurn(DispatchTurn.Issues).Should().Be(DispatchTurn.PullRequests);
    }

    [Fact]
    public void NextTurn_PullRequests_ReturnsDecomposition()
    {
        DispatchScheduler.NextTurn(DispatchTurn.PullRequests).Should().Be(DispatchTurn.Decomposition);
    }

    [Fact]
    public void NextTurn_Decomposition_ReturnsIssues()
    {
        DispatchScheduler.NextTurn(DispatchTurn.Decomposition).Should().Be(DispatchTurn.Issues);
    }

    [Fact]
    public void HasEligible_EmptyQueues_ReturnsFalse()
    {
        var templates = new List<PipelineJobTemplate> { CreateTemplate("t1") };
        var queues = new Dictionary<string, List<IssueSummary>>();

        var result = DispatchScheduler.HasEligible(templates, queues, t => t.ImplementationEnabled);

        result.Should().BeFalse();
    }

    [Fact]
    public void HasEligible_NonEmptyQueueButTemplateNotEnabled_ReturnsFalse()
    {
        var template = CreateTemplate("t1", implementationEnabled: false);
        var templates = new List<PipelineJobTemplate> { template };
        var queues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("1") }
        };

        var result = DispatchScheduler.HasEligible(templates, queues, t => t.ImplementationEnabled);

        result.Should().BeFalse();
    }

    [Fact]
    public void HasEligible_NonEmptyQueueAndTemplateEnabled_ReturnsTrue()
    {
        var template = CreateTemplate("t1");
        var templates = new List<PipelineJobTemplate> { template };
        var queues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("1") }
        };

        var result = DispatchScheduler.HasEligible(templates, queues, t => t.ImplementationEnabled);

        result.Should().BeTrue();
    }

    [Fact]
    public void HasEligible_QueueExistsButEmpty_ReturnsFalse()
    {
        var template = CreateTemplate("t1");
        var templates = new List<PipelineJobTemplate> { template };
        var queues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new()
        };

        var result = DispatchScheduler.HasEligible(templates, queues, t => t.ImplementationEnabled);

        result.Should().BeFalse();
    }

    [Fact]
    public void HasEligibleProjectLevelDecomposition_EmptyDict_ReturnsFalse()
    {
        var queues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>();

        var result = DispatchScheduler.HasEligibleProjectLevelDecomposition(queues);

        result.Should().BeFalse();
    }

    [Fact]
    public void HasEligibleProjectLevelDecomposition_WithItems_ReturnsTrue()
    {
        var template = CreateTemplate("t1");
        var queues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>>
        {
            ["proj-1"] = new() { (CreateIssueSummary("epic-1"), PipelineRunType.DecompositionAnalysis, template) }
        };

        var result = DispatchScheduler.HasEligibleProjectLevelDecomposition(queues);

        result.Should().BeTrue();
    }

    #endregion

    #region Fairness Test

    [Fact]
    public async Task FairRoundRobin_EqualQueues_DispatchesByPriorityOrder()
    {
        // Arrange: 1 template, 3 queue types, 9 items each, budget = 9
        // With priority ordering (PRs > Decomp > Issues), all 9 budget slots are consumed by
        // PRs first (highest priority). Decomposition and Issues receive 0 dispatches.
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = Enumerable.Range(1, 9).Select(i => CreateIssueSummary($"issue-{i}")).ToList()
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = Enumerable.Range(1, 9).Select(i => CreatePrSummary($"pr-{i}", i)).ToList()
        };
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>
        {
            ["t1"] = Enumerable.Range(1, 9).Select(i => (CreateIssueSummary($"epic-{i}"), PipelineRunType.DecompositionAnalysis)).ToList()
        };

        // Act
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 9,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Assert: all 9 budget slots consumed by PRs (highest priority); Decomp and Issues get 0.
        result.ProcessedCount.Should().Be(9);
        _prDispatchCount.Should().Be(9);
        _decompDispatchCount.Should().Be(0);
        _issueDispatchCount.Should().Be(0);
        // TODO: add an integration test where the PR queue has fewer items than the budget so the
        // scheduler falls through to Decomposition (and then Issues) within a single cycle. The
        // current test always exhausts the full budget on PRs, so fallthrough to lower-priority
        // queues (e.g. Decomposition dispatched when PR queue empties mid-cycle) is not covered
        // by DispatchSchedulerTests integration tests.
    }

    #endregion

    #region Empty Queue Regression Tests (#974)

    [Fact]
    public async Task EmptyQueue_MissingKeyInPrQueues_DoesNotThrow()
    {
        // Arrange: issues populated, PR queue has NO entry for template, decomp empty
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-1"), CreateIssueSummary("issue-2"), CreateIssueSummary("issue-3") }
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>(); // No entry at all
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>();

        // Act — should NOT throw KeyNotFoundException
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration(),
                MaxRunsPerCycle = 5,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Assert: issues dispatched successfully
        result.ProcessedCount.Should().Be(3);
        _issueDispatchCount.Should().Be(3);
    }

    [Fact]
    public async Task EmptyQueue_EmptyListInPrQueues_DoesNotThrow()
    {
        // Arrange: PR queue key exists but list is empty
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-1"), CreateIssueSummary("issue-2") }
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = new() // Empty list
        };
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>();

        // Act
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration(),
                MaxRunsPerCycle = 5,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Assert
        result.ProcessedCount.Should().Be(2);
        _issueDispatchCount.Should().Be(2);
    }

    #endregion

    #region Budget Exhaustion

    [Fact]
    public async Task BudgetExhaustion_StopsAfterBudgetReached()
    {
        // Arrange: 3 queues × 10 items, budget = 2
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = Enumerable.Range(1, 10).Select(i => CreateIssueSummary($"issue-{i}")).ToList()
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = Enumerable.Range(1, 10).Select(i => CreatePrSummary($"pr-{i}", i)).ToList()
        };
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>
        {
            ["t1"] = Enumerable.Range(1, 10).Select(i => (CreateIssueSummary($"epic-{i}"), PipelineRunType.DecompositionAnalysis)).ToList()
        };

        // Act
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 2,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Assert: exactly 2 dispatched, no more.
        // Priority order is PR→Decomp→Issues; PRs have highest priority, so both budget slots
        // are consumed by PRs. A bug dispatching from a lower-priority queue would fail these checks.
        result.ProcessedCount.Should().Be(2);
        _prDispatchCount.Should().Be(2);
        _decompDispatchCount.Should().Be(0);
        _issueDispatchCount.Should().Be(0);
    }

    #endregion

    #region Termination When No Progress (filter-all scenario)

    [Fact]
    public async Task FilterAll_AllItemsFilteredByLabel_TerminatesWithZeroProcessed()
    {
        // Arrange: all issues have agent:error label → will be filtered out
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = Enumerable.Range(1, 5).Select(i => CreateIssueSummary($"issue-{i}", labels: new[] { AgentLabels.Error })).ToList()
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>();

        // Act — must terminate (no infinite loop)
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration(),
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Assert
        result.ProcessedCount.Should().Be(0);
        result.FailedCount.Should().Be(0);
    }

    [Fact]
    public async Task FilterAll_AllItemsAlreadyProcessing_TerminatesWithZeroProcessed()
    {
        // Arrange: all issues are already being processed
        _mockOrchestration.Setup(o => o.IsIssueBeingProcessed(It.IsAny<string>(), It.IsAny<ProviderConfigId>()))
            .Returns(true);

        // TODO: [WARNING] The test below only exercises IsIssueAlreadyActive branch (1):
        // _orchestration.IsIssueBeingProcessed. Branch (2) — ctx.ActiveIssueIdentifiers.Contains —
        // is never covered because ActiveIssueIdentifiers is always initialized empty.
        // Add a test where IsIssueBeingProcessed returns false but the identifier IS in
        // ActiveIssueIdentifiers to cover the second deduplication guard.
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = Enumerable.Range(1, 5).Select(i => CreateIssueSummary($"issue-{i}")).ToList()
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>();

        // Act
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration(),
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Assert
        result.ProcessedCount.Should().Be(0);
    }

    #endregion

    #region ProcessedCount Accuracy (#1369 regression)

    [Fact]
    public async Task ProcessedCount_MatchesActualDispatchCount_MixedQueues()
    {
        // Arrange: issues=3, PRs=2, decomp=1, budget=10 (enough for all)
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = Enumerable.Range(1, 3).Select(i => CreateIssueSummary($"issue-{i}")).ToList()
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = Enumerable.Range(1, 2).Select(i => CreatePrSummary($"pr-{i}", i)).ToList()
        };
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>
        {
            ["t1"] = new() { (CreateIssueSummary("epic-1"), PipelineRunType.DecompositionAnalysis) }
        };

        // Act
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Assert: processedCount == 3+2+1 = 6
        result.ProcessedCount.Should().Be(6);
        _issueDispatchCount.Should().Be(3);
        _prDispatchCount.Should().Be(2);
        _decompDispatchCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessedCount_IncludesProjectLevelDecomposition()
    {
        // Arrange: 2 issues + 1 project-level decomposition
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-1"), CreateIssueSummary("issue-2") }
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>();
        var projectLevelDecompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>>
        {
            ["p1"] = new() { (CreateIssueSummary("proj-epic-1"), PipelineRunType.DecompositionAnalysis, template) }
        };

        // Act
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = projectLevelDecompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Assert: 2 issues + 1 project-level decomp = 3
        // TODO: [WARNING] Add assertion for _decompDispatchCount to verify the project-level decomp
        // was actually dispatched (not just counted). The DispatchProjectLevelEpicAsync extraction
        // changed how dispatched/failed propagate back to counters — a regression where
        // additionalDecompDispatches is not incremented but processed still is would not be caught
        // by ProcessedCount alone. Expected: _decompDispatchCount.Should().Be(1).
        result.ProcessedCount.Should().Be(3);
    }

    [Fact]
    public async Task ProcessedCount_FailureCountsAsProcessedAndFailed()
    {
        // Arrange: project-level decomposition that throws on prepare
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        // Override decomposition prepare to throw
        _mockDispatchOrchestration
            .Setup(d => d.PrepareDecompositionDistributionRequestAsync(
                It.IsAny<DecompositionDispatchOrchestrationRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated dispatch failure"));

        var issueQueues = new Dictionary<string, List<IssueSummary>>();
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>();
        var projectLevelDecompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>>
        {
            ["p1"] = new() { (CreateIssueSummary("proj-epic-fail"), PipelineRunType.DecompositionAnalysis, template) }
        };

        // Act
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = projectLevelDecompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Assert: failure counts as both processed and failed
        result.ProcessedCount.Should().Be(1);
        result.FailedCount.Should().Be(1);
    }

    #endregion

    #region ExecuteTurnAsync / ComputeQueueAvailability / TrySelectNextTurn coverage

    /// <summary>
    /// When hasIssues=false but hasPrs=true, TrySelectNextTurn skips the Issues turn and selects PullRequests.
    /// </summary>
    [Fact]
    public async Task FairRoundRobin_NoIssues_StartsDispatchingFromPRQueue()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>(); // empty — no issues
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = new() { CreatePrSummary("pr-1", 1), CreatePrSummary("pr-2", 2) }
        };
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>();

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(2, "both PRs should be dispatched when no issues are present");
        _issueDispatchCount.Should().Be(0);
        _prDispatchCount.Should().Be(2);
    }

    /// <summary>
    /// When MaxConcurrentDecompositions is reached (active >= max), ComputeQueueAvailability
    /// returns hasDecomp=false and no decomp items are dispatched even when the queue is non-empty.
    /// </summary>
    [Fact]
    public async Task FairRoundRobin_DecompAtConcurrencyLimit_SkipsDecompQueue()
    {
        // Simulate 2 already-active decomposition runs at the limit
        _mockOrchestration.Setup(o => o.GetAllActiveRuns())
            .Returns(new List<PipelineRun>
            {
                new() { RunId = "active-1", IssueIdentifier = "a1", IssueTitle = "A1", IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1", RunType = PipelineRunType.DecompositionAnalysis },
                new() { RunId = "active-2", IssueIdentifier = "a2", IssueTitle = "A2", IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1", RunType = PipelineRunType.Decomposition }
            });

        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-1") }
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>
        {
            ["t1"] = new() { (CreateIssueSummary("epic-1"), PipelineRunType.DecompositionAnalysis) }
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                // MaxConcurrentDecompositions = 2, and there are already 2 active → limit reached
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 2 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        _decompDispatchCount.Should().Be(0, "decomp queue should be skipped when at the concurrency limit");
        _issueDispatchCount.Should().Be(1, "issue dispatch should still proceed");
    }

    /// <summary>
    /// Project-level decomposition fallback fires when regular decomp queue is empty but
    /// project-level queue has items and concurrency limit is not reached.
    /// </summary>
    [Fact]
    public async Task FairRoundRobin_ProjectLevelDecomp_FallbackWhenRegularDecompEmpty()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>();
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>(); // empty regular
        var projectLevelDecompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>>
        {
            ["p1"] = new() { (CreateIssueSummary("proj-epic-1"), PipelineRunType.DecompositionAnalysis, template) }
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = projectLevelDecompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(1, "project-level decomp fallback should fire when regular decomp queue is empty");
        _decompDispatchCount.Should().Be(1);
    }

    /// <summary>
    /// When regular decomp queue has items AND dispatches successfully in a turn, the project-level
    /// decomp fallback does NOT fire in that same turn (decompMadeProgress=true guards the fallback).
    /// On the next decomp turn, once regular is exhausted, project-level fires as fallback.
    /// </summary>
    [Fact]
    public async Task FairRoundRobin_ProjectLevelDecomp_NotFiredInSameTurnAsRegularDecomp()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>();
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        // Regular decomp queue with 1 item
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>
        {
            ["t1"] = new() { (CreateIssueSummary("epic-1"), PipelineRunType.DecompositionAnalysis) }
        };
        // Project-level also has 1 item — should only fire AFTER regular is exhausted
        var projectLevelDecompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>>
        {
            ["p1"] = new() { (CreateIssueSummary("proj-epic-1"), PipelineRunType.DecompositionAnalysis, template) }
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = projectLevelDecompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Regular decomp fires first (decompMadeProgress=true → project-level blocked in same turn).
        // Next decomp turn: regular is exhausted, project-level fires as fallback.
        // Total: 2 dispatches, both from decomp queue.
        result.ProcessedCount.Should().Be(2);
        _decompDispatchCount.Should().Be(2, "regular decomp dispatches first, then project-level as fallback in the next turn");
    }

    /// <summary>
    /// AllQueuesEmpty — TrySelectNextTurn returns found=false, loop breaks immediately, ProcessedCount=0.
    /// </summary>
    [Fact]
    public async Task FairRoundRobin_AllQueuesEmpty_BreaksImmediately()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = new Dictionary<string, List<IssueSummary>>(),
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>(),
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(0);
        result.FailedCount.Should().Be(0);
        _issueDispatchCount.Should().Be(0);
        _prDispatchCount.Should().Be(0);
        _decompDispatchCount.Should().Be(0);
    }

    /// <summary>
    /// Only issues remain (PRs and decomp empty). Verifies remaining turns are skipped when
    /// a queue type has no eligible items (exercising TrySelectNextTurn skip-ahead path).
    /// </summary>
    [Fact]
    public async Task FairRoundRobin_OnlyIssues_AllBudgetUsedByIssueQueue()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("i-1"), CreateIssueSummary("i-2"), CreateIssueSummary("i-3") }
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>(),
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(3);
        _issueDispatchCount.Should().Be(3);
        _prDispatchCount.Should().Be(0);
        _decompDispatchCount.Should().Be(0);
    }

    #endregion

    #region Priority Ordering — TrySelectNextTurn and DispatchFairRoundRobinAsync (#1931)

    /// <summary>
    /// TrySelectNextTurn returns PullRequests when both PRs and Issues are available.
    /// </summary>
    [Fact]
    public void TrySelectNextTurn_HasPrsAndIssues_SelectsPullRequestsFirst()
    {
        var (found, turn) = DispatchScheduler.TrySelectNextTurn(
            DispatchTurn.Issues, hasIssues: true, hasPrs: true, hasDecomp: false);

        found.Should().BeTrue();
        turn.Should().Be(DispatchTurn.PullRequests);
    }

    /// <summary>
    /// TrySelectNextTurn returns Decomposition when Decomposition and Issues are available but no PRs.
    /// </summary>
    [Fact]
    public void TrySelectNextTurn_HasDecompAndIssues_SelectsDecompositionFirst()
    {
        var (found, turn) = DispatchScheduler.TrySelectNextTurn(
            DispatchTurn.Issues, hasIssues: true, hasPrs: false, hasDecomp: true);

        found.Should().BeTrue();
        turn.Should().Be(DispatchTurn.Decomposition);
    }

    /// <summary>
    /// TrySelectNextTurn returns Issues when only Issues queue is non-empty.
    /// </summary>
    [Fact]
    public void TrySelectNextTurn_OnlyIssues_SelectsIssues()
    {
        var (found, turn) = DispatchScheduler.TrySelectNextTurn(
            DispatchTurn.Issues, hasIssues: true, hasPrs: false, hasDecomp: false);

        found.Should().BeTrue();
        turn.Should().Be(DispatchTurn.Issues);
    }

    /// <summary>
    /// TrySelectNextTurn returns found=false when all queues are empty.
    /// </summary>
    [Fact]
    public void TrySelectNextTurn_NoneEligible_ReturnsFalse()
    {
        var (found, _) = DispatchScheduler.TrySelectNextTurn(
            DispatchTurn.Issues, hasIssues: false, hasPrs: false, hasDecomp: false);

        found.Should().BeFalse();
    }

    /// <summary>
    /// TrySelectNextTurn returns PullRequests when all three queues are non-empty (PRs have highest priority).
    /// </summary>
    [Fact]
    public void TrySelectNextTurn_AllEligible_SelectsPullRequestsFirst()
    {
        var (found, turn) = DispatchScheduler.TrySelectNextTurn(
            DispatchTurn.Issues, hasIssues: true, hasPrs: true, hasDecomp: true);

        found.Should().BeTrue();
        turn.Should().Be(DispatchTurn.PullRequests);
    }

    /// <summary>
    /// Integration: when PR and Issue queues are both non-empty with budget=1,
    /// the PR (Review) is dispatched first — not the Issue (Implementation).
    /// </summary>
    [Fact]
    public async Task FairRoundRobin_ReviewAndImplementation_ReviewDispatchedFirst()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-1") }
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = new() { CreatePrSummary("pr-1", 1) }
        };
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>();

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 1,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(1);
        _prDispatchCount.Should().Be(1, "Review (PR) has higher priority than Implementation (Issue)");
        _issueDispatchCount.Should().Be(0);
    }

    /// <summary>
    /// Integration: when Decomposition and Issue queues are both non-empty with budget=1,
    /// the Decomposition is dispatched first — not the Issue (Implementation).
    /// </summary>
    [Fact]
    public async Task FairRoundRobin_DecompAndImplementation_DecompDispatchedFirst()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-1") }
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>
        {
            ["t1"] = new() { (CreateIssueSummary("epic-1"), PipelineRunType.DecompositionAnalysis) }
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 1,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ProjectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary, PipelineRunType, PipelineJobTemplate)>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(1);
        _decompDispatchCount.Should().Be(1, "Decomposition has higher priority than Implementation (Issue)");
        _issueDispatchCount.Should().Be(0);
    }

    #endregion

    #region StoppingToken Cancellation — Project-Level Decomp Loop (#1863)

    /// <summary>
    /// Regression test for #1863: when stoppingToken is cancelled during the first project-level
    /// dispatch call, the loop must break after that project and not iterate the remaining ones.
    /// Without the fix (adding stoppingToken.IsCancellationRequested to limitReached),
    /// PrepareDecompositionDistributionRequestAsync would be called 3 times instead of once.
    /// </summary>
    [Fact]
    public async Task WhenStoppingTokenCancelledDuringDispatch_ProjectLevelDecompLoop_BreaksAfterFirstProject()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        using var stoppingCts = new CancellationTokenSource();

        // On the first call, cancel stoppingToken and throw OperationCanceledException to simulate shutdown
        _mockDispatchOrchestration
            .Setup(d => d.PrepareDecompositionDistributionRequestAsync(
                It.IsAny<DecompositionDispatchOrchestrationRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns((DecompositionDispatchOrchestrationRequest req, CancellationToken ct) =>
            {
                stoppingCts.Cancel();
                throw new OperationCanceledException("Simulated shutdown", stoppingCts.Token);
            });

        var projectLevelDecompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>>
        {
            ["p1"] = new() { (CreateIssueSummary("proj-epic-1"), PipelineRunType.DecompositionAnalysis, template) },
            ["p2"] = new() { (CreateIssueSummary("proj-epic-2"), PipelineRunType.DecompositionAnalysis, template) },
            ["p3"] = new() { (CreateIssueSummary("proj-epic-3"), PipelineRunType.DecompositionAnalysis, template) },
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = new Dictionary<string, List<IssueSummary>>(),
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>(),
                ProjectLevelDecompositionQueues = projectLevelDecompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            stoppingCts.Token, CancellationToken.None);

        // The loop must break after the first project — only 1 dispatch attempt, not 3.
        // After the fix, the second iteration's limitReached check sees stoppingToken.IsCancellationRequested=true and breaks.
        // Note: The mock is configured to match any PrepareDecompositionDistributionRequestAsync call, so a
        // second call (for p2 or p3) would also throw and increment the count, making Times.Once reliable.
        _mockDispatchOrchestration.Verify(
            d => d.PrepareDecompositionDistributionRequestAsync(
                It.IsAny<DecompositionDispatchOrchestrationRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // No successful dispatches
        result.ProcessedCount.Should().Be(0);
    }

    /// <summary>
    /// Regression test for #1863: when stoppingToken is pre-cancelled before the call,
    /// the limitReached check at the top of the first foreach iteration fires immediately
    /// and no dispatch is attempted.
    /// </summary>
    [Fact]
    public async Task WhenStoppingTokenPreCancelled_ProjectLevelDecompLoop_NoDispatchAttempted()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        using var stoppingCts = new CancellationTokenSource();
        stoppingCts.Cancel(); // pre-cancel before the call

        var projectLevelDecompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>>
        {
            ["p1"] = new() { (CreateIssueSummary("proj-epic-1"), PipelineRunType.DecompositionAnalysis, template) },
            ["p2"] = new() { (CreateIssueSummary("proj-epic-2"), PipelineRunType.DecompositionAnalysis, template) },
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = new Dictionary<string, List<IssueSummary>>(),
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>(),
                ProjectLevelDecompositionQueues = projectLevelDecompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            stoppingCts.Token, CancellationToken.None);

        // limitReached fires on first iteration, no dispatch attempted
        _mockDispatchOrchestration.Verify(
            d => d.PrepareDecompositionDistributionRequestAsync(
                It.IsAny<DecompositionDispatchOrchestrationRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        result.ProcessedCount.Should().Be(0);
        result.FailedCount.Should().Be(0);
    }

    #endregion

    #region Helpers

    private static PipelineJobTemplate CreateTemplate(
        string id,
        bool implementationEnabled = true,
        bool reviewEnabled = true,
        bool decompositionEnabled = true)
    {
        return new PipelineJobTemplate
        {
            Id = id,
            Name = $"Template {id}",
            IssueProviderId = $"provider-{id}",
            RepoProviderId = $"repo-{id}",
            ImplementationEnabled = implementationEnabled,
            ReviewEnabled = reviewEnabled,
            DecompositionEnabled = decompositionEnabled
        };
    }

    private static PipelineProject CreateProject(string id) => new()
    {
        Id = id,
        Name = $"Project {id}"
    };

    private static IssueSummary CreateIssueSummary(string identifier, IEnumerable<string>? labels = null) => new()
    {
        Identifier = identifier,
        Title = $"Test issue {identifier}",
        Labels = labels?.ToList() ?? new List<string>()
    };

    private static PullRequestSummary CreatePrSummary(string identifier, int number) => new()
    {
        Identifier = identifier,
        Title = $"Test PR {identifier}",
        Description = "",
        Labels = new List<string>(),
        BranchName = $"feat/{identifier}",
        TargetBranch = "main",
        Url = $"https://github.com/owner/repo/pull/{number}",
        Number = number,
        IsDraft = false
    };

    private static (IReadOnlyList<PipelineJobTemplate> Pollable, IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> Flattened)
        BuildTemplateLists(PipelineJobTemplate template, PipelineProject project)
    {
        var pollable = new List<PipelineJobTemplate> { template };
        var flattened = new List<(PipelineJobTemplate, PipelineProject)> { (template, project) };
        return (pollable, flattened);
    }

    private static JobDistributionRequest CreateMinimalJobDistributionRequest(string issueIdentifier) => new()
    {
        IssueIdentifier = issueIdentifier,
        IssueProviderConfigId = "provider-t1",
        RepoProviderConfigId = "repo-t1",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "",
        TimeoutSeconds = 300
    };

    #endregion
}
