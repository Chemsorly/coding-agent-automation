using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.TestUtilities;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for the queue sweep feature:
/// <see cref="PipelineLoopService.SweepPendingWorkItemsAsync"/> and
/// <see cref="PipelineLoopService.BuildEligibilityMap"/>.
///
/// Tests call <c>SweepPendingWorkItemsAsync</c> directly (it is <c>internal</c>) and also
/// exercise <c>BuildEligibilityMap</c> (also <c>internal static</c>) in isolation.
/// </summary>
/// <remarks>
/// Metrics assertions use [Collection("Metrics")] to serialize against other metrics tests and
/// avoid cross-talk on the static <see cref="PipelineTelemetry.Meter"/>.
/// </remarks>
[Collection("Metrics")]
public sealed class PipelineLoopServiceQueueSweepTests : IAsyncDisposable
{
    private readonly Mock<IWorkItemSweepClient> _sweepClientMock = new();
    private readonly Mock<IConfigurationStore> _mockStore = new();
    private readonly Mock<IProviderFactory> _mockFactory = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<(string Name, long Value)> _counters = [];
    private PipelineLoopService? _loopService;

    public PipelineLoopServiceQueueSweepTests()
    {
        // Capture telemetry from the pipeline meter
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            _counters.Add((instrument.Name, measurement));
        });
        _listener.Start();

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
        _listener.Dispose();
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
            WorkItemClient        = sweepClient
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

    private long CounterValue(string name) =>
        _counters.Where(c => c.Name == name).Sum(c => c.Value);

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
        var eligibility = EligibilityMap("ip-1", "99");

        await svc.SweepPendingWorkItemsAsync(eligibility, sweepEnabled: true, CancellationToken.None);

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
        var eligibility = EligibilityMap("ip-1", "42");

        await svc.SweepPendingWorkItemsAsync(eligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0);
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
        var eligibility = EligibilityMap("ip-other", "42");

        await svc.SweepPendingWorkItemsAsync(eligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        CounterValue("pipeline.queue_sweep.skipped").Should().Be(1);
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(0);
    }

    // ── TaskType != Implementation ────────────────────────────────────────────

    [Theory]
    [InlineData(WorkItemTaskType.Review)]
    [InlineData(WorkItemTaskType.Decomposition)]
    [InlineData(WorkItemTaskType.Consolidation)]
    public async Task SweepPendingWorkItemsAsync_WhenTaskTypeIsNotImplementation_IsSkipped(
        WorkItemTaskType taskType)
    {
        var item = MakePendingItem("42", "ip-1", taskType: taskType);
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var svc = CreateService(_sweepClientMock.Object);
        // Provider present, issue NOT in set — but TaskType is not Implementation
        var eligibility = EligibilityMap("ip-1", "99");

        await svc.SweepPendingWorkItemsAsync(eligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        CounterValue("pipeline.queue_sweep.skipped").Should().Be(1);
    }

    // ── GetPendingAsync failure ───────────────────────────────────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenGetPendingThrows_AbortsWithoutCancelling()
    {
        _sweepClientMock
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated network failure"));

        var svc = CreateService(_sweepClientMock.Object);
        var eligibility = EligibilityMap("ip-1");  // empty set

        // Must not throw — should log Warning and return
        await svc.SweepPendingWorkItemsAsync(eligibility, sweepEnabled: true, CancellationToken.None);

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
        var eligibility = EligibilityMap("ip-1");

        await svc.SweepPendingWorkItemsAsync(eligibility, sweepEnabled: true, CancellationToken.None);

        _sweepClientMock.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "second item must still be processed after first fails");

        CounterValue("pipeline.queue_sweep.failed").Should().Be(1);
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(2,
            "QueueSweepCancelled is incremented before the PostStatusAsync call");
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
        var eligibility = EligibilityMap("ip-1");  // empty — item "42" not eligible

        await svc.SweepPendingWorkItemsAsync(eligibility, sweepEnabled: true, CancellationToken.None);

        CounterValue("pipeline.queue_sweep.failed").Should().Be(0,
            "expected HTTP race should not count as failure");
        CounterValue("pipeline.queue_sweep.cancelled").Should().Be(1,
            "cancel was attempted; QueueSweepCancelled is incremented before PostStatusAsync");
    }

    // ── Null client guard ─────────────────────────────────────────────────────

    [Fact]
    public async Task SweepPendingWorkItemsAsync_WhenClientIsNull_ReturnsImmediately()
    {
        var svc = CreateService(sweepClient: null);  // WorkItemClient = null
        var eligibility = EligibilityMap("ip-1", "42");

        // Must not throw
        await svc.SweepPendingWorkItemsAsync(eligibility, sweepEnabled: true, CancellationToken.None);

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
        var eligibility = EligibilityMap("ip-1", "42");

        await svc.SweepPendingWorkItemsAsync(eligibility, sweepEnabled: true, CancellationToken.None);

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
        var eligibility = EligibilityMap("ip-1");  // empty — would cancel if sweep ran

        await svc.SweepPendingWorkItemsAsync(eligibility, sweepEnabled: false, CancellationToken.None);

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
        PipelineTelemetry.QueueSweepCancelled.Add(1);
        PipelineTelemetry.QueueSweepSkipped.Add(1);
        PipelineTelemetry.QueueSweepFailed.Add(1);

        _counters.Should().Contain(c => c.Name == "pipeline.queue_sweep.cancelled");
        _counters.Should().Contain(c => c.Name == "pipeline.queue_sweep.skipped");
        _counters.Should().Contain(c => c.Name == "pipeline.queue_sweep.failed");
    }
}
