using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Reflection;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="DispatchService"/> and <see cref="ConsolidationDispatchHandler"/>
/// migration to <see cref="LeaderElectedPollingService"/>:
/// - DispatchService._startupValidationRun resets on each leadership tenure (RunLeadershipTermAsync override)
/// - DispatchService.OnPollCycleAsync delegates to PollAndDispatchAsync
/// - ConsolidationDispatchHandler.OnPollCycleAsync delegates to PollAndDispatchConsolidationAsync
/// </summary>
[Trait("Feature", "LeaderElectedPollingService")]
public class LeaderElectedPollingMigrationTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;

    public LeaderElectedPollingMigrationTests()
    {
        var dbName = $"LeaderElectedPollingMigration-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _mockKubeClient = new Mock<IKubernetesJobClient>();
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── DispatchService._startupValidationRun reset ───────────────────────────

    /// <summary>
    /// Verifies that _startupValidationRun resets on EACH leadership tenure, not just once.
    /// This is the key behavioral invariant of the RunLeadershipTermAsync override.
    ///
    /// Strategy: use a mock IAgentProfileStore so we can count LoadAgentProfilesAsync calls,
    /// which only happens when _startupValidationRun == false (i.e., the first poll of a tenure).
    /// With two leadership tenures we expect two validation runs.
    /// </summary>
    [Fact]
    public async Task DispatchService_StartupValidationRun_ResetsOnEachLeadershipTenure()
    {
        // Arrange: track how many times startup validation runs (AgentProfileStore.LoadAgentProfilesAsync)
        var mockAgentProfileStore = new Mock<IAgentProfileStore>();
        mockAgentProfileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var leaderCts1 = new CancellationTokenSource();
        var leaderElection = CreateLeaderElection(isLeader: true, leaderCts1);
        var service = CreateDispatchService(leaderElection, agentProfileStore: mockAgentProfileStore.Object);
        var hostCts = new CancellationTokenSource();

        // Act — tenure 1: start, wait for at least 1 poll cycle, then lose leadership
        var executeTask = InvokeExecuteAsync(service, hostCts.Token);

        // Wait for the first poll (validation run) to occur.
        // The leader-wait loop uses 2s intervals — allow up to 5s to ensure detection.
        // TODO: This is a raw time-based polling loop with no synchronization. If the CI machine is
        // under heavy load, the first poll cycle may not fire within 5 seconds (PollIntervalSeconds=1
        // plus the leader-wait loop's 2s delay). Use a SemaphoreSlim released via a Callback on the
        // mockAgentProfileStore.Setup, or a TaskCompletionSource, to make this detection deterministic
        // and remove the 5-second ceiling.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                mockAgentProfileStore.Verify(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce());
                break;
            }
            catch (Moq.MockException)
            {
                await Task.Delay(50);
            }
        }

        mockAgentProfileStore.Verify(
            s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(),
            "startup validation should run on the first poll of tenure 1");

        var callsAfterTenure1 = mockAgentProfileStore.Invocations.Count(i => i.Method.Name == "LoadAgentProfilesAsync");

        // Lose leadership: cancel the leader token
        // TODO: Race condition — leaderCts1 is cancelled before SetLeaderState sets IsLeader = false.
        // Between these two lines, the service can observe that the linked token cancelled, re-enter the
        // outer wait loop, see IsLeader is still true (field not yet updated), skip the inner wait, and
        // enter a phantom third tenure using the temporary CancellationTokenSource created in
        // SetLeaderState. The subsequent SetLeaderState(isLeader: true, leaderCts2) may then overwrite
        // the CTS mid-tenure. The assertion count becomes ambiguous under CI load. Use an atomic state
        // object or a mock ILeaderElectionService with a TaskCompletionSource to make the transition
        // atomic and deterministic.
        leaderCts1.Cancel();
        SetLeaderState(leaderElection, isLeader: false, new CancellationTokenSource());
        // TODO: This 150ms delay is an arbitrary heuristic with no deterministic guarantee that the
        // base class has exited RunLeadershipTermAsync and re-entered the leader-wait loop. Under
        // sustained CPU pressure (e.g., a saturated CI runner) this may not be sufficient. A
        // SemaphoreSlim or TaskCompletionSource signalled by a test hook on the mock would make
        // this synchronisation point provably correct.
        await Task.Delay(150); // let the cancellation propagate

        // Grant leadership again with a fresh leader CTS
        var leaderCts2 = new CancellationTokenSource();
        SetLeaderState(leaderElection, isLeader: true, leaderCts2);

        // Wait for at least one more LoadAgentProfilesAsync call (tenure 2 validation)
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var totalCalls = mockAgentProfileStore.Invocations.Count(i => i.Method.Name == "LoadAgentProfilesAsync");
            if (totalCalls > callsAfterTenure1)
                break;
            await Task.Delay(50);
        }

        // Assert: startup validation ran again in tenure 2
        var totalAfterTenure2 = mockAgentProfileStore.Invocations.Count(i => i.Method.Name == "LoadAgentProfilesAsync");
        totalAfterTenure2.Should().BeGreaterThan(callsAfterTenure1,
            "startup validation should re-run on the first poll of tenure 2 (reset by RunLeadershipTermAsync override)");

        // Cleanup
        hostCts.Cancel();
        await WaitForTaskCompletion(executeTask);
    }

    // ── DispatchService.OnPollCycleAsync wiring ──────────────────────────────

    /// <summary>
    /// Verifies that OnPollCycleAsync delegates to PollAndDispatchAsync by confirming that
    /// startup validation (LoadAgentProfilesAsync) is invoked, which only occurs when
    /// PollAndDispatchAsync is actually called. A no-op or wrong-wiring implementation
    /// would NOT call LoadAgentProfilesAsync, causing this assertion to fail.
    /// </summary>
    [Fact]
    public async Task DispatchService_OnPollCycleAsync_DelegatesToPollAndDispatch_WithNoPendingItems()
    {
        var mockAgentProfileStore = new Mock<IAgentProfileStore>();
        mockAgentProfileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var leaderElection = CreateLeaderElection(isLeader: true, new CancellationTokenSource());
        var service = CreateDispatchService(leaderElection, agentProfileStore: mockAgentProfileStore.Object);

        // OnPollCycleAsync → PollAndDispatchAsync → RunStartupValidationIfNeededAsync
        //   → _agentProfileStore.LoadAgentProfilesAsync (observable side-effect proving delegation)
        await InvokeOnPollCycleAsync(service, CancellationToken.None);

        // Assert: LoadAgentProfilesAsync was called, proving OnPollCycleAsync delegated to
        // PollAndDispatchAsync rather than returning early or calling a no-op.
        mockAgentProfileStore.Verify(
            s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()),
            Times.Once(),
            "OnPollCycleAsync must delegate to PollAndDispatchAsync, which calls LoadAgentProfilesAsync during startup validation");
    }

    // ── ConsolidationDispatchHandler.OnPollCycleAsync wiring ─────────────────

    /// <summary>
    /// Verifies that OnPollCycleAsync delegates to PollAndDispatchConsolidationAsync by confirming
    /// that the DB context factory is invoked (BuildStateAsync always calls CreateDbContextAsync).
    /// A no-op or wrong-wiring implementation would NOT call the factory, causing this assertion to fail.
    /// </summary>
    [Fact]
    public async Task ConsolidationDispatchHandler_OnPollCycleAsync_DelegatesToPollAndDispatchConsolidation()
    {
        var leaderMock = new Mock<ILeaderElectionService>();
        leaderMock.Setup(l => l.IsLeader).Returns(true);
        leaderMock.Setup(l => l.LeaderToken).Returns(CancellationToken.None);

        // Wrap the real DB factory in a counting spy so we can assert it was called.
        var spyDbFactory = new SpyDbContextFactory(_dbOptions);
        var handler = CreateConsolidationHandler(leaderMock.Object, spyDbFactory);

        // OnPollCycleAsync → PollAndDispatchConsolidationAsync → _stateBuilder.BuildStateAsync
        //   → _dbFactory.CreateDbContextAsync (observable side-effect proving delegation)
        await InvokeOnPollCycleAsync(handler, CancellationToken.None);

        // Assert: the DB context factory was called, proving OnPollCycleAsync delegated to
        // PollAndDispatchConsolidationAsync rather than returning early or calling a no-op.
        spyDbFactory.CreateDbContextAsyncCallCount.Should().Be(1,
            "OnPollCycleAsync must delegate to PollAndDispatchConsolidationAsync, which calls " +
            "CreateDbContextAsync exactly once via BuildStateAsync");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private DispatchService CreateDispatchService(
        ILeaderElectionService leaderElection,
        IAgentProfileStore? agentProfileStore = null)
    {
        var lifecycle = new DispatchLifecycleService(
            _mockKubeClient.Object,
            new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance),
            new DispatchServiceOptions
            {
                PollIntervalSeconds = 1,
                RateLimitPerSecond = 100,
                Namespace = "default",
                OrchestratorUrl = "http://orchestrator:8080",
                AgentApiKeySecretName = "agent-api-key"
            });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "1",
                ["WorkDistribution:Dispatch:RateLimitPerSecond"] = "100",
                ["WorkDistribution:Namespace"] = "default",
                ["WorkDistribution:OrchestratorUrl"] = "http://orchestrator:8080",
                ["WorkDistribution:AgentApiKeySecretName"] = "agent-api-key"
            })
            .Build();

        var stateBuilder = new DispatchStateBuilder(
            _dbFactory,
            lifecycle,
            JobTemplateStore.CreateEmpty(),
            new DispatchTemplateResolver(agentProfileStore, JobTemplateStore.CreateEmpty()),
            new DispatchServiceOptions { PollIntervalSeconds = 1, RateLimitPerSecond = 100 });

        var coreDeps = new DispatchServiceCoreDependencies(
            _dbFactory,
            leaderElection,
            lifecycle,
            AgentProfileStore: agentProfileStore,
            StateBuilder: stateBuilder);

        return new DispatchService(coreDeps, config, JobTemplateStore.CreateEmpty());
    }

    private ConsolidationDispatchHandler CreateConsolidationHandler(
        ILeaderElectionService leaderElection,
        IDbContextFactory<PipelineDbContext>? dbFactory = null)
    {
        var factory = dbFactory ?? _dbFactory;

        var transitionService = new WorkItemTransitionService(
            factory,
            NullLogger<WorkItemTransitionService>.Instance);

        var lifecycle = new DispatchLifecycleService(
            _mockKubeClient.Object,
            transitionService,
            new DispatchServiceOptions());

        var stateBuilder = new DispatchStateBuilder(
            factory,
            lifecycle,
            JobTemplateStore.CreateEmpty(),
            new DispatchTemplateResolver(null, JobTemplateStore.CreateEmpty()),
            new DispatchServiceOptions());

        var deps = new ConsolidationDispatchHandlerDependencies(
            factory,
            leaderElection,
            lifecycle,
            JobTemplateStore.CreateEmpty(),
            Mock.Of<IConfiguration>(),
            TransitionService: transitionService,
            StateBuilder: stateBuilder);

        return new ConsolidationDispatchHandler(deps, new DispatchServiceOptions { PollIntervalSeconds = 1 });
    }

    private static async Task InvokeExecuteAsync(DispatchService service, CancellationToken stoppingToken)
    {
        // TODO: ExecuteAsync only completes when stoppingToken is cancelled. If the background service
        // enters an unexpected state (e.g., the second leadership tenure is never acquired due to the
        // race in SetLeaderState), the task hangs indefinitely and the test never reports a failure.
        // The test relies on hostCts.Cancel() being reached to unblock this. If the code path before
        // that cancel blocks (e.g., a WaitForTaskCompletion that itself awaits this task), the test
        // runner will hang. Consider wrapping with a timeout (e.g., Task.WhenAny with a Task.Delay)
        // to fail fast with a descriptive message instead of hanging indefinitely.
        var method = typeof(BackgroundService).GetMethod("ExecuteAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [stoppingToken])!;
        await task.ConfigureAwait(false);
    }

    private static async Task InvokeOnPollCycleAsync(LeaderElectedPollingService service, CancellationToken ct)
    {
        var method = typeof(LeaderElectedPollingService).GetMethod("OnPollCycleAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [ct])!;
        await task;
    }

    private static async Task WaitForTaskCompletion(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
    }

    private static LeaderElectionService CreateLeaderElection(bool isLeader, CancellationTokenSource cts)
    {
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        SetLeaderState(les, isLeader, cts);
        return les;
    }

    private static void SetLeaderState(LeaderElectionService les, bool isLeader, CancellationTokenSource cts)
    {
        // TODO: This method couples the tests to the internal implementation of LeaderElectionService
        // via private field reflection. If LeaderElectionService is refactored (e.g., field renamed or
        // backing mechanism changed), these tests will silently fail to compile or, worse, silently stop
        // setting state and produce false positives (the second-tenure assertion would pass vacuously
        // because tenure 2 was never actually entered). Also, there is no guarantee that the _leaderCts
        // field maps to the LeaderToken property getter — if LeaderToken reads from a different field,
        // leadership loss will never propagate and the poll loop will never exit tenure 1. Consider
        // exposing an internal test-seam on LeaderElectionService (e.g., SimulateLeadershipAcquired /
        // SimulateLeadershipLost methods) or using a mock ILeaderElectionService with a
        // TaskCompletionSource-backed LeaderToken to eliminate the reflection dependency.
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            BindingFlags.NonPublic | BindingFlags.Instance);
        isLeaderField!.SetValue(les, isLeader);

        var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
            BindingFlags.NonPublic | BindingFlags.Instance);
        leaderCtsField!.SetValue(les, cts);
    }

    // ── Test infrastructure ──────────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersion = entityType.FindProperty("RowVersion");
                if (rowVersion != null)
                {
                    rowVersion.IsConcurrencyToken = false;
                    rowVersion.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var index in entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    entityType.RemoveIndex(index);
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// Wraps a real <see cref="IDbContextFactory{TContext}"/> and counts calls to
    /// <see cref="CreateDbContextAsync"/> so tests can assert that the factory was entered.
    /// Used to verify that <see cref="DispatchStateBuilder.BuildStateAsync"/> was actually invoked
    /// (as opposed to a no-op OnPollCycleAsync implementation).
    /// </summary>
    private sealed class SpyDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        private int _createDbContextAsyncCallCount;

        public int CreateDbContextAsyncCallCount => _createDbContextAsyncCallCount;

        public SpyDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;

        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            System.Threading.Interlocked.Increment(ref _createDbContextAsyncCallCount);
            return Task.FromResult<PipelineDbContext>(new TestPipelineDbContext(_options));
        }
    }
}
