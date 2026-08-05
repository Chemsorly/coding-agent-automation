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
    public async Task FairRoundRobin_EqualQueues_DispatchesEquallyAcrossTypes()
    {
        // Arrange: 1 template, 3 queue types, 9 items each, budget = 9
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

        // Assert: exactly 3 per queue type (fairness ±1)
        result.ProcessedCount.Should().Be(9);
        _issueDispatchCount.Should().Be(3);
        _prDispatchCount.Should().Be(3);
        _decompDispatchCount.Should().Be(3);
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

        // Assert: exactly 2 dispatched, no more
        // Per-queue-type assertions verify that round-robin fairness is maintained under budget pressure:
        // turn order is issue→PR→(budget exhausted), so exactly 1 issue and 1 PR should be dispatched.
        // A bug dispatching 2 items from a single queue type would fail these per-type checks.
        result.ProcessedCount.Should().Be(2);
        _issueDispatchCount.Should().Be(1);
        _prDispatchCount.Should().Be(1);
        _decompDispatchCount.Should().Be(0);
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
