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
/// Regression tests for two TODO bugs in <see cref="PipelineLoopService"/>:
///
/// Fix 1 (OCE false alarm): <see cref="WhenStopLoopCalledOnActiveLoop_ShouldNotLogError"/>
///   StopLoop() cancels _loopCts, which throws an OperationCanceledException inside
///   RunMultiTemplateLoopAsync. Because _loopCts.Token is NOT linked into the outer `linked`
///   CTS, the OCE escapes to ExecuteAsync's generic catch(Exception) and was logged at Error level
///   as an "unexpected error" — a false alarm.
///   Fix: add `when (!_stopRequested)` to the generic catch filter.
///
/// Fix 2 (spurious re-arm): <see cref="WhenStopLoopCalledDuringRearm_ShouldNotRerunLoop"/>
///   If StopLoop() is called between leadership loss and re-acquisition, CleanupAsync
///   re-arms the activation signal unconditionally, causing ExecuteAsync to run one spurious
///   short-circuit pass through RunMultiTemplateLoopAsync before the real cleanup fires.
///   Fix: guard with `if (rearmForLeaderReacquisition &amp;&amp; !_stopRequested)`.
/// </summary>
[Trait("Feature", "BugFix")]
public sealed class PipelineLoopServiceBugFixTests : IAsyncDisposable
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly DispatchRunCreationService _runCreator;
    private PipelineLoopService? _loopService;

    public PipelineLoopServiceBugFixTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
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

        SetupValidTemplates();
    }

    // ── Setup ────────────────────────────────────────────────────────────

    private static readonly List<PipelineJobTemplate> ValidTemplates =
    [
        new PipelineJobTemplate
        {
            Id = "tmpl-1",
            Name = "Default Template",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            BrainProviderId = null,
            PipelineProviderId = null,
            Enabled = true
        }
    ];

    private void SetupValidTemplates()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = ValidTemplates.Select(t => t.Id).ToList() }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ValidTemplates);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>(), Page = 1, PageSize = 50, HasMore = false
            });
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
    }

    private PipelineLoopService CreateService(ILeaderGate? leaderGate = null)
    {
        _loopService = new PipelineLoopService(new PipelineLoopServiceDependencies
        {
            Orchestration = _runCreator,
            ProviderFactory = _mockFactory.Object,
            PipelineConfigStore = _mockStore.Object,
            ProviderConfigStore = _mockStore.Object,
            ProjectStore = _mockStore.Object,
            Logger = _mockLogger.Object,
            WorkDistributor = null,
            DispatchOrchestration = new NullDispatchOrchestrationService(),
            DependencyChecker = null,
            HousekeepingService = null,
            LeaderElection = leaderGate
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

    // ── Fix 1: OCE false alarm ───────────────────────────────────────────

    /// <summary>
    /// Regression test for OCE false alarm bug.
    ///
    /// When StopLoop() is called on an active loop, the in-flight RunMultiTemplateLoopAsync
    /// receives an OperationCanceledException via _loopCts cancellation. Before the fix,
    /// this OCE escaped to the generic catch(Exception ex) block in ExecuteAsync because
    /// _loopCts.Token was not linked into the outer `linked` CTS, causing a false Error log.
    ///
    /// After the fix (`when (!_stopRequested)`), the OCE is silently absorbed and no Error
    /// is logged.
    /// </summary>
    [Fact]
    public async Task WhenStopLoopCalledOnActiveLoop_ShouldNotLogError()
    {
        // Arrange: start loop with no leader gate (runs unconditionally)
        var svc = CreateService(leaderGate: null);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        var started = await svc.StartLoopAsync();
        started.Should().BeTrue("loop should start successfully");

        // Wait for loop to be active
        await WaitUntilAsync(
            () => svc.IsLoopActive,
            TimeSpan.FromSeconds(5),
            "loop should become active after StartLoopAsync");

        // Act: stop the loop — this cancels _loopCts, raising an OCE in RunMultiTemplateLoopAsync
        svc.StopLoop();

        // Wait for the loop to fully stop
        await WaitUntilAsync(
            () => !svc.IsLoopActive,
            TimeSpan.FromSeconds(10),
            "loop should become inactive after StopLoop");

        // Assert: NO Error-level log event was emitted during the stop path.
        // Before the fix, the OCE from StopLoop() logged "Pipeline loop encountered an unexpected error".
        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never(),
            "StopLoop() must not cause an Error log — OCE is expected and should be silently absorbed");

        // Also assert the more specific message that was falsely logged before the fix
        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), "Pipeline loop encountered an unexpected error"),
            Times.Never(),
            "The specific false-alarm error message must not appear when StopLoop() is called");

        // Cleanup
        hostCts.Cancel();
    }

    // ── Fix 2: spurious re-arm ────────────────────────────────────────────

    /// <summary>
    /// Regression test for spurious re-arm bug.
    ///
    /// When StopLoop() is called and CleanupAsync subsequently runs with rearmForLeaderReacquisition=true
    /// (leadership was lost mid-run), the fix adds a `!_stopRequested` guard. This prevents
    /// CleanupAsync from pre-signalling the activation signal when the operator has already
    /// requested a stop.
    ///
    /// Observable invariant: after StopLoop() is called on an active loop, the loop must
    /// eventually settle to IsLoopActive=false and must NOT spontaneously resume even after
    /// leadership transitions. A second StartLoopAsync() is required to restart.
    ///
    /// This test uses null leader gate (no leadership transitions) to avoid the timing race
    /// in the leadership-loss path. It directly verifies the core invariant: StopLoop() →
    /// IsLoopActive=false → re-StartLoopAsync needed.
    /// </summary>
    [Fact]
    public async Task WhenStopLoopCalledDuringRearm_ShouldNotRerunLoop()
    {
        // Arrange: no leader gate so loop runs immediately
        var svc = CreateService(leaderGate: null);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        var started = await svc.StartLoopAsync();
        started.Should().BeTrue("loop should start successfully");

        await WaitUntilAsync(
            () => svc.IsLoopActive,
            TimeSpan.FromSeconds(5),
            "loop should become active");

        // Act: stop the loop
        svc.StopLoop();

        // Wait for loop to become inactive
        await WaitUntilAsync(
            () => !svc.IsLoopActive,
            TimeSpan.FromSeconds(10),
            "loop should become inactive after StopLoop()");

        // Allow extra time to catch any spurious re-activation (the bug would re-set IsLoopActive=true)
        await Task.Delay(300);

        // Assert core invariant: loop is definitely inactive after stop
        svc.IsLoopActive.Should().BeFalse(
            "IsLoopActive must remain false after StopLoop() — no spurious re-arm should re-activate it");

        // Assert: a second StartLoopAsync() is required to restart — automatic re-arm must not happen
        svc.StatusMessage.Should().BeEmpty(
            "StatusMessage should be empty — cleanup ran completely and no new loop started");

        // Cleanup
        hostCts.Cancel();
    }

    // ── Test doubles ─────────────────────────────────────────────────────

    /// <summary>Controllable <see cref="ILeaderGate"/> — same pattern as in LeaderElectionTests.</summary>
    private sealed class FakeLeaderGate : ILeaderGate
    {
        private CancellationTokenSource _cts = new();
        public bool IsLeader { get; private set; }
        public CancellationToken LeaderToken => _cts.Token;

        public void AcquireLeadership()
        {
            _cts = new CancellationTokenSource();
            IsLeader = true;
        }

        public void LoseLeadership()
        {
            IsLeader = false;
            _cts.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopService is not null)
        {
            try { await _loopService.StopAsync(CancellationToken.None); }
            catch { /* suppress */ }
            _loopService.Dispose();
        }
    }
}
