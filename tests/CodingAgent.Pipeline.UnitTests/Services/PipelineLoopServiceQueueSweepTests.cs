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
/// <see cref="PipelineLoopService.BuildEligibilityMap"/>.
///
/// Tests call <c>SweepPendingWorkItemsAsync</c> directly (it is <c>internal</c>) and also
/// exercise <c>BuildEligibilityMap</c> / <c>BuildPrEligibilityMap</c> (also <c>internal static</c>) in isolation.
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
        // Template does not appear in prQueues (ReviewEnabled = false, or PR poll threw)
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };

        var result = PipelineLoopService.BuildPrEligibilityMap(
            pollableTemplates: [template],
            prQueues: []  // "t-1" absent — fail open
        );

        result.Should().NotContainKey("ip-1",
            "provider should be omitted (fail-open) when template PR queue was not polled this cycle");
    }

    [Fact]
    public void BuildPrEligibilityMap_WhenTemplateHasPrs_IncludesThemInSet()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t-1"] = [
                new PullRequestSummary { Number = 101, Identifier = "101", Title = "PR 101", Description = "", Labels = [], BranchName = "feat/a", TargetBranch = "main", Url = "", IsDraft = false },
                new PullRequestSummary { Number = 202, Identifier = "202", Title = "PR 202", Description = "", Labels = [], BranchName = "feat/b", TargetBranch = "main", Url = "", IsDraft = false }
            ]
        };

        var result = PipelineLoopService.BuildPrEligibilityMap([template], prQueues);

        result.Should().ContainKey("ip-1");
        result["ip-1"].Should().Contain("101");
        result["ip-1"].Should().Contain("202");
    }

    [Fact]
    public void BuildPrEligibilityMap_WhenPrPollFailedDuringCycle_OmitsProvider()
    {
        // PollPrQueueAsync intentionally omits the prQueues entry on failure (does NOT write a
        // present-but-empty list). This means BuildPrEligibilityMap sees an absent key and fails
        // open, rather than treating an empty result from a failed poll as "zero eligible PRs"
        // which would incorrectly cancel all Pending Review WorkItems for that provider.
        // TODO [WARNING]: This test is functionally identical to BuildPrEligibilityMap_WhenTemplateNotInPrQueues_OmitsProvider —
        // both pass an empty prQueues with "t-1" absent and assert "ip-1" is omitted. Neither test
        // exercises the ConsecutiveFailures guard (line ~307 in BuildPrEligibilityMap) or distinguishes
        // a "PR poll threw" scenario from a "ReviewEnabled=false" scenario via the absent-key path.
        // A genuine poll-failure test that exercises the same-cycle-failure guard should pass
        // failuresBefore and templateStatuses showing a ConsecutiveFailures increment (matching the
        // pattern of BuildPrEligibilityMap_WhenTemplateRateLimitedDuringCycle_OmitsProvider but for
        // the ConsecutiveFailures > before branch rather than the RateLimitResetAt branch).
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        // Simulate what PollPrQueueAsync produces on failure: the key is absent.
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();  // "t-1" absent — poll threw

        var result = PipelineLoopService.BuildPrEligibilityMap(
            pollableTemplates: [template],
            prQueues: prQueues);

        result.Should().NotContainKey("ip-1",
            "provider must be omitted (fail-open) when the template PR poll threw — " +
            "PollPrQueueAsync omits the prQueues entry on failure so the absent-key guard fires");
    }

    [Fact]
    public void BuildPrEligibilityMap_WhenTemplateRateLimitedDuringCycle_OmitsProvider()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            ["t-1"] = []
        };
        var failuresBefore = new Dictionary<string, int> { ["t-1"] = 0 };
        var templateStatuses = new Dictionary<string, ConfigStatusSnapshot>
        {
            ["t-1"] = new ConfigStatusSnapshot { ConsecutiveFailures = 1, RateLimitResetAt = DateTimeOffset.UtcNow.AddMinutes(5) }
        };

        var result = PipelineLoopService.BuildPrEligibilityMap(
            pollableTemplates: [template],
            prQueues: prQueues,
            failuresBefore: failuresBefore,
            templateStatuses: templateStatuses);

        result.Should().NotContainKey("ip-1",
            "provider must be omitted (fail-open) when template was rate-limited during this cycle");
    }

    // ── SweepPendingWorkItemsAsync — cancellation path ────────────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenItemIssueNotInEligibilitySet_CancelsWorkItem()
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

        await svc.SweepPendingWorkItemsAsync(issueEligibility, EmptyEligibilityMap(), sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            itemId,
            It.Is<WorkItemStatusUpdate>(u =>
                u.Status == "Cancelled" &&
                u.ErrorMessage != null),
            It.IsAny<CancellationToken>()),
            Times.Once);
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1);
    }

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenItemIssueInEligibilitySet_DoesNotCancel()
    {
        // Issue "42" IS in the eligibility set — must NOT be cancelled
        var item = MakePendingItem("42", "ip-1");
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        var issueEligibility = EligibilityMap("ip-1", "42");

        await svc.SweepPendingWorkItemsAsync(issueEligibility, EmptyEligibilityMap(), sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0);
    }

    // ── Review WorkItem — PR eligibility ──────────────────────────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenReviewItemPrNotInPrEligibilitySet_CancelsItem()
    {
        // PR "101" is NOT in the PR eligibility set (PR closed, agent:next removed, etc.) → cancel
        var itemId = Guid.NewGuid();
        var item = MakePendingItem("101", "ip-1", taskType: WorkItemTaskType.Review, id: itemId);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _sweepClientMock
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService(_sweepClientMock.Object);
        // Issue map: ip-1 has issues; PR map: ip-1 has only PR "999" (not "101")
        var issueEligibility = EligibilityMap("ip-1", "42");
        var prEligibility = EligibilityMap("ip-1", "999");

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            itemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Cancelled" && u.ErrorMessage != null),
            It.IsAny<CancellationToken>()),
            Times.Once, "Review item whose PR is not in prEligibilitySet must be cancelled");
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1);
    }

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenReviewItemPrIsInPrEligibilitySet_DoesNotCancel()
    {
        // PR "101" IS in the PR eligibility set — must NOT be cancelled
        var item = MakePendingItem("101", "ip-1", taskType: WorkItemTaskType.Review);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        var issueEligibility = EligibilityMap("ip-1", "42");
        var prEligibility = EligibilityMap("ip-1", "101");

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never, "Review item whose PR is still eligible must not be cancelled");
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0);
    }

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenReviewItemProviderNotInPrMap_SkipsItem()
    {
        // PR poll failed for "ip-1" this cycle — provider is absent from prEligibilityMap → fail open
        var item = MakePendingItem("101", "ip-1", taskType: WorkItemTaskType.Review);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        // prEligibilityMap does NOT contain "ip-1" (poll failed / rate-limited)
        var issueEligibility = EligibilityMap("ip-1", "42");
        var prEligibility = EligibilityMap("ip-other", "101");

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never, "Review item must not be cancelled when its provider is missing from the PR map");
        CounterValue("pipeline.queue_sweep.skipped").Should().Be(1);
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0);
    }

    // ── Implementation item uses issue map; Review item uses PR map ───────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_ImplementationUsesIssueMap_ReviewUsesPrMap()
    {
        // Implementation item "42" is NOT in issue map → cancel
        // Review item "101" IS in PR map → do NOT cancel
        var implItem = MakePendingItem("42", "ip-1", taskType: WorkItemTaskType.Implementation, id: Guid.NewGuid());
        var reviewItem = MakePendingItem("101", "ip-1", taskType: WorkItemTaskType.Review, id: Guid.NewGuid());

        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([implItem, reviewItem]);
        _sweepClientMock
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService(_sweepClientMock.Object);
        var issueEligibility = EligibilityMap("ip-1", "99");  // "42" not present → impl cancelled
        var prEligibility = EligibilityMap("ip-1", "101");     // "101" present → review kept

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            implItem.Id, It.Is<WorkItemStatusUpdate>(u => u.Status == "Cancelled"), It.IsAny<CancellationToken>()),
            Times.Once, "Implementation item not in issue map must be cancelled");
        _sweepClientMock.Verify(c => c.PostStatusAsync(
            reviewItem.Id, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never, "Review item present in PR map must not be cancelled");
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1);
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
        // Eligibility map does NOT contain "ip-rate-limited"
        var issueEligibility = EligibilityMap("ip-other", "42");

        await svc.SweepPendingWorkItemsAsync(issueEligibility, EmptyEligibilityMap(), sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        CounterValue("pipeline.queue_sweep.skipped").Should().Be(1);
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0);
    }

    // ── TaskType skipping ─────────────────────────────────────────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenTaskTypeIsConsolidation_IsSkipped()
    {
        // Consolidation WorkItems are dispatched synchronously via KubernetesWorkDistributor
        // and must never be cancelled by the queue sweep.
        var item = MakePendingItem("42", "ip-1", taskType: WorkItemTaskType.Consolidation);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        // Provider present, issue NOT in set — but TaskType is Consolidation
        var issueEligibility = EligibilityMap("ip-1", "99");

        await svc.SweepPendingWorkItemsAsync(issueEligibility, EmptyEligibilityMap(), sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        CounterValue("pipeline.queue_sweep.skipped").Should().Be(1);
    }

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenTaskTypeIsDecomposition_IsSkipped()
    {
        // Decomposition WorkItems are not swept: their eligibility source (decompositionQueues,
        // projectLevelDecompositionQueues) is not yet folded into the sweep maps — skip to
        // avoid incorrect cancellations (fail-open).
        var item = MakePendingItem("42", "ip-1", taskType: WorkItemTaskType.Decomposition);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        // Even with the provider present and the issue absent from the map, Decomposition must be skipped
        var issueEligibility = EligibilityMap("ip-1", "99");

        await svc.SweepPendingWorkItemsAsync(issueEligibility, EmptyEligibilityMap(), sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never, "Decomposition items must not be cancelled by the sweep (fail-open)");
        CounterValue("pipeline.queue_sweep.skipped").Should().Be(1);
    }

    [Theory]
    [InlineData(WorkItemTaskType.Review)]
    [InlineData(WorkItemTaskType.Implementation)]
    public async Task SweepPendingWorkItemsAsync_WhenTaskTypeIsReviewOrImplementation_EligibilityCheckApplies(
        WorkItemTaskType taskType)
    {
        // Review and Implementation WorkItems are swept when their issue/PR is no longer eligible.
        // The maps are intentionally ASYMMETRIC so the test fails if the wrong map is consulted:
        //   - issueEligibility has "42" present → Implementation item would be KEPT (not cancelled)
        //     if incorrectly routed to the issue map.
        //   - prEligibility has "42" absent → Review item should be CANCELLED when routed to the PR map.
        // For Implementation: issueEligibility does NOT contain "42" → cancel.
        // For Review: prEligibility does NOT contain "42" → cancel.
        // Both cases should cancel. If a Review item were routed to the issue map (which DOES contain
        // "42"), it would NOT be cancelled and the test would fail — proving the routing is correct.
        // TODO [WARNING]: The cross-routing sensitivity claim above is incorrect for the Implementation
        // case. The comment says issueEligibility "DOES contain '42'" to provide routing sensitivity,
        // but issueEligibility = EligibilityMap("ip-1", "99") — "42" is NOT present in either map.
        // Both maps lack "42", so this test would pass even if routing were inverted (Review reading
        // issueEligibility, Implementation reading prEligibility) or both types read the same map.
        // The companion test SweepPendingWorkItemsAsync_WhenTaskTypeIsReviewOrImplementation_UsesCorrectMap
        // does use genuinely asymmetric maps and correctly detects routing errors; that test provides
        // the actual routing guarantee. Consider fixing or removing the misleading comment here.
        var item = MakePendingItem("42", "ip-1", taskType: taskType);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _sweepClientMock
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService(_sweepClientMock.Object);
        // Issue map: "42" IS present (would keep an Implementation item if it read prEligibility)
        // PR map:    "42" is NOT present (would cancel a Review item correctly)
        // For Implementation: "42" is absent from issueEligibility (only "99" is present) → cancel.
        // For Review:         "42" is absent from prEligibility (only "999" is present) → cancel.
        var issueEligibility = EligibilityMap("ip-1", "99");   // "42" absent → impl cancelled
        var prEligibility = EligibilityMap("ip-1", "999");      // "42" absent → review cancelled

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Once, $"{taskType} items must be cancelled by the sweep when no longer eligible");
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1);
    }

    [Theory]
    [InlineData(WorkItemTaskType.Review)]
    [InlineData(WorkItemTaskType.Implementation)]
    public async Task SweepPendingWorkItemsAsync_WhenTaskTypeIsReviewOrImplementation_UsesCorrectMap(
        WorkItemTaskType taskType)
    {
        // Verifies that each task type is routed to its own map and NOT to the other map.
        // Implementation item "42": PRESENT in issueEligibility, ABSENT from prEligibility.
        //   → must NOT be cancelled (routed to issue map, "42" present → keep).
        //   → would be cancelled if incorrectly routed to PR map ("42" absent).
        // Review item "42": ABSENT from issueEligibility, PRESENT in prEligibility.
        //   → must NOT be cancelled (routed to PR map, "42" present → keep).
        //   → would be cancelled if incorrectly routed to issue map ("42" absent).
        var item = MakePendingItem("42", "ip-1", taskType: taskType);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        // issueEligibility has "42" (keeps Implementation); prEligibility has "42" (keeps Review).
        // Neither map cancels — but the test is sensitive to cross-routing:
        var issueEligibility = taskType == WorkItemTaskType.Implementation
            ? EligibilityMap("ip-1", "42")   // Implementation: "42" present → keep
            : EligibilityMap("ip-1", "99");  // Review: "42" absent (wrong map would cancel)
        var prEligibility = taskType == WorkItemTaskType.Review
            ? EligibilityMap("ip-1", "42")   // Review: "42" present → keep
            : EligibilityMap("ip-1", "99");  // Implementation: "42" absent (wrong map would cancel)

        await svc.SweepPendingWorkItemsAsync(issueEligibility, prEligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never, $"{taskType} item present in its own map must NOT be cancelled");
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0);
    }

    // ── GetPendingAsync failure ───────────────────────────────────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenGetPendingThrows_AbortsWithoutCancelling()
    {
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated network failure"));

        var svc = CreateService(_sweepClientMock.Object);
        var issueEligibility = EligibilityMap("ip-1");  // empty set

        // Must not throw — should log Warning and return
        await svc.SweepPendingWorkItemsAsync(issueEligibility, EmptyEligibilityMap(), sweepEnabled: true, CancellationToken.None);

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

        await svc.SweepPendingWorkItemsAsync(issueEligibility, EmptyEligibilityMap(), sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "second item must still be processed after first fails");

        CounterValue("pipeline.queue_sweep.failed").Should().Be(1);
        // Counter incremented AFTER successful PostStatusAsync: item1 failed (no counter), item2 succeeded (counter=1)
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1,
            "QueueSweepCancelled is incremented only after a successful PostStatusAsync call");
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
        var issueEligibility = EligibilityMap("ip-1");  // empty — item "42" not eligible

        await svc.SweepPendingWorkItemsAsync(issueEligibility, EmptyEligibilityMap(), sweepEnabled: true, CancellationToken.None);

        CounterValue("pipeline.queue_sweep.failed").Should().Be(0,
            "expected HTTP race should not count as failure");
        // Counter is incremented AFTER PostStatusAsync; since it threw, the counter is NOT incremented
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0,
            "QueueSweepCancelled is only incremented after a successful PostStatusAsync; a race exception means no confirmed cancellation");
    }

    // ── Null client guard ─────────────────────────────────────────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenClientIsNull_ReturnsImmediately()
    {
        var svc = CreateService(sweepClient: null);  // WorkItemClient = null
        var issueEligibility = EligibilityMap("ip-1", "42");

        // Must not throw
        await svc.SweepPendingWorkItemsAsync(issueEligibility, EmptyEligibilityMap(), sweepEnabled: true, CancellationToken.None);

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

        await svc.SweepPendingWorkItemsAsync(issueEligibility, EmptyEligibilityMap(), sweepEnabled: true, CancellationToken.None);

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
        // The sweepEnabled parameter mirrors PipelineConfiguration.QueueSweepEnabled as passed
        // from ExecuteCycleAsync. If sweepEnabled = false, GetPendingAsync must NEVER be called.
        var item = MakePendingItem("42", "ip-1");
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        var issueEligibility = EligibilityMap("ip-1");  // empty — would cancel if sweep ran

        await svc.SweepPendingWorkItemsAsync(issueEligibility, EmptyEligibilityMap(), sweepEnabled: false, CancellationToken.None);

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
        // Template was NOT rate-limited at cycle start (included in pollableTemplates), but hit
        // a rate limit during PollIssueQueueAsync this cycle. HandleRateLimitException sets
        // RateLimitResetAt and clears the queue to an empty list. BuildEligibilityMap must detect
        // the now-set RateLimitResetAt and omit the provider (fail-open), preventing incorrect
        // cancellation of all pending WorkItems for that provider.
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
            // RateLimitResetAt set during this cycle's poll
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
        // Template poll threw a generic (non-rate-limit) exception during this cycle.
        // HandleGenericPollException increments ConsecutiveFailures and clears the queue.
        // BuildEligibilityMap must detect the ConsecutiveFailures increase and omit the provider
        // (fail-open), preventing cancellation of all pending WorkItems due to a transient failure.
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t-1"] = []  // cleared by HandleGenericPollException
        };
        var failuresBefore = new Dictionary<string, int> { ["t-1"] = 2 };  // had 2 prior failures
        var templateStatuses = new Dictionary<string, ConfigStatusSnapshot>
        {
            // ConsecutiveFailures incremented from 2 → 3 during this cycle
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
        // Template had prior failures but succeeded this cycle (ConsecutiveFailures reset to 0).
        // The provider SHOULD be included so stale WorkItems are correctly cancelled.
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            ["t-1"] = [new IssueSummary { Identifier = "42", Title = "Issue 42", Labels = [] }]
        };
        var failuresBefore = new Dictionary<string, int> { ["t-1"] = 3 };  // had 3 prior failures
        var templateStatuses = new Dictionary<string, ConfigStatusSnapshot>
        {
            // ConsecutiveFailures reset to 0 because this cycle's poll succeeded
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
}
