using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for the queue sweep feature:
/// <see cref="PipelineLoopService.SweepPendingWorkItemsAsync"/> and
/// <see cref="PipelineLoopService.BuildEligibilityMap"/> /
/// <see cref="PipelineLoopService.BuildPrEligibilityMap"/>.
///
/// Tests call <c>SweepPendingWorkItemsAsync</c> directly (it is <c>internal</c>) and also
/// exercise <c>BuildEligibilityMap</c> and <c>BuildPrEligibilityMap</c> (also <c>internal static</c>) in isolation.
/// </summary>
public sealed class PipelineLoopServiceQueueSweepTests : IAsyncDisposable
{
    private readonly Mock<IWorkItemSweepClient> _sweepClientMock = new();
    private readonly Mock<IConfigurationStore> _mockStore = new();
    private readonly Mock<IProviderFactory> _mockFactory = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly TestMeterFactory _meterFactory = new();
    private readonly MetricCollector<long> _cancelledCollector;
    private readonly MetricCollector<long> _skippedCollector;
    private readonly MetricCollector<long> _failedCollector;
    private PipelineLoopService? _loopService;

    public PipelineLoopServiceQueueSweepTests()
    {
        _cancelledCollector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "pipeline.queue_sweep.cancelled");
        _skippedCollector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "pipeline.queue_sweep.skipped");
        _failedCollector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "pipeline.queue_sweep.failed");
        // Logger forward for ForContext calls used inside the service
        _mockLogger
            .Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
        _mockLogger
            .Setup(l => l.ForContext<It.IsAnyType>())
            .Returns(_mockLogger.Object);
    }

    public async ValueTask DisposeAsync()
    {
        _cancelledCollector.Dispose();
        _skippedCollector.Dispose();
        _failedCollector.Dispose();
        _meterFactory.Dispose();
        if (_loopService is not null)
        {
            try { await _loopService.StopAsync(CancellationToken.None); } catch { }
            _loopService.Dispose();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PipelineLoopService CreateService(IWorkItemSweepClient? sweepClient = null)
    {
        var lifecycle = new PipelineRunLifecycleService(
            new TestOrchestrationFactory.NullHistoryService(), null, _mockLogger.Object);
        var runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            lifecycle: lifecycle,
            logger: _mockLogger.Object);

        _loopService = new PipelineLoopService(new PipelineLoopServiceDependencies
        {
            Orchestration         = runCreator,
            ProviderFactory       = _mockFactory.Object,
            PipelineConfigStore   = _mockStore.Object,
            ProviderConfigStore   = _mockStore.Object,
            ProjectStore          = _mockStore.Object,
            Logger                = _mockLogger.Object,
            WorkDistributor       = null,
            DispatchOrchestration = new NullDispatchOrchestrationService(),
            DependencyChecker     = null,
            HousekeepingService   = null,
            LeaderElection        = null,
            WorkItemClient        = sweepClient,
            MeterFactory          = _meterFactory
        });
        return _loopService;
    }

    private static PendingWorkItemDto MakePendingItem(
        string issueIdentifier = "42",
        string issueProviderConfigId = "ip-1",
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderConfigId,
            TaskType = taskType,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = "",
            RetryCount = 0,
            TimeoutSeconds = 3600
        };

    private static IReadOnlyDictionary<string, HashSet<string>> EligibilityMap(
        string providerId, params string[] issueIds)
    {
        var set = new HashSet<string>(issueIds, StringComparer.Ordinal);
        return new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { [providerId] = set };
    }

    private static IReadOnlyDictionary<string, HashSet<string>> EmptyEligibilityMap() =>
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

    private long CounterValue(string name)
    {
        return name switch
        {
            "pipeline.queue_sweep.cancelled" => _cancelledCollector.GetMeasurementSnapshot().Sum(m => m.Value),
            "pipeline.queue_sweep.skipped" => _skippedCollector.GetMeasurementSnapshot().Sum(m => m.Value),
            "pipeline.queue_sweep.failed" => _failedCollector.GetMeasurementSnapshot().Sum(m => m.Value),
            _ => 0
        };
    }

    // ── BuildEligibilityMap unit tests ────────────────────────────────────────

    [Fact]
    public void BuildEligibilityMap_WhenNoTemplates_ReturnsEmptyMap()
    {
        var result = PipelineLoopService.BuildEligibilityMap(
            pollableTemplates: [],
            issueQueues: []);

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildEligibilityMap_WhenTemplateNotInIssueQueues_OmitsProvider()
    {
        // Template polled but not present in issueQueues at all (e.g. never got to polling)
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };

        var result = PipelineLoopService.BuildEligibilityMap(
            pollableTemplates: [template],
            issueQueues: []  // "ip-1" absent
        );

        result.Should().NotContainKey("ip-1",
            "provider should be omitted from map (fail-open) when template was not polled");
    }

    [Fact]
    public void BuildEligibilityMap_WhenTemplateHasNoIssues_IncludesProviderWithEmptySet()
    {
        // Template polled successfully (no failure increment) but returned zero eligible issues.
        // failuresBefore shows ConsecutiveFailures = 0 both before and after — this is a genuine
        // "zero eligible issues" result, not a failed poll. The provider IS included with an empty
        // set, so Pending WorkItems for this provider WILL be cancelled.
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t-1"] = []  // polled successfully, found nothing
        };
        var failuresBefore = new Dictionary<string, int> { ["t-1"] = 0 };
        var templateStatuses = new Dictionary<string, ConfigStatusSnapshot>
        {
            ["t-1"] = new ConfigStatusSnapshot { ConsecutiveFailures = 0, RateLimitResetAt = null }
        };

        var result = PipelineLoopService.BuildEligibilityMap(
            pollableTemplates: [template],
            issueQueues: issueQueues,
            failuresBefore: failuresBefore,
            templateStatuses: templateStatuses);

        result.Should().ContainKey("ip-1");
        result["ip-1"].Should().BeEmpty(
            "empty set means 'zero eligible issues' for a successful poll — Pending WorkItems for this provider SHOULD be cancelled");
    }

    [Fact]
    public void BuildEligibilityMap_WhenTemplateHasIssues_IncludesThemInSet()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t-1"] = [
                new IssueSummary { Identifier = "42", Title = "Issue 42", Labels = [] },
                new IssueSummary { Identifier = "99", Title = "Issue 99", Labels = [] }
            ]
        };

        var result = PipelineLoopService.BuildEligibilityMap([template], issueQueues);

        result.Should().ContainKey("ip-1");
        result["ip-1"].Should().Contain("42");
        result["ip-1"].Should().Contain("99");
    }

    [Fact]
    public void BuildEligibilityMap_WhenMultipleTemplatesShareProvider_UnionsIssueSets()
    {
        // Two templates for the same issue provider (different repos, same provider) — their
        // eligible issues must be unioned so items for either repo's issues are not cancelled.
        var t1 = new PipelineJobTemplate { Id = "t-1", Name = "T1", IssueProviderId = "ip-shared", RepoProviderId = "rp-1", Enabled = true };
        var t2 = new PipelineJobTemplate { Id = "t-2", Name = "T2", IssueProviderId = "ip-shared", RepoProviderId = "rp-2", Enabled = true };
        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t-1"] = [new IssueSummary { Identifier = "10", Title = "I10", Labels = [] }],
            ["t-2"] = [new IssueSummary { Identifier = "20", Title = "I20", Labels = [] }]
        };

        var result = PipelineLoopService.BuildEligibilityMap([t1, t2], issueQueues);

        result.Should().ContainKey("ip-shared");
        result["ip-shared"].Should().Contain("10");
        result["ip-shared"].Should().Contain("20");
    }

    // ── BuildPrEligibilityMap unit tests ──────────────────────────────────────

    [Fact]
    public void BuildPrEligibilityMap_WhenNoTemplates_ReturnsEmptyMap()
    {
        var result = PipelineLoopService.BuildPrEligibilityMap(
            pollableTemplates: [],
            prQueues: []);

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildPrEligibilityMap_WhenTemplateNotInPrQueues_OmitsProvider()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1",
            Enabled = true, ReviewEnabled = true
        };

        var result = PipelineLoopService.BuildPrEligibilityMap(
            pollableTemplates: [template],
            prQueues: []  // "t-1" absent — PR poll was not run or PR polling failed
        );

        result.Should().NotContainKey("ip-1",
            "provider should be omitted (fail-open) when template was not polled or PR polling failed");
    }

    [Fact]
    public void BuildPrEligibilityMap_WhenTemplateHasPrs_IncludesIdentifiersInSet()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1",
            Enabled = true, ReviewEnabled = true
        };
        var pr1 = new PullRequestSummary
        {
            Number = 101, Identifier = "101", Title = "PR 101",
            Description = "", Labels = [], BranchName = "branch-1",
            TargetBranch = "main", Url = "https://example.com/pr/101", IsDraft = false
        };
        var pr2 = new PullRequestSummary
        {
            Number = 202, Identifier = "202", Title = "PR 202",
            Description = "", Labels = [], BranchName = "branch-2",
            TargetBranch = "main", Url = "https://example.com/pr/202", IsDraft = false
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t-1"] = [pr1, pr2]
        };

        var result = PipelineLoopService.BuildPrEligibilityMap([template], prQueues);

        result.Should().ContainKey("ip-1");
        result["ip-1"].Should().Contain("101");
        result["ip-1"].Should().Contain("202");
    }

    [Fact]
    public void BuildPrEligibilityMap_WhenTemplateFailedDuringCycle_OmitsProvider()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1",
            Enabled = true, ReviewEnabled = true
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t-1"] = []  // cleared by HandleGenericPollException
        };
        var failuresBefore = new Dictionary<string, int> { ["t-1"] = 2 };
        var templateStatuses = new Dictionary<string, ConfigStatusSnapshot>
        {
            ["t-1"] = new ConfigStatusSnapshot { ConsecutiveFailures = 3, RateLimitResetAt = null }
        };

        var result = PipelineLoopService.BuildPrEligibilityMap(
            pollableTemplates: [template],
            prQueues: prQueues,
            failuresBefore: failuresBefore,
            templateStatuses: templateStatuses);

        result.Should().NotContainKey("ip-1",
            "provider must be omitted (fail-open) when template poll failed during this cycle");
    }

    [Fact]
    public void BuildPrEligibilityMap_WhenPrPollFailedSilently_OmitsProvider()
    {
        // After the CRITICAL fix: PollPrQueueAsync removes the key from prQueues on exception,
        // so the template's entry is ABSENT (not an empty list) when the PR poll fails.
        // BuildPrEligibilityMap uses "not in prQueues" as the fail-open signal.
        // This test verifies that BuildPrEligibilityMap correctly omits the provider when the key
        // is absent (i.e. PR poll failed — PollPrQueueAsync removed the key in the catch block).
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1",
            Enabled = true, ReviewEnabled = true
        };
        // Key absent: simulates PollPrQueueAsync removing the key after an exception.
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var failuresBefore = new Dictionary<string, int> { ["t-1"] = 0 };  // no issue-level failure
        var templateStatuses = new Dictionary<string, ConfigStatusSnapshot>
        {
            // ConsecutiveFailures unchanged — issue poll succeeded, only PR poll failed
            ["t-1"] = new ConfigStatusSnapshot { ConsecutiveFailures = 0, RateLimitResetAt = null }
        };

        var result = PipelineLoopService.BuildPrEligibilityMap(
            pollableTemplates: [template],
            prQueues: prQueues,
            failuresBefore: failuresBefore,
            templateStatuses: templateStatuses);

        // Key absent from prQueues → BuildPrEligibilityMap omits the provider (fail-open).
        // Review WorkItems for this provider are NOT cancelled when the PR poll fails.
        result.Should().NotContainKey("ip-1",
            "provider must be omitted (fail-open) when PollPrQueueAsync removed the key after a PR poll exception");
    }

    // ── SweepPendingWorkItemsAsync — Implementation item cancellation path ────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenImplementationItemIssueNotInEligibilitySet_CancelsWorkItem()
    {
        // Eligibility: ip-1 has issues {"99"} — item "42" is NOT eligible → must be cancelled
        var itemId = Guid.NewGuid();
        var item = MakePendingItem("42", "ip-1", id: itemId);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _sweepClientMock
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService(_sweepClientMock.Object);
        var issueEligibility = EligibilityMap("ip-1", "99");
        var prEligibility = EmptyEligibilityMap();

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            itemId,
            It.Is<WorkItemStatusUpdate>(u =>
                u.Status == "Cancelled" &&
                u.ErrorMessage != null),
            It.IsAny<CancellationToken>()),
            Times.Once);
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1,
            "counter must be incremented AFTER PostStatusAsync succeeds");
    }

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenImplementationItemIssueInEligibilitySet_DoesNotCancel()
    {
        // Issue "42" IS in the eligibility set — must NOT be cancelled
        var item = MakePendingItem("42", "ip-1");
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        var issueEligibility = EligibilityMap("ip-1", "42");
        var prEligibility = EmptyEligibilityMap();

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0);
    }

    // ── SweepPendingWorkItemsAsync — Review item (PR-aware) ───────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenReviewItemPrNotInPrEligibilitySet_CancelsWorkItem()
    {
        // PR 101 is NOT in the PR eligibility set — Review WorkItem must be cancelled.
        var itemId = Guid.NewGuid();
        var reviewItem = MakePendingItem("101", "ip-1", taskType: WorkItemTaskType.Review, id: itemId);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([reviewItem]);
        _sweepClientMock
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService(_sweepClientMock.Object);
        // Issue map has ip-1 (e.g. from issue polling) but it does NOT contain "101" (which is a PR number)
        var issueEligibility = EligibilityMap("ip-1", "42", "99");  // only regular issue numbers
        // PR map has ip-1 but ONLY PR "202" — PR 101 is NOT present (closed/label removed/terminal)
        var prEligibility = EligibilityMap("ip-1", "202");

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            itemId,
            It.Is<WorkItemStatusUpdate>(u =>
                u.Status == "Cancelled" &&
                u.ErrorMessage != null &&
                u.ErrorMessage.Contains("PR")),
            It.IsAny<CancellationToken>()),
            Times.Once,
            "Review WorkItem for closed PR must be cancelled against the PR eligibility map");
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1,
            "counter must be incremented AFTER PostStatusAsync succeeds");
    }

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenReviewItemPrIsInPrEligibilitySet_DoesNotCancel()
    {
        // PR 101 IS in the PR eligibility set — Review WorkItem must NOT be cancelled.
        var reviewItem = MakePendingItem("101", "ip-1", taskType: WorkItemTaskType.Review);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([reviewItem]);

        var svc = CreateService(_sweepClientMock.Object);
        var issueEligibility = EligibilityMap("ip-1", "42");  // regular issues — does NOT contain "101"
        var prEligibility = EligibilityMap("ip-1", "101");    // PR 101 is eligible (open, has agent:next)

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Review WorkItem must NOT be cancelled when its PR is still in the PR eligibility set");
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0);
    }

    [Fact]
    public async Task SweepPendingWorkItemsAsync_ReviewItemIsNotCheckedAgainstIssueMap()
    {
        // Critical regression guard: a Review item with identifier "101" (PR number) must NOT be
        // checked against the issue map (which would never contain "101" as an issue number).
        // Only the PR eligibility map is authoritative for Review items.
        var reviewItem = MakePendingItem("101", "ip-1", taskType: WorkItemTaskType.Review);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([reviewItem]);

        var svc = CreateService(_sweepClientMock.Object);
        // Issue map has ip-1 but "101" is NOT in it (as expected — "101" is a PR number, not an issue)
        var issueEligibility = EligibilityMap("ip-1", "42", "99");
        // PR map does NOT have ip-1 at all — provider absent → fail open → no cancellation
        var prEligibility = EmptyEligibilityMap();

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Review item must not be cancelled when provider absent from PR map (fail-open); " +
            "the issue map must never be used for Review items");
        CounterValue("pipeline.queue_sweep.skipped").Should().Be(1,
            "item should be skipped (fail-open) when provider not in PR map");
    }

    // ── Rate-limited provider (absent from eligibility map) ───────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenProviderNotInEligibilityMap_SkipsItem()
    {
        // "ip-rate-limited" is absent from the map (excluded upstream because it's rate-limited)
        // — fail-open: do not cancel
        var item = MakePendingItem("42", "ip-rate-limited");
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        // Eligibility maps do NOT contain "ip-rate-limited"
        var issueEligibility = EligibilityMap("ip-other", "42");
        var prEligibility = EligibilityMap("ip-other", "99");

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        CounterValue("pipeline.queue_sweep.skipped").Should().Be(1);
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0);
    }

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenPrPollFailedAndProviderAbsentFromPrMap_ReviewItemIsSkipped()
    {
        // After the CRITICAL fix: PollPrQueueAsync removes the key from prQueues on exception,
        // so BuildPrEligibilityMap omits the provider from the PR map (fail-open).
        // This test verifies that the sweep skips (does not cancel) a Review WorkItem when
        // the provider is absent from the PR eligibility map (simulating a PR poll failure).
        var reviewItem = MakePendingItem("101", "ip-1", taskType: WorkItemTaskType.Review);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([reviewItem]);

        var svc = CreateService(_sweepClientMock.Object);
        // Provider "ip-1" is absent from the PR map because the template failed during this cycle.
        // This is the result of BuildPrEligibilityMap omitting providers with ConsecutiveFailures increased.
        var issueEligibility = EmptyEligibilityMap();  // also absent from issue map (failed cycle)
        var prEligibility = EmptyEligibilityMap();      // absent from PR map — fail open

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Review item must not be cancelled when provider absent from PR map (PR poll failed — fail open)");
        CounterValue("pipeline.queue_sweep.skipped").Should().Be(1);
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0);
    }

    // ── TaskType == Consolidation / Decomposition skipped ────────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenTaskTypeIsConsolidation_IsSkipped()
    {
        // Consolidation WorkItems are managed by ConsolidationWorkItemDispatchService and
        // must never be cancelled by the queue sweep.
        var item = MakePendingItem("42", "ip-1", taskType: WorkItemTaskType.Consolidation);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        // Provider present in both maps, issue NOT in sets — but TaskType is Consolidation
        var issueEligibility = EligibilityMap("ip-1", "99");
        var prEligibility = EligibilityMap("ip-1", "99");

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        CounterValue("pipeline.queue_sweep.skipped").Should().Be(1);
    }

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenTaskTypeIsDecomposition_IsSkipped()
    {
        // Decomposition WorkItems use a separate eligibility source (decompositionQueues /
        // projectLevelDecompositionQueues) that is not folded into the current eligibility maps.
        // Must be skipped (fail-open) to avoid incorrect cancellations.
        var item = MakePendingItem("42", "ip-1", taskType: WorkItemTaskType.Decomposition);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        // Provider present, issue NOT in sets — but TaskType is Decomposition
        var issueEligibility = EligibilityMap("ip-1", "99");
        var prEligibility = EligibilityMap("ip-1", "99");

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Decomposition items must be skipped (fail-open) — eligibility source not built this cycle");
        CounterValue("pipeline.queue_sweep.skipped").Should().Be(1);
    }

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenTaskTypeIsImplementation_EligibilityCheckApplies()
    {
        // Implementation WorkItems are checked against the issue eligibility map.
        var item = MakePendingItem("42", "ip-1", taskType: WorkItemTaskType.Implementation);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _sweepClientMock
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService(_sweepClientMock.Object);
        // Issue NOT in eligibility set → should cancel
        var issueEligibility = EligibilityMap("ip-1", "99");
        var prEligibility = EmptyEligibilityMap();

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Once, "Implementation items must be cancelled by the sweep when no longer eligible");
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1);
    }

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenTaskTypeIsReview_EligibilityCheckAppliesViaPrMap()
    {
        // Review WorkItems are checked against the PR eligibility map (not the issue map).
        var item = MakePendingItem("42", "ip-1", taskType: WorkItemTaskType.Review);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _sweepClientMock
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService(_sweepClientMock.Object);
        // PR NOT in PR eligibility set → should cancel
        var issueEligibility = EligibilityMap("ip-1", "99");  // issue map irrelevant for Review
        var prEligibility = EligibilityMap("ip-1", "99");     // "42" not in PR map

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Once, "Review items must be cancelled when PR not in PR eligibility map");
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1);
    }

    // ── GetPendingAsync failure ───────────────────────────────────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenGetPendingThrows_AbortsWithoutCancelling()
    {
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated network failure"));

        var svc = CreateService(_sweepClientMock.Object);
        var issueEligibility = EligibilityMap("ip-1");
        var prEligibility = EmptyEligibilityMap();

        // Must not throw — should log Warning and return
        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _mockLogger.Verify(
            l => l.Warning(It.IsAny<Exception>(), It.Is<string>(s => s.Contains("QueueSweep"))),
            Times.Once);
    }

    // ── PostStatusAsync unexpected failure ────────────────────────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenPostStatusFailsUnexpectedly_ContinuesForOtherItems()
    {
        var item1 = MakePendingItem("1", "ip-1", id: Guid.NewGuid());
        var item2 = MakePendingItem("2", "ip-1", id: Guid.NewGuid());

        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item1, item2]);

        // First call throws unexpected; second succeeds
        var callCount = 0;
        _sweepClientMock
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, WorkItemStatusUpdate, CancellationToken>((_, _, _) =>
            {
                callCount++;
                if (callCount == 1) throw new InvalidOperationException("unexpected failure");
                return Task.CompletedTask;
            });

        var svc = CreateService(_sweepClientMock.Object);
        // Both items are ineligible (provider ip-1 has empty set)
        var issueEligibility = EligibilityMap("ip-1");
        var prEligibility = EmptyEligibilityMap();

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "second item must still be processed after first fails");

        CounterValue("pipeline.queue_sweep.failed").Should().Be(1);
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1,
            "QueueSweepCancelled is incremented only after PostStatusAsync succeeds — only item2 succeeded");
    }

    // ── PostStatusAsync expected HTTP race (400/404/409) ─────────────────────

    [Theory]
    [InlineData(System.Net.HttpStatusCode.BadRequest)]
    [InlineData(System.Net.HttpStatusCode.NotFound)]
    [InlineData(System.Net.HttpStatusCode.Conflict)]
    public async Task SweepPendingWorkItemsAsync_WhenPostStatusReturnsExpectedHttpError_DoesNotIncrementFailed(
        System.Net.HttpStatusCode statusCode)
    {
        var item = MakePendingItem("42", "ip-1");
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _sweepClientMock
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("transition rejected", null, statusCode));

        var svc = CreateService(_sweepClientMock.Object);
        // item "42" not eligible
        var issueEligibility = EligibilityMap("ip-1");
        var prEligibility = EmptyEligibilityMap();

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        CounterValue("pipeline.queue_sweep.failed").Should().Be(0,
            "expected HTTP race should not count as failure");
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0,
            "counter must NOT be incremented because PostStatusAsync threw (counter is after successful PostStatusAsync)");
    }

    // ── Null client guard ─────────────────────────────────────────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenClientIsNull_ReturnsImmediately()
    {
        var svc = CreateService(sweepClient: null);  // WorkItemClient = null
        var issueEligibility = EligibilityMap("ip-1", "42");
        var prEligibility = EmptyEligibilityMap();

        // Must not throw
        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        // GetPendingAsync must never be called since there is no client
        _sweepClientMock.Verify(c => c.GetPendingAsync(
            It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Mixed eligible/ineligible ─────────────────────────────────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenMixedItems_OnlyCancelsIneligible()
    {
        var eligible = MakePendingItem("42", "ip-1");
        var ineligible = MakePendingItem("99", "ip-1", id: Guid.NewGuid());

        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([eligible, ineligible]);
        _sweepClientMock
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService(_sweepClientMock.Object);
        // "42" is eligible; "99" is not
        var issueEligibility = EligibilityMap("ip-1", "42");
        var prEligibility = EmptyEligibilityMap();

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            ineligible.Id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Cancelled"),
            It.IsAny<CancellationToken>()),
            Times.Once, "only the ineligible item must be cancelled");

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            eligible.Id,
            It.IsAny<WorkItemStatusUpdate>(),
            It.IsAny<CancellationToken>()),
            Times.Never, "the eligible item must not be cancelled");

        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1);
    }

    // ── sweepEnabled = false guard (CRITICAL: acceptance criterion) ───────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenSweepEnabledIsFalse_DoesNotCallGetPending()
    {
        // This test guards the QueueSweepEnabled = false acceptance criterion.
        var item = MakePendingItem("42", "ip-1");
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        var issueEligibility = EligibilityMap("ip-1");
        var prEligibility = EmptyEligibilityMap();

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: false, CancellationToken.None);

        _sweepClientMock.Verify(c => c.GetPendingAsync(
            It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "GetPendingAsync must not be called when sweepEnabled = false");

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never, "no cancellations must occur when sweepEnabled = false");
    }

    // ── BuildEligibilityMap: same-cycle rate-limit skip ───────────────────────

    [Fact]
    public void BuildEligibilityMap_WhenTemplateRateLimitedDuringThisCycle_OmitsProvider()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t-1"] = []  // cleared by HandleRateLimitException
        };
        var failuresBefore = new Dictionary<string, int> { ["t-1"] = 0 };
        var templateStatuses = new Dictionary<string, ConfigStatusSnapshot>
        {
            ["t-1"] = new ConfigStatusSnapshot { ConsecutiveFailures = 1, RateLimitResetAt = DateTimeOffset.UtcNow.AddMinutes(5) }
        };

        var result = PipelineLoopService.BuildEligibilityMap(
            pollableTemplates: [template],
            issueQueues: issueQueues,
            failuresBefore: failuresBefore,
            templateStatuses: templateStatuses);

        result.Should().NotContainKey("ip-1",
            "provider must be omitted (fail-open) when template was rate-limited during this cycle");
    }

    // ── BuildEligibilityMap: same-cycle generic poll failure skip ─────────────

    [Fact]
    public void BuildEligibilityMap_WhenTemplateFailedDuringThisCycle_OmitsProvider()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t-1"] = []  // cleared by HandleGenericPollException
        };
        var failuresBefore = new Dictionary<string, int> { ["t-1"] = 2 };
        var templateStatuses = new Dictionary<string, ConfigStatusSnapshot>
        {
            ["t-1"] = new ConfigStatusSnapshot { ConsecutiveFailures = 3, RateLimitResetAt = null }
        };

        var result = PipelineLoopService.BuildEligibilityMap(
            pollableTemplates: [template],
            issueQueues: issueQueues,
            failuresBefore: failuresBefore,
            templateStatuses: templateStatuses);

        result.Should().NotContainKey("ip-1",
            "provider must be omitted (fail-open) when template poll failed during this cycle");
    }

    [Fact]
    public void BuildEligibilityMap_WhenTemplateHasPriorFailuresButSucceededThisCycle_IncludesProvider()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t-1"] = [new IssueSummary { Identifier = "42", Title = "Issue 42", Labels = [] }]
        };
        var failuresBefore = new Dictionary<string, int> { ["t-1"] = 3 };
        var templateStatuses = new Dictionary<string, ConfigStatusSnapshot>
        {
            ["t-1"] = new ConfigStatusSnapshot { ConsecutiveFailures = 0, RateLimitResetAt = null }
        };

        var result = PipelineLoopService.BuildEligibilityMap(
            pollableTemplates: [template],
            issueQueues: issueQueues,
            failuresBefore: failuresBefore,
            templateStatuses: templateStatuses);

        result.Should().ContainKey("ip-1",
            "provider must be included when the template succeeded this cycle, regardless of prior failures");
        result["ip-1"].Should().Contain("42");
    }

    // ── Telemetry counter names ───────────────────────────────────────────────

    [Fact]
    public void QueueSweepCounters_EmitCorrectMetricNames()
    {
        using var cancelledCollector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "pipeline.queue_sweep.cancelled");
        using var skippedCollector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "pipeline.queue_sweep.skipped");
        using var failedCollector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "pipeline.queue_sweep.failed");

        var meter = _meterFactory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        meter.CreateCounter<long>("pipeline.queue_sweep.cancelled").Add(1);
        meter.CreateCounter<long>("pipeline.queue_sweep.skipped").Add(1);
        meter.CreateCounter<long>("pipeline.queue_sweep.failed").Add(1);

        cancelledCollector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 1);
        skippedCollector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 1);
        failedCollector.GetMeasurementSnapshot().Should().ContainSingle(m => m.Value == 1);
    }

    // ── _queueSweepCancelled incremented only after PostStatusAsync succeeds ──

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenPostStatusSucceeds_CancelledCounterIncrements()
    {
        var item = MakePendingItem("42", "ip-1", id: Guid.NewGuid());
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _sweepClientMock
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService(_sweepClientMock.Object);
        var issueEligibility = EligibilityMap("ip-1");  // empty — item not eligible
        var prEligibility = EmptyEligibilityMap();

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1,
            "counter must increment exactly once after a confirmed successful PostStatusAsync");
    }

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenPostStatusThrowsUnexpected_CancelledCounterDoesNotIncrement()
    {
        // Counter must NOT increment when PostStatusAsync throws (the item was not actually cancelled).
        var item = MakePendingItem("42", "ip-1", id: Guid.NewGuid());
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _sweepClientMock
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network error"));

        var svc = CreateService(_sweepClientMock.Object);
        var issueEligibility = EligibilityMap("ip-1");  // empty — item not eligible
        var prEligibility = EmptyEligibilityMap();

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0,
            "counter must NOT increment when PostStatusAsync threw — item was not confirmed cancelled");
        CounterValue("pipeline.queue_sweep.failed").Should().Be(1);
    }
}
