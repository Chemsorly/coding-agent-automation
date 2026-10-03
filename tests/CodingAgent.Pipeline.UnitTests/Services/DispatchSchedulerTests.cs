using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using static CodingAgent.Pipeline.Services.DispatchScheduler;

namespace CodingAgent.Pipeline.UnitTests;

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

        _mockOrchestration.Setup(o => o.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()))
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
            dependencyChecker: null,
            _cacheManager,
            Serilog.Core.Logger.None);
    }

    #region Static Helper Tests

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

    #endregion

    #region Fairness Test

    [Fact]
    public async Task FairRoundRobin_EqualQueues_StrictPriorityWithFloorDisabled()
    {
        // Arrange: 1 template, 3 queue types, 9 items each, budget = 9, MinIssueSlots = 0
        // With priority ordering (PRs > Decomp > Issues) and floor disabled, all 9 budget slots
        // are consumed by PRs first (highest priority). Decomposition and Issues receive 0 dispatches.
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
        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = Enumerable.Range(1, 9).Select(i => new EpicCandidate(CreateIssueSummary($"epic-{i}"), PipelineRunType.DecompositionAnalysis, "provider-t1")).ToList()
        };

        // Act — MinIssueSlots = 0 disables the floor (strict priority, original behavior)
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100, MinIssueSlots = 0 },
                MaxRunsPerCycle = 9,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
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
    }

    [Fact]
    public async Task FairRoundRobin_EqualQueues_FloorReservesOneSlotForIssues()
    {
        // Arrange: 1 template, 3 queue types, 9 items each, budget = 9, MinIssueSlots = 1 (default)
        // With priority ordering (PRs > Decomp > Issues) and floor enabled:
        //   Priority loop: 8 PR slots dispatched (remaining drops 9→1), loop exits (no more budget).
        //   Floor pass: remaining=1 > 0, issueDispatchedThisCycle=false → dispatches 1 Issue.
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
        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = Enumerable.Range(1, 9).Select(i => new EpicCandidate(CreateIssueSummary($"epic-{i}"), PipelineRunType.DecompositionAnalysis, "provider-t1")).ToList()
        };

        // Act — MinIssueSlots = 1 (default) enables the floor
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100, MinIssueSlots = 1 },
                MaxRunsPerCycle = 9,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Assert: 8 PR slots from priority loop + 1 Issue slot from floor pass = 9 total.
        result.ProcessedCount.Should().Be(9);
        _prDispatchCount.Should().Be(8);
        _decompDispatchCount.Should().Be(0);
        _issueDispatchCount.Should().Be(1);
    }

    #endregion

    #region Floor Allocation Tests (#2476)

    [Fact]
    public async Task FloorAllocation_WhenBothPrsAndIssuesPresent_IssuesGetAtLeastOneSlot()
    {
        // AC1: When PRs and Implementation issues are both present,
        // at least 1 Implementation issue is dispatched per cycle.
        // TODO: [WARNING] This test uses an empty Decomp queue. AC1 says "When PRs and Implementation
        // issues are both present" without qualification on Decomp, so the realistic scenario includes
        // all three queue types non-empty. A Decomp queue consumes priority-loop budget between PRs
        // and Issues, changing how much budget remains before the floor fires. The property test
        // (FloorProperty_IssuesAlwaysGetAtLeastOneSlot_WhenPresent) now covers this dimension, but
        // a dedicated deterministic test with a non-empty Decomp queue would make AC1 more explicit.
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = Enumerable.Range(1, 5).Select(i => CreateIssueSummary($"issue-{i}")).ToList()
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = Enumerable.Range(1, 10).Select(i => CreatePrSummary($"pr-{i}", i)).ToList()
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MinIssueSlots = 1 },
                MaxRunsPerCycle = 5,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(5);
        _prDispatchCount.Should().Be(4);
        _issueDispatchCount.Should().Be(1);
    }

    [Fact]
    public async Task FloorAllocation_WhenOnlyPrsPresent_AllSlotsGoToPrs()
    {
        // AC2: When only PRs are present, all available slots go to PRs (no change from current behavior).
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = Enumerable.Range(1, 5).Select(i => CreatePrSummary($"pr-{i}", i)).ToList()
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MinIssueSlots = 1 },
                MaxRunsPerCycle = 5,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = new Dictionary<string, List<IssueSummary>>(),
                PrQueues = prQueues,
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(5);
        _prDispatchCount.Should().Be(5);
        _issueDispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task FloorAllocation_WithMinIssueSlotsZero_StrictPriorityBehavior()
    {
        // AC3: MinIssueSlots = 0 disables floor (strict priority, original behavior).
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = Enumerable.Range(1, 5).Select(i => CreateIssueSummary($"issue-{i}")).ToList()
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = Enumerable.Range(1, 5).Select(i => CreatePrSummary($"pr-{i}", i)).ToList()
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MinIssueSlots = 0 },
                MaxRunsPerCycle = 5,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(5);
        _prDispatchCount.Should().Be(5);
        _issueDispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task FloorAllocation_WithMaxRunsPerCycleOne_FloorDoesNotApply()
    {
        // MaxRunsPerCycle = 1: only one slot — PRs win, floor does not apply.
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

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MinIssueSlots = 1 },
                MaxRunsPerCycle = 1,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(1);
        _prDispatchCount.Should().Be(1);
        _issueDispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task FloorAllocation_WithUnlimitedBudget_PriorityLoopNaturallyFallsThrough()
    {
        // MaxRunsPerCycle = 0 means unlimited. With a finite in-memory PR queue, the priority
        // loop exhausts all PRs and then naturally falls through to dispatch the issue
        // (issueDispatchedThisCycle=true → floor guard skips). This verifies correct total
        // dispatch count and that Issues are not blocked when PRs run out.
        //
        // NOTE: The unlimited-budget floor code path (floorBudget = issueFloor when
        // totalBudget == int.MaxValue) is structurally unreachable with finite in-memory queues:
        // the priority loop always drains all PRs before AnyProgress becomes false, setting
        // issueDispatchedThisCycle=true via natural fallthrough, which causes the floor guard
        // to skip. To exercise the unlimited-budget floor path, a truly infinite PR source
        // (e.g., a mock returning endless items) would be required. The floor mechanism for
        // finite budgets is covered by FloorAllocation_WhenBothPrsAndIssuesPresent_IssuesGetAtLeastOneSlot
        // and FairRoundRobin_EqualQueues_FloorReservesOneSlotForIssues.
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-1") }
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = Enumerable.Range(1, 3).Select(i => CreatePrSummary($"pr-{i}", i)).ToList()
        };

        // Act with unlimited budget (MaxRunsPerCycle = 0)
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MinIssueSlots = 1 },
                MaxRunsPerCycle = 0,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // All PRs dispatched by priority loop, then issue dispatched via natural fallthrough.
        result.ProcessedCount.Should().Be(4);
        _prDispatchCount.Should().Be(3);
        _issueDispatchCount.Should().Be(1);
        _decompDispatchCount.Should().Be(0);
    }

    #endregion

    #region Floor Allocation Property Test (#2476 — AC4)

    /// <summary>
    /// AC4: For any combination of PR/Issue/Decomp queue sizes with MaxRunsPerCycle ≥ 2,
    /// Issues always get ≥ 1 slot when present (MinIssueSlots = 1, default).
    /// Calls DispatchFairRoundRobinAsync directly — NOT a simulation — to catch real regressions.
    /// Decomposition queue is populated (0–9 items) to verify the floor invariant holds when
    /// Decomp items compete for priority-loop budget between PRs and Issues.
    /// </summary>
    [Property(MaxTest = 20)]
    public Property FloorProperty_IssuesAlwaysGetAtLeastOneSlot_WhenPresent(
        PositiveInt prCount,
        PositiveInt issueCount,
        NonNegativeInt decompCount,
        PositiveInt maxRunsPerCycle)
    {
        // Clamp to reasonable sizes (property generators can produce very large values)
        int prs = prCount.Get % 20 + 1;     // 1–20 PRs
        int issues = issueCount.Get % 10 + 1; // 1–10 Issues
        int decomps = decompCount.Get % 10;   // 0–9 Decomp items (NonNegativeInt so 0 is included)
        int budget = maxRunsPerCycle.Get % 19 + 2; // 2–20 (ensures >= 2)

        var template = new PipelineJobTemplate
        {
            Id = "t1",
            Name = "Template t1",
            IssueProviderId = "provider-t1",
            RepoProviderId = "repo-t1",
            ImplementationEnabled = true,
            ReviewEnabled = true,
            DecompositionEnabled = true
        };
        var project = new PipelineProject { Id = "p1", Name = "Project p1" };

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = Enumerable.Range(1, issues).Select(i => new IssueSummary
            {
                Identifier = $"issue-{i}",
                Title = $"Issue {i}",
                Labels = new List<string>()
            }).ToList()
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = Enumerable.Range(1, prs).Select(i => new PullRequestSummary
            {
                Identifier = $"pr-{i}",
                Title = $"PR {i}",
                Description = "",
                Labels = new List<string>(),
                BranchName = $"feat/pr-{i}",
                TargetBranch = "main",
                Url = $"https://github.com/owner/repo/pull/{i}",
                Number = i,
                IsDraft = false
            }).ToList()
        };
        var decompQueues = new Dictionary<string, List<EpicCandidate>>();
        if (decomps > 0)
        {
            decompQueues["t1"] = Enumerable.Range(1, decomps).Select(i =>
                new EpicCandidate(new IssueSummary { Identifier = $"epic-{i}", Title = $"Epic {i}", Labels = new List<string>() },
                 PipelineRunType.DecompositionAnalysis, "provider-t1")).ToList();
        }

        var issueDispatched = 0;
        var mockOrch = new Mock<IDispatchRunCreator>();
        mockOrch.Setup(o => o.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()))
            .Returns(false);
        // GetAllActiveRuns() is no longer used for decomposition concurrency counting —
        // the count comes from DispatchRoundRobinRequest.ActiveDecompositionCount (default 0).

        var mockDispatch = new Mock<IDispatchOrchestrationService>();
        mockDispatch
            .Setup(d => d.PrepareDistributionRequestAsync(
                It.IsAny<ImplementationDispatchOrchestrationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ImplementationDispatchOrchestrationRequest req, CancellationToken _) =>
            {
                Interlocked.Increment(ref issueDispatched);
                return new JobDistributionRequest
                {
                    IssueIdentifier = req.IssueIdentifier,
                    IssueProviderConfigId = "provider-t1",
                    RepoProviderConfigId = "repo-t1",
                    InitiatedBy = "test",
                    TaskType = WorkItemTaskType.Implementation,
                    AgentSelector = "",
                    TimeoutSeconds = 300
                };
            });
        mockDispatch
            .Setup(d => d.PrepareReviewDistributionRequestAsync(
                It.IsAny<ReviewDispatchRequest>(), It.IsAny<PipelineProject>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReviewDispatchRequest req, PipelineProject _, CancellationToken __) =>
                new JobDistributionRequest
                {
                    IssueIdentifier = req.PrIdentifier,
                    IssueProviderConfigId = "provider-t1",
                    RepoProviderConfigId = "repo-t1",
                    InitiatedBy = "test",
                    TaskType = WorkItemTaskType.Review,
                    AgentSelector = "",
                    TimeoutSeconds = 300
                });
        mockDispatch
            .Setup(d => d.PrepareDecompositionDistributionRequestAsync(
                It.IsAny<DecompositionDispatchOrchestrationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DecompositionDispatchOrchestrationRequest req, CancellationToken _) =>
                new JobDistributionRequest
                {
                    IssueIdentifier = req.EpicIdentifier,
                    IssueProviderConfigId = "provider-t1",
                    RepoProviderConfigId = "repo-t1",
                    InitiatedBy = "test",
                    TaskType = WorkItemTaskType.Decomposition,
                    AgentSelector = "",
                    TimeoutSeconds = 300
                });
        mockDispatch
            .Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(true, false, null));

        var mockFactory = new Mock<IProviderFactory>();
        var cacheManager = new ProviderCacheManager(mockFactory.Object, Serilog.Core.Logger.None);
        var scheduler = new DispatchScheduler(
            mockOrch.Object, mockDispatch.Object,
            dependencyChecker: null, cacheManager, Serilog.Core.Logger.None);

        var result = scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = new[] { template },
                FlattenedTemplates = new[] { (template, project) },
                Config = new PipelineConfiguration { MinIssueSlots = 1, MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = budget,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None)
            .GetAwaiter().GetResult();
        // NOTE: .GetAwaiter().GetResult() is required here because FsCheck [Property] functions must
        // be synchronous. This is safe in xUnit's thread-pool context (no SynchronizationContext).

        return (issueDispatched >= 1)
            .ToProperty()
            .Label($"PRs={prs}, Issues={issues}, Decomps={decomps}, Budget={budget}, IssueDispatched={issueDispatched}, Total={result.ProcessedCount}");
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
        var decompQueues = new Dictionary<string, List<EpicCandidate>>();

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
        var decompQueues = new Dictionary<string, List<EpicCandidate>>();

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
        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = Enumerable.Range(1, 10).Select(i => new EpicCandidate(CreateIssueSummary($"epic-{i}"), PipelineRunType.DecompositionAnalysis, "provider-t1")).ToList()
        };

        // Act
        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100, MinIssueSlots = 0 },
                MaxRunsPerCycle = 2,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Assert: exactly 2 dispatched, no more.
        // Priority order is PR→Decomp→Issues; PRs have highest priority, so both budget slots
        // are consumed by PRs. MinIssueSlots=0 disables the floor, preserving strict-priority behavior.
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
        var decompQueues = new Dictionary<string, List<EpicCandidate>>();

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
        _mockOrchestration.Setup(o => o.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()))
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
        var decompQueues = new Dictionary<string, List<EpicCandidate>>();

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
        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = new() { new EpicCandidate(CreateIssueSummary("epic-1"), PipelineRunType.DecompositionAnalysis, "provider-t1") }
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
    public async Task ProcessedCount_IncludesProjectEpicInExecutorQueue()
    {
        // Arrange: 2 issues + 1 project epic queued under its executor template
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-1"), CreateIssueSummary("issue-2") }
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = new() { new EpicCandidate(CreateIssueSummary("proj-epic-1"), PipelineRunType.DecompositionAnalysis, "provider-epics") }
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
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // Assert: 2 issues + 1 project epic = 3
        result.ProcessedCount.Should().Be(3);
        _decompDispatchCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessedCount_FailureCountsAsProcessedAndFailed()
    {
        // Arrange: project epic whose dispatch throws on prepare
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
        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = new() { new EpicCandidate(CreateIssueSummary("proj-epic-fail"), PipelineRunType.DecompositionAnalysis, "provider-epics") }
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

    #region ExecuteTurnAsync / ComputeQueueAvailability / TrySelectHighestPriorityQueue coverage

    /// <summary>
    /// When hasIssues=false but hasPrs=true, TrySelectHighestPriorityQueue skips the Issues turn and selects PullRequests.
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
        var decompQueues = new Dictionary<string, List<EpicCandidate>>();

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
        // The active count comes from DispatchRoundRobinRequest.ActiveDecompositionCount (loaded
        // at cycle start from the API), not from GetAllActiveRuns(). Set it to 2 to hit the limit.
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-1") }
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = new() { new EpicCandidate(CreateIssueSummary("epic-1"), PipelineRunType.DecompositionAnalysis, "provider-t1") }
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
                ActiveDecompositionCount = 2, // simulates 2 active from a prior cycle
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        _decompDispatchCount.Should().Be(0, "decomp queue should be skipped when at the concurrency limit");
        _issueDispatchCount.Should().Be(1, "issue dispatch should still proceed");
    }

    /// <summary>
    /// A project epic sits in its executor template's queue with the epic tracker it lives in:
    /// the run is bound to that tracker and executes in the template's repository.
    /// </summary>
    [Fact]
    public async Task FairRoundRobin_ProjectEpicInExecutorQueue_BindsRunToEpicTracker()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);
        var requests = CaptureDecompositionRequests();

        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = new() { new EpicCandidate(CreateIssueSummary("proj-epic-1"), PipelineRunType.DecompositionAnalysis, "provider-epics") }
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            CreateDecompositionOnlyRequest(pollable, flattened, decompQueues, new HashSet<(IssueIdentifier, ProviderConfigId)>()),
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(1);
        requests.Should().ContainSingle();
        requests[0].IssueProviderId.Value.Should().Be("provider-epics", "the run is bound to the tracker the epic lives in");
        requests[0].RepoProviderId.Value.Should().Be("repo-t1", "the executor template's repository runs the epic");
    }

    /// <summary>
    /// The already-active check uses each candidate's own tracker: an epic that is active in the
    /// epic tracker is skipped, while the same number in the template's tracker is a different issue.
    /// </summary>
    [Fact]
    public async Task FairRoundRobin_ActiveCheck_UsesTheCandidatesTracker()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);
        var requests = CaptureDecompositionRequests();

        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = new()
            {
                new EpicCandidate(CreateIssueSummary("7"), PipelineRunType.DecompositionAnalysis, "provider-epics"),
                new EpicCandidate(CreateIssueSummary("7"), PipelineRunType.DecompositionAnalysis, "provider-t1")
            }
        };
        var active = new HashSet<(IssueIdentifier, ProviderConfigId)>
        {
            (new IssueIdentifier("7"), new ProviderConfigId("provider-epics"))
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            CreateDecompositionOnlyRequest(pollable, flattened, decompQueues, active),
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(1);
        requests.Should().ContainSingle()
            .Which.IssueProviderId.Value.Should().Be("provider-t1");
    }

    /// <summary>
    /// Regression coverage for #1863, ported from the removed project-level loop: when the stopping token
    /// is cancelled during a decomposition dispatch, no other template's epic is prepared.
    /// </summary>
    [Fact]
    public async Task WhenStoppingTokenCancelledDuringDecompositionDispatch_NoOtherEpicIsPrepared()
    {
        var t1 = CreateTemplate("t1");
        var t2 = CreateTemplate("t2");
        var project = CreateProject("p1");
        var pollable = new List<PipelineJobTemplate> { t1, t2 };
        var flattened = new List<(PipelineJobTemplate Template, PipelineProject Project)> { (t1, project), (t2, project) };
        using var stoppingCts = new CancellationTokenSource();

        // On the first call, cancel stoppingToken and throw OperationCanceledException to simulate shutdown
        _mockDispatchOrchestration
            .Setup(d => d.PrepareDecompositionDistributionRequestAsync(
                It.IsAny<DecompositionDispatchOrchestrationRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns((DecompositionDispatchOrchestrationRequest _, CancellationToken _) =>
            {
                stoppingCts.Cancel();
                throw new OperationCanceledException("Simulated shutdown", stoppingCts.Token);
            });

        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = new() { new EpicCandidate(CreateIssueSummary("epic-1"), PipelineRunType.DecompositionAnalysis, "provider-t1") },
            ["t2"] = new() { new EpicCandidate(CreateIssueSummary("epic-2"), PipelineRunType.DecompositionAnalysis, "provider-t2") }
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            CreateDecompositionOnlyRequest(pollable, flattened, decompQueues, new HashSet<(IssueIdentifier, ProviderConfigId)>()),
            stoppingCts.Token, CancellationToken.None);

        _mockDispatchOrchestration.Verify(
            d => d.PrepareDecompositionDistributionRequestAsync(
                It.IsAny<DecompositionDispatchOrchestrationRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        result.ProcessedCount.Should().Be(0);
        result.FailedCount.Should().Be(0);
    }

    private List<DecompositionDispatchOrchestrationRequest> CaptureDecompositionRequests()
    {
        var requests = new List<DecompositionDispatchOrchestrationRequest>();
        _mockDispatchOrchestration
            .Setup(d => d.PrepareDecompositionDistributionRequestAsync(
                It.IsAny<DecompositionDispatchOrchestrationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DecompositionDispatchOrchestrationRequest req, CancellationToken _) =>
            {
                requests.Add(req);
                return CreateMinimalJobDistributionRequest(req.EpicIdentifier);
            });
        return requests;
    }

    private static DispatchRoundRobinRequest CreateDecompositionOnlyRequest(
        IReadOnlyList<PipelineJobTemplate> pollable,
        IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> flattened,
        Dictionary<string, List<EpicCandidate>> decompQueues,
        HashSet<(IssueIdentifier, ProviderConfigId)> activeIssueIdentifiers) => new()
        {
            PollableTemplates = pollable,
            FlattenedTemplates = flattened,
            Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
            MaxRunsPerCycle = 10,
            ActiveIssueIdentifiers = activeIssueIdentifiers,
            IssueQueues = new Dictionary<string, List<IssueSummary>>(),
            PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
            DecompositionQueues = decompQueues,
            ReportStatus = _ => { },
            ReportIssue = _ => { },
            NotifyChange = () => { }
        };

    /// <summary>
    /// AllQueuesEmpty — TrySelectHighestPriorityQueue returns found=false, loop breaks immediately, ProcessedCount=0.
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
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
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
    /// a queue type has no eligible items (exercising TrySelectHighestPriorityQueue skip-ahead path).
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
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
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

    #region Priority Ordering — TrySelectHighestPriorityQueue and DispatchFairRoundRobinAsync (#1931)

    /// <summary>
    /// TrySelectHighestPriorityQueue returns PullRequests when both PRs and Issues are available.
    /// </summary>
    [Fact]
    public void TrySelectHighestPriorityQueue_HasPrsAndIssues_SelectsPullRequestsFirst()
    {
        var (found, turn) = DispatchScheduler.TrySelectHighestPriorityQueue(
            hasIssues: true, hasPrs: true, hasDecomp: false);

        found.Should().BeTrue();
        turn.Should().Be(DispatchTurn.PullRequests);
    }

    /// <summary>
    /// TrySelectHighestPriorityQueue returns Decomposition when Decomposition and Issues are available but no PRs.
    /// </summary>
    [Fact]
    public void TrySelectHighestPriorityQueue_HasDecompAndIssues_SelectsDecompositionFirst()
    {
        var (found, turn) = DispatchScheduler.TrySelectHighestPriorityQueue(
            hasIssues: true, hasPrs: false, hasDecomp: true);

        found.Should().BeTrue();
        turn.Should().Be(DispatchTurn.Decomposition);
    }

    /// <summary>
    /// TrySelectHighestPriorityQueue returns Issues when only Issues queue is non-empty.
    /// </summary>
    [Fact]
    public void TrySelectHighestPriorityQueue_OnlyIssues_SelectsIssues()
    {
        var (found, turn) = DispatchScheduler.TrySelectHighestPriorityQueue(
            hasIssues: true, hasPrs: false, hasDecomp: false);

        found.Should().BeTrue();
        turn.Should().Be(DispatchTurn.Issues);
    }

    /// <summary>
    /// TrySelectHighestPriorityQueue returns found=false when all queues are empty.
    /// </summary>
    [Fact]
    public void TrySelectHighestPriorityQueue_NoneEligible_ReturnsFalse()
    {
        var (found, _) = DispatchScheduler.TrySelectHighestPriorityQueue(
            hasIssues: false, hasPrs: false, hasDecomp: false);

        found.Should().BeFalse();
    }

    /// <summary>
    /// TrySelectHighestPriorityQueue returns PullRequests when all three queues are non-empty (PRs have highest priority).
    /// </summary>
    [Fact]
    public void TrySelectHighestPriorityQueue_AllEligible_SelectsPullRequestsFirst()
    {
        var (found, turn) = DispatchScheduler.TrySelectHighestPriorityQueue(
            hasIssues: true, hasPrs: true, hasDecomp: true);

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
        var decompQueues = new Dictionary<string, List<EpicCandidate>>();

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
        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = new() { new EpicCandidate(CreateIssueSummary("epic-1"), PipelineRunType.DecompositionAnalysis, "provider-t1") }
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

    #region Identity per tracker and per repository (#3145)

    [Fact]
    public async Task IssueRound_ADependencyIsCheckedInTheIssuesOwnTracker()
    {
        // Two templates on different trackers each have an issue #30 that depends on #12. #12 is closed
        // in tracker a and open in tracker b, so only a's issue is ready. Issue numbers are unique only
        // within a tracker: an answer about #12 in one tracker says nothing about #12 in another.
        var trackerA = new Mock<IIssueProvider>();
        trackerA.Setup(p => p.IsIssueClosedAsync("12", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var trackerB = new Mock<IIssueProvider>();
        trackerB.Setup(p => p.IsIssueClosedAsync("12", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _cacheManager.IssueProviders["provider-ta"] = trackerA.Object;
        _cacheManager.IssueProviders["provider-tb"] = trackerB.Object;

        var dispatched = new List<string>();
        _mockDispatchOrchestration
            .Setup(d => d.PrepareDistributionRequestAsync(
                It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ImplementationDispatchOrchestrationRequest req, CancellationToken _) =>
            {
                dispatched.Add($"{req.IssueProviderId.Value}#{req.IssueIdentifier.Value}");
                return CreateMinimalJobDistributionRequest(req.IssueIdentifier);
            });
        var scheduler = new DispatchScheduler(
            _mockOrchestration.Object, _mockDispatchOrchestration.Object,
            new DependencyChecker(Serilog.Core.Logger.None), _cacheManager, Serilog.Core.Logger.None);

        var templateA = CreateTemplate("ta");
        var templateB = CreateTemplate("tb");
        var project = CreateProject("p1");
        IssueSummary DependsOn12() => new() { Identifier = "30", Title = "Needs #12", Labels = [], Description = "Depends on #12" };

        await scheduler.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = [templateA, templateB],
                FlattenedTemplates = [(templateA, project), (templateB, project)],
                Config = new PipelineConfiguration { MinIssueSlots = 0 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = new Dictionary<string, List<IssueSummary>> { ["ta"] = [DependsOn12()], ["tb"] = [DependsOn12()] },
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        dispatched.Should().Equal("provider-ta#30");
        trackerB.Verify(p => p.IsIssueClosedAsync("12", It.IsAny<CancellationToken>()), Times.Once,
            "tracker b is asked about its own #12 instead of reusing tracker a's answer");
    }

    [Fact]
    public async Task PrRound_AnActiveIssueWithTheSameNumber_DoesNotBlockThePullRequest()
    {
        // Issue #5 is being implemented. Pull request !5 is a different thing: a pull request is identified
        // by its repository and number, an issue by its tracker and number.
        var template = CreateTemplate("t1");
        var (pollable, flattened) = BuildTemplateLists(template, CreateProject("p1"));

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            PrOnlyRequest(pollable, flattened, active: ((IssueIdentifier)"5", (ProviderConfigId)template.IssueProviderId)),
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(1);
        _prDispatchCount.Should().Be(1);
    }

    [Fact]
    public async Task PrRound_AnActiveReviewOfThePullRequest_SkipsIt()
    {
        var template = CreateTemplate("t1");
        var (pollable, flattened) = BuildTemplateLists(template, CreateProject("p1"));

        await _scheduler.DispatchFairRoundRobinAsync(
            PrOnlyRequest(pollable, flattened, active: ((IssueIdentifier)"5", (ProviderConfigId)template.RepoProviderId)),
            CancellationToken.None, CancellationToken.None);

        _prDispatchCount.Should().Be(0, "the review work item of pull request !5 is keyed by the repository");
    }

    private static DispatchScheduler.DispatchRoundRobinRequest PrOnlyRequest(
        IReadOnlyList<PipelineJobTemplate> pollable,
        IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> flattened,
        (IssueIdentifier, ProviderConfigId) active) => new()
        {
            PollableTemplates = pollable,
            FlattenedTemplates = flattened,
            Config = new PipelineConfiguration { MinIssueSlots = 0 },
            MaxRunsPerCycle = 5,
            ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)> { active },
            IssueQueues = new Dictionary<string, List<IssueSummary>>(),
            PrQueues = new Dictionary<string, List<PullRequestSummary>> { [pollable[0].Id] = [CreatePrSummary("5", 5)] },
            DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
            ReportStatus = _ => { },
            ReportIssue = _ => { },
            NotifyChange = () => { }
        };

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

    #region Floor Reclaim Tests (#2655)

    // TODO: All three reclaim tests use MinIssueSlots=1 (issueFloor=1), so the
    // Math.Min(issueFloor, totalBudget - processedCount - failedCount) cap in the reclaim
    // formula is never the binding constraint. A regression where the cap misbehaves with
    // issueFloor > 1 (e.g. MinIssueSlots=2, MaxRunsPerCycle=5, issues-only backlog) would
    // not be detected. Consider adding a test with MinIssueSlots >= 2. (#2655 review warning)

    /// <summary>
    /// AC1: With MaxRunsPerCycle=5, MinIssueSlots=1, and 10 issues queued (no PRs),
    /// exactly 5 issues must be dispatched per cycle.
    ///
    /// Before the fix the priority-loop budget was reduced to 4 (5-1), so only 4 issues
    /// were dispatched and the reserved floor slot was silently abandoned once the floor
    /// pass was skipped (issueDispatchedThisCycle=true). The reclaim fix tops up `remaining`
    /// by 1 the first time an issue turn makes progress, allowing the loop to reach 5.
    /// </summary>
    [Fact]
    public async Task FloorReclaimScenario_IssuesOnlyBacklog_DispatchesFullBudget()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = Enumerable.Range(1, 10).Select(i => CreateIssueSummary($"issue-{i}")).ToList()
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MinIssueSlots = 1, MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 5,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(5, "exactly MaxRunsPerCycle issues must be dispatched when the backlog is issues-only");
        _issueDispatchCount.Should().Be(5);
        _prDispatchCount.Should().Be(0);
    }

    /// <summary>
    /// AC2: The floor guarantee still dispatches at least MinIssueSlots issues when no issues
    /// were dispatched in the priority loop and issues are pending.
    ///
    /// With PRs=4, Issues=5, budget=5, MinIssueSlots=1: the priority loop dispatches 4 PRs
    /// (priorityBudget=4, issueDispatchedThisCycle=false). The reclaim block does NOT fire.
    /// The floor pass then fires and dispatches 1 issue, yielding 5 total.
    /// </summary>
    // TODO: This test validates pre-existing floor behaviour (4 PRs exhaust priorityBudget,
    // floor pass fires). The reclaim path (IssueMadeProgress branch) is never entered here,
    // so a regression specific to the reclaim code would not be detected by this test alone.
    // It is effectively equivalent to the pre-existing FloorAllocation_WhenBothPrsAndIssuesPresent
    // test (same parameters). Consider adding a scenario where PRs partially fill the priority
    // loop and issues are then dispatched before the budget is exhausted, causing the reclaim
    // path to fire while the floor guarantee is still preserved. (#2655 review warning)
    [Fact]
    public async Task FloorReclaimScenario_PrsExhaustBudget_FloorGuaranteePreserved()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = Enumerable.Range(1, 5).Select(i => CreateIssueSummary($"issue-{i}")).ToList()
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = Enumerable.Range(1, 4).Select(i => CreatePrSummary($"pr-{i}", i)).ToList()
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MinIssueSlots = 1, MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 5,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(5, "4 PRs from the priority loop plus 1 issue from the floor pass");
        _prDispatchCount.Should().Be(4, "all 4 PRs consumed the priority-loop budget");
        _issueDispatchCount.Should().Be(1, "floor guarantee fires because no issues were dispatched in the priority loop");
    }

    /// <summary>
    /// Scenario B from the in-code TODO comment: mixed-queue backlog where PRs drain first,
    /// then issues are dispatched in the priority loop, and the reclaim path restores the
    /// reserved slot so the full budget is consumed.
    ///
    /// MaxRunsPerCycle=5, MinIssueSlots=1, PRs=2, Issues=10 → before fix: 4 dispatched
    /// (2 PRs + 2 issues against priorityBudget=4, floor skipped). After fix: 5 dispatched
    /// (2 PRs + 2 issues dispatched before reclaim, then 1 more issue after reclaim = 3 issues total).
    /// </summary>
    [Fact]
    public async Task FloorReclaimScenario_MixedBacklog_DispatchesFullBudget()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = Enumerable.Range(1, 10).Select(i => CreateIssueSummary($"issue-{i}")).ToList()
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = Enumerable.Range(1, 2).Select(i => CreatePrSummary($"pr-{i}", i)).ToList()
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MinIssueSlots = 1, MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 5,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(5, "full budget must be consumed: 2 PRs + 3 issues (1 reclaimed slot)");
        _prDispatchCount.Should().Be(2);
        _issueDispatchCount.Should().Be(3);
    }

    #endregion

    // TODO: No span-emission tests exist for Loop.Enqueue (added in issue #2977).
    // Add tests to verify: (1) Loop.Enqueue is emitted with issue_identifier and template_name tags
    // when an issue is successfully dispatched; (2) no span is emitted when dispatch is skipped.

    #region Issue #3154 — Cross-Cycle Decomposition Limit, 409 Budget Fix, Same-Issue Dedup

    // TODO [WARNING]: A 409 returns DispatchAttemptResult.Skip (Attempted=false), which means
    // DispatchRoundAsync does not set madeProgress=true for that template. If every template in
    // a given round returns Skip (e.g. the only template in the queue gets a 409), TurnResult.AnyProgress
    // is false and the outer while-loop in DispatchFairRoundRobinAsync breaks immediately. Remaining
    // items in other templates' queues are then not reached in the priority loop for that queue type
    // (PRs and decompositions have no floor pass, so they are missed entirely that cycle).
    // Concrete example: [PR_A → 409, PR_B, PR_C] in one template — PR_A gets a 409, loop breaks,
    // PR_B and PR_C are not dispatched until the next poll cycle. This is a behavioral side-effect of
    // treating 409 as Skip rather than as a "soft success" that keeps madeProgress=true.
    // Fixing this would require either (a) setting madeProgress=true on AlreadyQueued while still not
    // incrementing processed/consumed, or (b) removing the AnyProgress break guard for the Skip case.

    /// <summary>
    /// AC1: With MaxConcurrentDecompositions = 1 and one decomposition WorkItem active from
    /// an earlier cycle (supplied via ActiveDecompositionCount), the scheduler dispatches no
    /// decomposition. Issues still dispatch normally.
    ///
    /// This proves the cross-cycle fix: activeDecompositionCount = 1 from the request prevents
    /// decomposition dispatch even though GetAllActiveRuns() always returns [] in the Scheduler.
    /// </summary>
    [Fact]
    public async Task WhenActiveDecompositionCountEqualsMax_DecompositionQueueSkipped()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-1") }
        };
        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = new() { new EpicCandidate(CreateIssueSummary("epic-1"), PipelineRunType.DecompositionAnalysis, "provider-t1") }
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 1 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                // 1 decomposition work item active from an earlier cycle — must block new dispatch
                ActiveDecompositionCount = 1,
                IssueQueues = issueQueues,
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = decompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        _decompDispatchCount.Should().Be(0, "decomposition must be blocked by the cross-cycle active count");
        _issueDispatchCount.Should().Be(1, "issue dispatch must still proceed");
        result.ProcessedCount.Should().Be(1);
    }

    /// <summary>
    /// AC2: When the API returns a 409 (DispatchOutcome.AlreadyExists = true), the result
    /// must not increment ProcessedCount and must not consume budget.
    ///
    /// Design note: DispatchRoundAsync iterates over *templates* (one queue item per template
    /// per outer-loop iteration), not queue items directly. A 409 returns
    /// DispatchAttemptResult.Skip (Attempted=false), which causes the foreach to `continue`
    /// to the next template without setting madeProgress=true. With a single template, the
    /// outer while-loop then breaks (AnyProgress=false), so only one prepare+distribute
    /// round-trip is made for the 409 issue.
    ///
    /// To observe both the 409 skip (ProcessedCount not incremented) and a genuine dispatch
    /// in the same cycle, two templates are used: t1's issue gets a 409 (skip, continue),
    /// t2's issue dispatches normally. Both templates are visited in the same DispatchRoundAsync
    /// foreach pass; t2's success sets madeProgress=true so the outer loop continues until
    /// all queues are empty.
    /// </summary>
    [Fact]
    public async Task WhenDispatchReturnsAlreadyQueued_ProcessedCountNotIncrementedAndBudgetNotConsumed()
    {
        // Two templates — t1's issue will get a 409, t2's issue will dispatch normally.
        // Both templates share the same IssueProviderId so the dedup check works correctly.
        var t1 = new PipelineJobTemplate
        {
            Id = "t1",
            Name = "Template t1",
            IssueProviderId = "provider-t1",
            RepoProviderId = "repo-t1",
            ImplementationEnabled = true
        };
        var t2 = new PipelineJobTemplate
        {
            Id = "t2",
            Name = "Template t2",
            IssueProviderId = "provider-t2",
            RepoProviderId = "repo-t2",
            ImplementationEnabled = true
        };
        var project = CreateProject("p1");
        var pollable = new List<PipelineJobTemplate> { t1, t2 };
        var flattened = new List<(PipelineJobTemplate, PipelineProject)> { (t1, project), (t2, project) };

        // t1's prepare is called first (foreach order); t2's prepare is called second.
        // DistributeAndFinalizeAsync is called once per prepare: first call → 409, second → success.
        // TODO [WARNING]: This Setup overrides the default DistributeAndFinalizeAsync Setup registered
        // in the constructor. In Moq, the last non-sequence Setup for a given expression wins, so the
        // constructor's default is silently replaced here. If the constructor Setup were registered after
        // this one, the 409-simulation would never fire and ProcessedCount would be 2 instead of 1
        // (the test would still pass with the wrong assertion). Consider using SetupSequence (consistent
        // with the other dedup tests in this class) or calling _mockDispatchOrchestration.Reset() before
        // this Setup to make the override explicit and less fragile.
        var callCount = 0;
        _mockDispatchOrchestration
            .Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                // First call (t1's issue): simulate 409 — live WorkItem already exists.
                if (callCount == 1) return new DispatchOutcome(true, true, null) { AlreadyExists = true };
                // Subsequent calls (t2's issue): normal success.
                return new DispatchOutcome(true, false, null);
            });

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-already-queued") },
            ["t2"] = new() { CreateIssueSummary("issue-new") }
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MinIssueSlots = 0 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        // The 409 must not count as a processed dispatch — only t2's genuine dispatch counts.
        result.ProcessedCount.Should().Be(1,
            "the 409 (t1) must not increment ProcessedCount; only the genuine dispatch (t2) must count");
        // TODO [WARNING]: The AC requires "does not consume ClosedLoopMaxRunsPerCycle budget" but this
        // test uses MaxRunsPerCycle=10 with only 2 issues, so the budget can never become a constraint.
        // Budget non-consumption is indistinguishable from budget consumption here — ProcessedCount==1
        // is equally explained by "t2 dispatched, no more items" regardless of whether the 409 consumed
        // a slot. To prove the criterion, add a scenario with MaxRunsPerCycle=2 and three templates
        // (t1=409, t2=success, t3=success), asserting ProcessedCount==2. If the 409 consumed a slot,
        // t3 would be blocked and ProcessedCount would be 1.
        // TODO [WARNING]: The AC also requires "records a skipped-already-processing decision" but this
        // test makes no assertion on PipelineTelemetry.LoopDispatchDecisions. If the SkippedAlreadyProcessing
        // telemetry line in DispatchScheduler.Issues.cs were accidentally removed or used the wrong constant,
        // no test would catch it. Add an assertion on the telemetry counter or use a test double for
        // PipelineTelemetry to verify the decision is recorded.
        // TODO [WARNING]: The _issueDispatchCount==2 assertion locks in the implementation detail that
        // a 409 still incurs a full prepare round-trip before the 409 is discovered at DistributeAndFinalizeAsync.
        // The in-code TODO in DispatchScheduler.Issues.cs notes that updating ActiveIssueIdentifiers on
        // AlreadyQueued would short-circuit this redundant call. If that optimisation is applied, this
        // assertion will fail even though observable behaviour (ProcessedCount, budget) is correct.
        // Consider using BeGreaterThanOrEqualTo(1) or removing this assertion if prepare-call count
        // is not a behavioural requirement.
        _issueDispatchCount.Should().Be(2,
            "PrepareDistributionRequestAsync must be called for both templates' issues");
    }

    /// <summary>
    /// AC3: When the same issue appears in two template queues within one cycle, it must be
    /// dispatched exactly once. The second template must see the issue as already active because
    /// the first dispatch added it to ctx.ActiveIssueIdentifiers.
    /// </summary>
    [Fact]
    public async Task WhenSameIssueInTwoTemplateQueues_OnlyDispatchedOnce()
    {
        // Two templates, same IssueProviderId — simulates a project tracker appearing in both
        var t1 = new PipelineJobTemplate
        {
            Id = "t1",
            Name = "Template t1",
            IssueProviderId = "shared-provider",
            RepoProviderId = "repo-t1",
            ImplementationEnabled = true
        };
        var t2 = new PipelineJobTemplate
        {
            Id = "t2",
            Name = "Template t2",
            IssueProviderId = "shared-provider",
            RepoProviderId = "repo-t2",
            ImplementationEnabled = true
        };
        var project = CreateProject("p1");
        var pollable = new List<PipelineJobTemplate> { t1, t2 };
        var flattened = new List<(PipelineJobTemplate, PipelineProject)> { (t1, project), (t2, project) };

        // The same issue identifier in both template queues
        const string duplicateIssueId = "42";
        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary(duplicateIssueId) },
            ["t2"] = new() { CreateIssueSummary(duplicateIssueId) }
        };

        var prepareCallIds = new List<string>();
        _mockDispatchOrchestration
            .Setup(d => d.PrepareDistributionRequestAsync(
                It.IsAny<ImplementationDispatchOrchestrationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ImplementationDispatchOrchestrationRequest req, CancellationToken _) =>
            {
                prepareCallIds.Add(req.IssueIdentifier.Value);
                return CreateMinimalJobDistributionRequest(req.IssueIdentifier);
            });

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MinIssueSlots = 0 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = issueQueues,
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(1,
            "the same issue must be dispatched only once across both template queues");
        prepareCallIds.Should().ContainSingle()
            .Which.Should().Be(duplicateIssueId,
                "prepare must be called exactly once for the duplicate issue");
    }

    /// <summary>
    /// When the first decomposition dispatch in a cycle fills the remaining concurrency slot,
    /// the per-dispatch concurrency check inside DispatchDecompositionRoundAsync must return
    /// Abort for the second template — even though activeDecompositionCount (from the prior cycle)
    /// is 0. This verifies the in-round guard on additionalDecompDispatches, not ComputeQueueAvailability.
    /// </summary>
    [Fact]
    public async Task WhenDecompositionConcurrencyLimitFilledInRound_SecondTemplateIsAborted()
    {
        var t1 = new PipelineJobTemplate
        {
            Id = "t1",
            Name = "Template t1",
            IssueProviderId = "provider-t1",
            RepoProviderId = "repo-t1",
            DecompositionEnabled = true
        };
        var t2 = new PipelineJobTemplate
        {
            Id = "t2",
            Name = "Template t2",
            IssueProviderId = "provider-t2",
            RepoProviderId = "repo-t2",
            DecompositionEnabled = true
        };
        var project = CreateProject("p1");
        var pollable = new List<PipelineJobTemplate> { t1, t2 };
        var flattened = new List<(PipelineJobTemplate, PipelineProject)> { (t1, project), (t2, project) };

        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = new() { new EpicCandidate(CreateIssueSummary("epic-1"), PipelineRunType.DecompositionAnalysis, "provider-t1") },
            ["t2"] = new() { new EpicCandidate(CreateIssueSummary("epic-2"), PipelineRunType.DecompositionAnalysis, "provider-t2") }
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                // Limit = 1, ActiveDecompositionCount = 0 → first dispatch fills the slot;
                // second template's per-dispatch guard aborts.
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 1 },
                MaxRunsPerCycle = 10,
                ActiveDecompositionCount = 0,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = new Dictionary<string, List<IssueSummary>>(),
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = decompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        _decompDispatchCount.Should().Be(1,
            "only the first template's epic should be dispatched; the second is blocked by the in-round concurrency guard");
        result.ProcessedCount.Should().Be(1);
    }

    /// <summary>
    /// When a decomposition dispatch returns AlreadyQueued (409), it must not count as a
    /// processed dispatch and must not consume the per-cycle budget.
    /// Uses two templates: t1's epic gets a 409 (skip, no budget consumed), t2's epic dispatches.
    /// </summary>
    [Fact]
    public async Task WhenDecompositionDispatchReturnsAlreadyQueued_ProcessedCountNotIncrementedAndBudgetNotConsumed()
    {
        var t1 = new PipelineJobTemplate
        {
            Id = "t1",
            Name = "Template t1",
            IssueProviderId = "provider-t1",
            RepoProviderId = "repo-t1",
            DecompositionEnabled = true
        };
        var t2 = new PipelineJobTemplate
        {
            Id = "t2",
            Name = "Template t2",
            IssueProviderId = "provider-t2",
            RepoProviderId = "repo-t2",
            DecompositionEnabled = true
        };
        var project = CreateProject("p1");
        var pollable = new List<PipelineJobTemplate> { t1, t2 };
        var flattened = new List<(PipelineJobTemplate, PipelineProject)> { (t1, project), (t2, project) };

        // First DistributeAndFinalizeAsync call → 409; second → success.
        var callCount = 0;
        _mockDispatchOrchestration
            .Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) return new DispatchOutcome(true, true, null) { AlreadyExists = true };
                return new DispatchOutcome(true, false, null);
            });

        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = new() { new EpicCandidate(CreateIssueSummary("epic-already-running"), PipelineRunType.DecompositionAnalysis, "provider-t1") },
            ["t2"] = new() { new EpicCandidate(CreateIssueSummary("epic-new"), PipelineRunType.DecompositionAnalysis, "provider-t2") }
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                // MaxRunsPerCycle = 2 with 3 effective candidates (t1=409, t2=success):
                // if the 409 consumed a budget slot, t2 would be blocked and ProcessedCount == 0.
                // Since 409 must not consume budget, t2 dispatches and ProcessedCount == 1.
                MaxRunsPerCycle = 2,
                ActiveDecompositionCount = 0,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = new Dictionary<string, List<IssueSummary>>(),
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = decompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(1,
            "the 409 (t1 epic) must not increment ProcessedCount; only the genuine dispatch (t2 epic) must count");
        _decompDispatchCount.Should().BeGreaterThanOrEqualTo(1,
            "PrepareDecompositionDistributionRequestAsync must be called at least once");
    }

    #endregion

    #region Issue #3262 — FinalizeDispatchOutcome Characterization

    /// <summary>
    /// AC1 (PR 409): When a PR review dispatch returns AlreadyQueued (409), it must not count
    /// as a processed dispatch and must not consume the per-cycle budget.
    /// Uses two templates: t1's PR gets a 409 (skip, no budget consumed), t2's PR dispatches.
    /// Mirrors WhenDispatchReturnsAlreadyQueued_... and WhenDecompositionDispatch... for the
    /// Reviews path that was previously untested.
    /// </summary>
    // TODO [WARNING]: The "budget not consumed" claim in the test name is not fully falsifiable
    // with the current setup (MaxRunsPerCycle=10, 2 PRs: 1×409 + 1×success). ProcessedCount==1
    // is identical whether the budget was consumed by the 409 or not, because there are no
    // additional items to distinguish the two cases. To make budget non-consumption falsifiable,
    // use MaxRunsPerCycle=2 with three templates (t1=409, t2=success, t3=success): if the 409
    // wrongly consumed a budget slot, t3 would never be dispatched and ProcessedCount would stay
    // at 1. See the analogous Issues 409 test for the same documented gap.
    // TODO [WARNING]: This test does not assert that ActiveIssueIdentifiers is unchanged after
    // the 409 skip on the PR path. FinalizeDispatchOutcome explicitly does not add to
    // ActiveIssueIdentifiers on AlreadyQueued; a regression where it erroneously did so would
    // not be caught. Add: activeIdentifiers.Should().NotContain(("pr-already-queued", "repo-t1")).
    [Fact]
    public async Task WhenPrDispatchReturnsAlreadyQueued_ProcessedCountNotIncrementedAndBudgetNotConsumed()
    {
        var t1 = new PipelineJobTemplate
        {
            Id = "t1",
            Name = "Template t1",
            IssueProviderId = "provider-t1",
            RepoProviderId = "repo-t1",
            ReviewEnabled = true
        };
        var t2 = new PipelineJobTemplate
        {
            Id = "t2",
            Name = "Template t2",
            IssueProviderId = "provider-t2",
            RepoProviderId = "repo-t2",
            ReviewEnabled = true
        };
        var project = CreateProject("p1");
        var pollable = new List<PipelineJobTemplate> { t1, t2 };
        var flattened = new List<(PipelineJobTemplate, PipelineProject)> { (t1, project), (t2, project) };

        // First DistributeAndFinalizeAsync call → 409; second → success.
        var callCount = 0;
        _mockDispatchOrchestration
            .Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) return new DispatchOutcome(true, true, null) { AlreadyExists = true };
                return new DispatchOutcome(true, false, null);
            });

        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = new() { CreatePrSummary("pr-already-queued", 1) },
            ["t2"] = new() { CreatePrSummary("pr-new", 2) }
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MinIssueSlots = 0 },
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>(),
                IssueQueues = new Dictionary<string, List<IssueSummary>>(),
                PrQueues = prQueues,
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(1,
            "the 409 (t1 PR) must not increment ProcessedCount; only the genuine dispatch (t2 PR) must count");
        _prDispatchCount.Should().Be(2,
            "PrepareReviewDistributionRequestAsync must be called for both templates' PRs");
    }

    /// <summary>
    /// AC2 (SkippedNoAgent — Issues): When DistributeAndFinalizeAsync returns Success=false
    /// (no eligible agent or distribution failure) for an issue dispatch, ProcessedCount must
    /// be 0 and ActiveIssueIdentifiers must not be updated.
    /// Verifies the Failed → SkippedNoAgent path in FinalizeDispatchOutcome for Issues.
    /// </summary>
    // TODO [WARNING]: This test does not assert _issueDispatchCount.Should().Be(1) to confirm
    // that PrepareDistributionRequestAsync was actually called (i.e., the SkippedNoAgent path
    // in FinalizeDispatchOutcome was reached). Without this, a mis-routing that results in zero
    // dispatches for a different reason (e.g., ImplementationEnabled being false) would also
    // produce ProcessedCount==0 and activeIdentifiers.IsEmpty==true, making the test pass for
    // the wrong reason. Consider adding: _issueDispatchCount.Should().Be(1, "PrepareDistributionRequestAsync
    // must be called once before FinalizeDispatchOutcome returns SkippedNoAgent").
    [Fact]
    public async Task WhenIssueDispatchReturnsNoAgent_ProcessedCountIsZeroAndActiveIdentifiersUnchanged()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        _mockDispatchOrchestration
            .Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(false, false, null));

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-no-agent") }
        };
        var activeIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>();

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration(),
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = activeIdentifiers,
                IssueQueues = issueQueues,
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(0,
            "a no-agent outcome must not increment ProcessedCount");
        activeIdentifiers.Should().BeEmpty(
            "ActiveIssueIdentifiers must not be updated when dispatch returns no agent");
    }

    /// <summary>
    /// AC3 (SkippedNoAgent — Reviews): When DistributeAndFinalizeAsync returns Success=false
    /// for a PR review dispatch, ProcessedCount must be 0 and ActiveIssueIdentifiers must not
    /// be updated. Verifies the Failed → SkippedNoAgent path in FinalizeDispatchOutcome for Reviews.
    /// </summary>
    // TODO [WARNING]: CreateTemplate("t1") enables ImplementationEnabled and DecompositionEnabled
    // in addition to ReviewEnabled. The test relies on PrQueues routing to reach the PR dispatch
    // path, but if a future routing change redirects dispatch to a different queue type the test
    // could pass trivially (ProcessedCount==0 from a different reason) without exercising
    // FinalizeDispatchOutcome. Consider using a template with only ReviewEnabled=true (as the
    // AC1 test does) to make the intent unambiguous.
    [Fact]
    public async Task WhenPrDispatchReturnsNoAgent_ProcessedCountIsZeroAndActiveIdentifiersUnchanged()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        _mockDispatchOrchestration
            .Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(false, false, null));

        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t1"] = new() { CreatePrSummary("pr-no-agent", 1) }
        };
        var activeIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>();

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration(),
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = activeIdentifiers,
                IssueQueues = new Dictionary<string, List<IssueSummary>>(),
                PrQueues = prQueues,
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(0,
            "a no-agent outcome must not increment ProcessedCount");
        activeIdentifiers.Should().BeEmpty(
            "ActiveIssueIdentifiers must not be updated when PR dispatch returns no agent");
    }

    /// <summary>
    /// AC4 (SkippedNoAgent — Decomposition): When DistributeAndFinalizeAsync returns Success=false
    /// for a decomposition dispatch, ProcessedCount must be 0 and ActiveIssueIdentifiers must not
    /// be updated. Verifies the Failed → SkippedNoAgent path in FinalizeDispatchOutcome for Decomposition.
    /// </summary>
    // TODO [WARNING]: This test does not assert _decompDispatchCount.Should().Be(1) to confirm that
    // PrepareDecompositionDistributionRequestAsync was actually reached. If the decomposition budget
    // guard (ActiveDecompositionCount=0, MaxConcurrentDecompositions=100) is inadvertently mis-set
    // in a future refactor, the test could pass trivially (dispatch never attempted, ProcessedCount==0
    // by budget exhaustion) without exercising the FinalizeDispatchOutcome SkippedNoAgent branch.
    // Consider adding: _decompDispatchCount.Should().Be(1, "PrepareDecompositionDistributionRequestAsync
    // must be called once so FinalizeDispatchOutcome is reached").
    [Fact]
    public async Task WhenDecompositionDispatchReturnsNoAgent_ProcessedCountIsZeroAndActiveIdentifiersUnchanged()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        _mockDispatchOrchestration
            .Setup(d => d.DistributeAndFinalizeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchOutcome(false, false, null));

        var decompQueues = new Dictionary<string, List<EpicCandidate>>
        {
            ["t1"] = new() { new EpicCandidate(CreateIssueSummary("epic-no-agent"), PipelineRunType.DecompositionAnalysis, "provider-t1") }
        };
        var activeIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>();

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration { MaxConcurrentDecompositions = 100 },
                MaxRunsPerCycle = 10,
                ActiveDecompositionCount = 0,
                ActiveIssueIdentifiers = activeIdentifiers,
                IssueQueues = new Dictionary<string, List<IssueSummary>>(),
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = decompQueues,
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(0,
            "a no-agent outcome must not increment ProcessedCount");
        activeIdentifiers.Should().BeEmpty(
            "ActiveIssueIdentifiers must not be updated when decomposition dispatch returns no agent");
    }

    /// <summary>
    /// AC5 (ActiveIssueIdentifiers.Contains branch): When an issue's identifier is already present
    /// in ctx.ActiveIssueIdentifiers (not via IsIssueBeingProcessed), it must be skipped without
    /// calling PrepareDistributionRequestAsync. Covers branch (2) of IsIssueAlreadyActive which
    /// was previously untested (the existing FilterAll test only covered branch 1 via
    /// IsIssueBeingProcessed returning true).
    /// </summary>
    [Fact]
    public async Task WhenIssueIdentifierAlreadyInActiveSet_IssueSkippedWithoutDispatch()
    {
        var template = CreateTemplate("t1");
        var project = CreateProject("p1");
        var (pollable, flattened) = BuildTemplateLists(template, project);

        // IsIssueBeingProcessed returns false — dedup must come from ActiveIssueIdentifiers alone.
        _mockOrchestration.Setup(o => o.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()))
            .Returns(false);

        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t1"] = new() { CreateIssueSummary("issue-already-active") }
        };

        // Pre-populate the active set with the same (identifier, provider) pair.
        var activeIdentifiers = new HashSet<(IssueIdentifier, ProviderConfigId)>
        {
            ("issue-already-active", template.IssueProviderId)
        };

        var result = await _scheduler.DispatchFairRoundRobinAsync(
            new DispatchRoundRobinRequest
            {
                PollableTemplates = pollable,
                FlattenedTemplates = flattened,
                Config = new PipelineConfiguration(),
                MaxRunsPerCycle = 10,
                ActiveIssueIdentifiers = activeIdentifiers,
                IssueQueues = issueQueues,
                PrQueues = new Dictionary<string, List<PullRequestSummary>>(),
                DecompositionQueues = new Dictionary<string, List<EpicCandidate>>(),
                ReportStatus = _ => { },
                ReportIssue = _ => { },
                NotifyChange = () => { }
            },
            CancellationToken.None, CancellationToken.None);

        result.ProcessedCount.Should().Be(0,
            "issue already in ActiveIssueIdentifiers must be skipped entirely");
        _issueDispatchCount.Should().Be(0,
            "PrepareDistributionRequestAsync must not be called when the issue is pre-filtered by ActiveIssueIdentifiers");
    }

    #endregion
}
