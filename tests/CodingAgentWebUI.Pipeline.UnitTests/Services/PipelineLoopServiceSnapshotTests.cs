using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.Hosting;
using Moq;
using System.Reflection;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Characterization tests for <see cref="PipelineLoopService.SnapshotAndReconcileAsync"/>
/// (and after refactor: <see cref="PipelineLoopService.SnapshotCycleConfigAsync"/> /
/// <see cref="PipelineLoopService.ReconcileCachesAsync"/>).
///
/// These tests lock in the current behaviors before the split is implemented:
/// - Exception propagation asymmetry: ReconcileIssueProviderCacheAsync propagates,
///   ReconcileRepoProviderCacheAsync and ReconcileStuckWorkItemsAsync swallow exceptions.
/// - LoadActiveIssueIdentifiersAsync returns empty set on error.
/// - Non-null snapshot is always returned (even with empty template lists).
/// - The six-step execution order is preserved.
/// </summary>
[Trait("Feature", "PipelineLoop")]
public sealed class PipelineLoopServiceSnapshotTests : IAsyncDisposable
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly DispatchRunCreationService _runCreator;
    private PipelineLoopService? _loopService;

    private static readonly List<PipelineJobTemplate> DefaultTemplates =
    [
        new PipelineJobTemplate
        {
            Id = "tmpl-snap-1",
            Name = "Snapshot Test Template",
            IssueProviderId = "ip-snap",
            RepoProviderId = "rp-snap",
            Enabled = true
        }
    ];

    public PipelineLoopServiceSnapshotTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();

        // Forward .ForContext<T>() so the loop service gets a usable logger
        _mockLogger
            .Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
        _mockLogger
            .Setup(l => l.ForContext<It.IsAnyType>())
            .Returns(_mockLogger.Object);

        var lifecycle = new PipelineRunLifecycleService(
            new TestOrchestrationFactory.NullHistoryService(), null, _mockLogger.Object);

        _runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            lifecycle: lifecycle,
            logger: _mockLogger.Object);

        SetupValidDefaults();
    }

    private void SetupValidDefaults()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = DefaultTemplates.Select(t => t.Id).ToList() }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-snap", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "rp-snap", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultTemplates);

        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>(), Page = 1, PageSize = 50, HasMore = false
            });
        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(_mockIssueProvider.Object);
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
    }

    private PipelineLoopService CreateService(IWorkDistributor? workDistributor = null)
    {
        _loopService = new PipelineLoopService(new PipelineLoopServiceDependencies
        {
            Orchestration = _runCreator,
            ProviderFactory = _mockFactory.Object,
            PipelineConfigStore = _mockStore.Object,
            ProviderConfigStore = _mockStore.Object,
            ProjectStore = _mockStore.Object,
            Logger = _mockLogger.Object,
            WorkDistributor = workDistributor,
            DispatchOrchestration = new NullDispatchOrchestrationService(),
            DependencyChecker = null,
            HousekeepingService = null,
            LeaderElection = null
        });
        return _loopService;
    }

    private static Task InvokeExecuteAsync(PipelineLoopService service, CancellationToken stoppingToken)
    {
        var method = typeof(BackgroundService).GetMethod("ExecuteAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(service, [stoppingToken])!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        condition().Should().BeTrue(failMessage);
    }

    // ── Exception propagation asymmetry ──────────────────────────────────

    /// <summary>
    /// ReconcileIssueProviderCacheAsync has no try/catch — exceptions propagate out of
    /// SnapshotAndReconcileAsync (and after refactor: SnapshotCycleConfigAsync) to ExecuteAsync,
    /// where they are logged at Error level as unexpected errors.
    ///
    /// This contrasts with ReconcileRepoProviderCacheAsync and ReconcileStuckWorkItemsAsync,
    /// which swallow non-cancellation exceptions.
    /// </summary>
    [Fact]
    public async Task WhenReconcileIssueProviderCacheAsync_Throws_ExceptionPropagates()
    {
        // Arrange: first call (StartLoopAsync validation) succeeds; second call (first cycle) throws
        var callCount = 0;
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount > 1)
                    throw new InvalidOperationException("Simulated issue provider config load failure");
                return new List<ProviderConfig>
                {
                    new() { Id = "ip-snap", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" }
                };
            });

        var svc = CreateService();
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        await svc.StartLoopAsync();

        // Wait for the Error log — the exception must escape SnapshotAndReconcileAsync
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                _mockLogger.Verify(
                    l => l.Error(It.IsAny<Exception>(), "Pipeline loop encountered an unexpected error"),
                    Times.AtLeastOnce());
                break;
            }
            catch (MockException) { await Task.Delay(30); }
        }

        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), "Pipeline loop encountered an unexpected error"),
            Times.AtLeastOnce(),
            "exception from ReconcileIssueProviderCacheAsync must propagate out of SnapshotAndReconcileAsync");

        hostCts.Cancel();
    }

    /// <summary>
    /// ReconcileRepoProviderCacheAsync catches non-cancellation exceptions and logs a warning.
    /// The snapshot is still returned and ExecuteCycleAsync (poller) runs this cycle.
    /// Template must have ReviewEnabled=true or HousekeepingEnabled=true to enter the reconcile path.
    /// </summary>
    [Fact]
    public async Task WhenReconcileRepoProviderCacheAsync_Throws_SnapshotStillReturned()
    {
        // Use a template with ReviewEnabled so ReconcileRepoProviderCacheAsync doesn't short-circuit
        var reviewTemplate = new PipelineJobTemplate
        {
            Id = "tmpl-review",
            Name = "Review Template",
            IssueProviderId = "ip-snap",
            RepoProviderId = "rp-snap",
            Enabled = true,
            ReviewEnabled = true
        };
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([reviewTemplate]);
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = [reviewTemplate.Id] }
            });

        // First call (StartLoopAsync validation) succeeds; subsequent calls throw
        var callCount = 0;
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount > 1)
                    throw new InvalidOperationException("Simulated repo provider config load failure");
                return new List<ProviderConfig>
                {
                    new() { Id = "rp-snap", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" }
                };
            });

        var pollCalled = false;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCalled = true;
                return new PagedResult<IssueSummary>
                {
                    Items = new List<IssueSummary>(), Page = 1, PageSize = 50, HasMore = false
                };
            });

        var svc = CreateService();
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        await svc.StartLoopAsync();

        // The poller should be called — snapshot was returned despite repo reconcile failure
        await WaitUntilAsync(
            () => pollCalled,
            TimeSpan.FromSeconds(10),
            "poller must be called even when ReconcileRepoProviderCacheAsync throws (exception is swallowed)");

        // Warning must be logged for the swallowed exception
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<Exception>(), "Failed to reconcile repo provider cache, PR polling will be skipped this cycle"),
            Times.AtLeastOnce(),
            "repo provider cache reconcile failure must be logged as Warning");

        // TODO: The Times.Never() assertion below races against the shutdown path. StopLoop()/hostCts.Cancel()
        // are called immediately after, and any error logged during cancellation processing could cause a false
        // failure. Additionally, Times.Never() here is not ordered relative to cycle completion — it could pass
        // trivially if no Error has been logged yet (rather than proving no Error will ever be logged).
        // Fix: quiesce the loop before asserting, e.g. await StopAsync() before verifying Times.Never().
        // No Error-level log — the exception must not propagate
        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), "Pipeline loop encountered an unexpected error"),
            Times.Never(),
            "repo provider cache reconcile failure must not propagate to Error-level log");

        svc.StopLoop();
        hostCts.Cancel();
    }

    /// <summary>
    /// ReconcileStuckWorkItemsAsync catches all exceptions and logs a warning.
    /// The snapshot is still returned and ExecuteCycleAsync (poller) runs this cycle.
    /// </summary>
    [Fact]
    public async Task WhenReconcileStuckWorkItemsAsync_Throws_SnapshotStillReturned()
    {
        var mockDistributor = new Mock<IWorkDistributor>();
        mockDistributor.Setup(d => d.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(IssueIdentifier, ProviderConfigId)>());
        mockDistributor.Setup(d => d.ReconcileStuckItemsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated stuck item reconcile failure"));

        var pollCalled = false;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCalled = true;
                return new PagedResult<IssueSummary>
                {
                    Items = new List<IssueSummary>(), Page = 1, PageSize = 50, HasMore = false
                };
            });

        var svc = CreateService(workDistributor: mockDistributor.Object);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        await svc.StartLoopAsync();

        // The poller should be called — snapshot was returned despite stuck-items failure
        await WaitUntilAsync(
            () => pollCalled,
            TimeSpan.FromSeconds(10),
            "poller must be called even when ReconcileStuckWorkItemsAsync throws (exception is swallowed)");

        // Warning must be logged
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<Exception>(), "Failed to reconcile stuck work items at cycle start"),
            Times.AtLeastOnce(),
            "stuck work item reconcile failure must be logged as Warning");

        // No Error-level log
        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), "Pipeline loop encountered an unexpected error"),
            Times.Never(),
            "stuck work item reconcile failure must not propagate to Error-level log");

        svc.StopLoop();
        hostCts.Cancel();
    }

    // ── LoadActiveIssueIdentifiersAsync fallback ──────────────────────────

    /// <summary>
    /// When GetActiveIssueIdentifiersAsync throws, LoadActiveIssueIdentifiersAsync returns
    /// an empty set and logs a warning. The snapshot is returned and the poller runs.
    /// </summary>
    [Fact]
    public async Task WhenLoadActiveIssueIdentifiersAsync_WorkDistributorThrows_ReturnsEmptySet()
    {
        var mockDistributor = new Mock<IWorkDistributor>();
        mockDistributor.Setup(d => d.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated active identifiers load failure"));
        mockDistributor.Setup(d => d.ReconcileStuckItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var pollCalled = false;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCalled = true;
                return new PagedResult<IssueSummary>
                {
                    Items = new List<IssueSummary>(), Page = 1, PageSize = 50, HasMore = false
                };
            });

        var svc = CreateService(workDistributor: mockDistributor.Object);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        await svc.StartLoopAsync();

        // Poller must run — empty set fallback does not abort the cycle
        await WaitUntilAsync(
            () => pollCalled,
            TimeSpan.FromSeconds(10),
            "poller must be called when GetActiveIssueIdentifiersAsync throws (empty set fallback)");

        // Warning must be logged
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<Exception>(), It.Is<string>(s => s.Contains("active issue identifiers"))),
            Times.AtLeastOnce(),
            "active issue identifier load failure must be logged as Warning");

        svc.StopLoop();
        hostCts.Cancel();
    }

    // ── Null-return semantics ─────────────────────────────────────────────

    /// <summary>
    /// SnapshotAndReconcileAsync always returns a non-null CycleSnapshot, even when no templates
    /// are configured. The null-check guard in RunMultiTemplateLoopAsync is dead code for this path.
    /// Verified by: starting with one template, removing it between cycles, then confirming the
    /// loop remains active (not crashed) after an empty-template cycle completes.
    /// </summary>
    [Fact]
    public async Task WhenSnapshotAndReconcileAsync_EmptyTemplates_ReturnsSnapshotWithEmptyLists()
    {
        var fastConfig = TestPipelineConfig.Default() with
        {
            ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50)
        };
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(fastConfig);

        var cycleCount = 0;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref cycleCount);
                // After first cycle, remove templates to exercise the empty-template path
                if (cycleCount == 1)
                {
                    _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new List<PipelineJobTemplate>());
                    _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new List<PipelineProject>
                        {
                            new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = [] }
                        });
                }
                return new PagedResult<IssueSummary>
                {
                    Items = new List<IssueSummary>(), Page = 1, PageSize = 50, HasMore = false
                };
            });

        var svc = CreateService();
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        await svc.StartLoopAsync();

        // Wait for first cycle (with templates) to complete
        await WaitUntilAsync(
            () => cycleCount >= 1,
            TimeSpan.FromSeconds(10),
            "at least one poll cycle must complete");

        // TODO: The fixed Task.Delay(200) is fragile under CI load — the second cycle may not have started
        // (or completed) within the window, making IsLoopActive a false-positive pass (loop appears alive
        // simply because the empty-template cycle hasn't run yet). Fix: wait for cycleCount >= 2 or check
        // CurrentCycleTemplateCount == 0 to confirm the empty-template cycle actually ran to completion.
        // Wait for second cycle (with empty templates). The loop must not crash.
        await Task.Delay(200); // allow 2nd cycle to start and potentially crash

        svc.IsLoopActive.Should().BeTrue(
            "loop must remain active when template list becomes empty (non-null snapshot returned)");

        svc.StopLoop();
        hostCts.Cancel();
    }

    // ── Execution order ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies the six-step execution order within SnapshotAndReconcileAsync:
    /// 1. LoadPipelineConfigAsync
    /// 2. LoadAllTemplatesAsync (LoadAndFlattenTemplatesAsync)
    /// 3. LoadProviderConfigsAsync(Issue)  [ReconcileIssueProviderCacheAsync]
    /// 4. LoadProviderConfigsAsync(Repository)  [ReconcileRepoProviderCacheAsync — only if ReviewEnabled]
    /// 5. GetActiveIssueIdentifiersAsync  [LoadActiveIssueIdentifiersAsync]
    /// 6. ReconcileStuckItemsAsync  [ReconcileStuckWorkItemsAsync]
    ///
    /// The critical ordering constraint is: step 3 before step 5, and step 5 before step 6.
    /// Uses sequence numbers to establish causal ordering across multiple cycles.
    /// </summary>
    [Fact]
    public async Task WhenSnapshotAndReconcileAsync_OperationsExecuteInCorrectOrder()
    {
        // Each call appends (seqNum, name) to the log. Atomic counter provides strict ordering.
        var callLog = new List<(int Seq, string Name)>();
        var sync = new object();
        var seqCounter = 0;

        void Record(string name)
        {
            var seq = Interlocked.Increment(ref seqCounter);
            lock (sync) { callLog.Add((seq, name)); }
        }

        // Use a very short poll interval so the second cycle fires quickly
        var fastConfig = TestPipelineConfig.Default() with { ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50) };

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { Record("LoadPipelineConfig"); return fastConfig; });

        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { Record("LoadAllTemplates"); return DefaultTemplates; });

        // Issue provider load = step 3 (ReconcileIssueProviderCacheAsync)
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Record("LoadProviderConfigs_Issue");
                return new List<ProviderConfig>
                {
                    new() { Id = "ip-snap", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" }
                };
            });

        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Record("LoadProviderConfigs_Repository");
                return new List<ProviderConfig>
                {
                    new() { Id = "rp-snap", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" }
                };
            });

        var mockDistributor = new Mock<IWorkDistributor>();
        mockDistributor.Setup(d => d.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Record("GetActiveIssueIdentifiers");
                return new HashSet<(IssueIdentifier, ProviderConfigId)>();
            });
        mockDistributor.Setup(d => d.ReconcileStuckItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { Record("ReconcileStuckItems"); return 0; });

        // Block after two polls so we capture at least one complete runtime cycle
        var pollCount = 0;
        var secondCycleDone = new TaskCompletionSource();
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (Interlocked.Increment(ref pollCount) >= 2)
                    secondCycleDone.TrySetResult();
                return new PagedResult<IssueSummary>
                {
                    Items = new List<IssueSummary>(), Page = 1, PageSize = 50, HasMore = false
                };
            });

        var svc = CreateService(workDistributor: mockDistributor.Object);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        var started = await svc.StartLoopAsync();
        started.Should().BeTrue("loop must start with valid templates");

        await secondCycleDone.Task.WaitAsync(TimeSpan.FromSeconds(15));

        svc.StopLoop();
        hostCts.Cancel();

        // Extract ordering using the last occurrence of each step name so we compare
        // within a single runtime cycle rather than mixing startup and runtime calls.
        // StartLoopAsync triggers LoadPipelineConfig + LoadAllTemplates + LoadProviderConfigs
        // (Issue + Repository) but NOT GetActiveIssueIdentifiers/ReconcileStuckItems.
        // After startup, both sets of calls appear in each runtime cycle.
        lock (sync)
        {
            callLog.Should().Contain(e => e.Name == "LoadPipelineConfig", "step 1 must be recorded");
            callLog.Should().Contain(e => e.Name == "LoadAllTemplates", "step 2 must be recorded");
            callLog.Should().Contain(e => e.Name == "LoadProviderConfigs_Issue", "step 3 must be recorded");
            callLog.Should().Contain(e => e.Name == "GetActiveIssueIdentifiers", "step 5 must be recorded");
            callLog.Should().Contain(e => e.Name == "ReconcileStuckItems", "step 6 must be recorded");

            // Use last occurrence of each to analyze a single complete cycle
            int LastSeq(string name) => callLog.Where(e => e.Name == name).Max(e => e.Seq);

            var seqConfig = LastSeq("LoadPipelineConfig");
            var seqTemplates = LastSeq("LoadAllTemplates");
            var seqIssue = LastSeq("LoadProviderConfigs_Issue");
            var seqActive = LastSeq("GetActiveIssueIdentifiers");
            var seqStuck = LastSeq("ReconcileStuckItems");

            seqConfig.Should().BeLessThan(seqTemplates, "LoadPipelineConfig (step 1) must precede LoadAllTemplates (step 2)");
            seqTemplates.Should().BeLessThan(seqIssue, "LoadAllTemplates (step 2) must precede ReconcileIssueProviderCache (step 3)");
            seqIssue.Should().BeLessThan(seqActive, "ReconcileIssueProviderCache (step 3) must precede LoadActiveIssueIdentifiers (step 5)");
            seqActive.Should().BeLessThan(seqStuck, "LoadActiveIssueIdentifiers (step 5) must precede ReconcileStuckWorkItems (step 6)");
            // TODO: Step 4 (LoadProviderConfigs_Repository / ReconcileRepoProviderCacheAsync) ordering is not
            // asserted here because DefaultTemplates has ReviewEnabled=false, meaning the repo reconcile path is
            // never entered during runtime cycles (only during StartLoopAsync validation). A regression where
            // ReconcileCachesAsync called repo reconcile before issue reconcile would not be caught.
            // Fix: add a ReviewEnabled=true template to DefaultTemplates (or override it in this test) and add:
            //   var seqRepo = LastSeq("LoadProviderConfigs_Repository");
            //   seqIssue.Should().BeLessThan(seqRepo, "step 3 must precede step 4");
            //   seqRepo.Should().BeLessThan(seqActive, "step 4 must precede step 5");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopService is not null)
        {
            _loopService.StopLoop();
            try { await _loopService.StopAsync(CancellationToken.None); } catch { }
            _loopService.Dispose();
        }
    }
}
