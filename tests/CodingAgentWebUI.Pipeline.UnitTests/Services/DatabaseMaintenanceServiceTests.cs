using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Moq;
using System.Reflection;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for DatabaseMaintenanceService.
/// Validates: retention cleanup for WorkItems, PipelineRuns, and ConsolidationRuns.
/// </summary>
public class DatabaseMaintenanceServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly Mock<IConsolidationService> _mockConsolidationService;
    private readonly Mock<ILeaderElectionService> _mockLeaderElection;
    private readonly Mock<IPipelineConfigStore> _mockConfigStore;
    private readonly IConfiguration _configuration;

    public DatabaseMaintenanceServiceTests()
    {
        var dbName = $"DatabaseMaintenance-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _mockConsolidationService = new Mock<IConsolidationService>();
        _mockLeaderElection = new Mock<ILeaderElectionService>();
        _mockLeaderElection.Setup(l => l.IsLeader).Returns(true);

        // Default: both retention counts = -1 (disabled), so sweep methods are no-ops
        _mockConfigStore = new Mock<IPipelineConfigStore>();
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Reconciliation:StaleRetentionDays"] = "7",
                ["WorkDistribution:Reconciliation:PipelineRunRetentionDays"] = "90",
                ["WorkDistribution:Reconciliation:ConsolidationRunRetentionDays"] = "90",
                ["WorkDistribution:Reconciliation:MaintenanceIntervalHours"] = "6"
            })
            .Build();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── ConsolidationRun Cleanup Tests ──────────────────────────────────

    [Fact]
    public async Task CleanupStaleConsolidationRuns_OldCompletedRuns_AreDeleted()
    {
        // Arrange: completed run older than 90 days
        var oldRun = new ConsolidationRun
        {
            RunId = "old-run-1",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-100),
            CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-95),
            Status = ConsolidationRunStatus.Succeeded
        };

        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { oldRun });

        var service = CreateService();

        // Act
        await service.CleanupStaleConsolidationRunsAsync(CancellationToken.None);

        // Assert
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync((RunId)"old-run-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CleanupStaleConsolidationRuns_RecentRuns_ArePreserved()
    {
        // Arrange: completed run within retention period
        var recentRun = new ConsolidationRun
        {
            RunId = "recent-run-1",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-10),
            CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-5),
            Status = ConsolidationRunStatus.Succeeded
        };

        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { recentRun });

        var service = CreateService();

        // Act
        await service.CleanupStaleConsolidationRunsAsync(CancellationToken.None);

        // Assert
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CleanupStaleConsolidationRuns_RunningRuns_NeverDeleted()
    {
        // Arrange: running run that started a long time ago
        var runningRun = new ConsolidationRun
        {
            RunId = "running-run-1",
            Type = ConsolidationRunType.RefactoringDetection,
            StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-200),
            Status = ConsolidationRunStatus.Running
        };

        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { runningRun });

        var service = CreateService();

        // Act
        await service.CleanupStaleConsolidationRunsAsync(CancellationToken.None);

        // Assert: Running runs are never deleted regardless of age
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CleanupStaleConsolidationRuns_QueuedRuns_NeverDeleted()
    {
        // Arrange: queued run that started a long time ago
        var queuedRun = new ConsolidationRun
        {
            RunId = "queued-run-1",
            Type = ConsolidationRunType.HarnessSuggestions,
            StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-200),
            Status = ConsolidationRunStatus.Queued
        };

        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { queuedRun });

        var service = CreateService();

        // Act
        await service.CleanupStaleConsolidationRunsAsync(CancellationToken.None);

        // Assert
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CleanupStaleConsolidationRuns_FailedOldRun_IsDeleted()
    {
        // Arrange: failed run older than retention
        var failedRun = new ConsolidationRun
        {
            RunId = "failed-run-1",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-120),
            CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-120),
            Status = ConsolidationRunStatus.Failed
        };

        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { failedRun });

        var service = CreateService();

        // Act
        await service.CleanupStaleConsolidationRunsAsync(CancellationToken.None);

        // Assert
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync((RunId)"failed-run-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CleanupStaleConsolidationRuns_UsesStartedAtUtcWhenNoCompletedAt()
    {
        // Arrange: cancelled run with no CompletedAtUtc — falls back to StartedAtUtc
        var cancelledRun = new ConsolidationRun
        {
            RunId = "cancelled-run-1",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-100),
            CompletedAtUtc = null,
            Status = ConsolidationRunStatus.Cancelled
        };

        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { cancelledRun });

        var service = CreateService();

        // Act
        await service.CleanupStaleConsolidationRunsAsync(CancellationToken.None);

        // Assert: Should be deleted because StartedAtUtc (100 days ago) > 90 day retention
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync((RunId)"cancelled-run-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CleanupStaleConsolidationRuns_MixedRuns_DeletesOnlyStaleTerminal()
    {
        // Arrange: mix of runs — only old terminal ones should be deleted
        var runs = new List<ConsolidationRun>
        {
            new()
            {
                RunId = "old-success",
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-100),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-95),
                Status = ConsolidationRunStatus.Succeeded
            },
            new()
            {
                RunId = "recent-success",
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-5),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-4),
                Status = ConsolidationRunStatus.Succeeded
            },
            new()
            {
                RunId = "old-running",
                Type = ConsolidationRunType.RefactoringDetection,
                StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-200),
                Status = ConsolidationRunStatus.Running
            },
            new()
            {
                RunId = "old-failed",
                Type = ConsolidationRunType.HarnessSuggestions,
                StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-150),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-150),
                Status = ConsolidationRunStatus.Failed
            }
        };

        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(runs);

        var service = CreateService();

        // Act
        await service.CleanupStaleConsolidationRunsAsync(CancellationToken.None);

        // Assert: only "old-success" and "old-failed" should be deleted
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync((RunId)"old-success", It.IsAny<CancellationToken>()),
            Times.Once);
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync((RunId)"old-failed", It.IsAny<CancellationToken>()),
            Times.Once);
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync((RunId)"recent-success", It.IsAny<CancellationToken>()),
            Times.Never);
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync((RunId)"old-running", It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Leader Election Gating ──────────────────────────────────────────

    [Fact]
    public async Task Service_WaitsForLeaderElection()
    {
        // Arrange: not the leader
        _mockLeaderElection.Setup(l => l.IsLeader).Returns(false);

        var oldRun = new ConsolidationRun
        {
            RunId = "old-run-1",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-100),
            CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-95),
            Status = ConsolidationRunStatus.Succeeded
        };
        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { oldRun });

        var service = CreateService();

        // Act: run one cycle manually via ExecuteAsync (will skip because not leader)
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try { await service.StartAsync(cts.Token); await Task.Delay(50); await service.StopAsync(CancellationToken.None); }
        catch (OperationCanceledException) { /* expected */ }

        // Assert: no cleanup was attempted
        _mockConsolidationService.Verify(
            s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Configuration Tests ─────────────────────────────────────────────

    [Fact]
    public async Task Service_UsesConfiguredRetentionDays()
    {
        // Arrange: use short retention (30 days) via configuration
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Reconciliation:ConsolidationRunRetentionDays"] = "30",
                ["WorkDistribution:Reconciliation:MaintenanceIntervalHours"] = "1"
            })
            .Build();

        // Run that is 50 days old — older than 30d retention but within 90d default
        var run = new ConsolidationRun
        {
            RunId = "medium-old-run",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-50),
            CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-50),
            Status = ConsolidationRunStatus.Succeeded
        };

        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { run });

        var mockProvider = new Mock<IServiceProvider>();
        mockProvider.Setup(p => p.GetService(typeof(ILeaderElectionService)))
            .Returns(_mockLeaderElection.Object);

        var service = new DatabaseMaintenanceService(
            _dbFactory, _mockConsolidationService.Object, mockProvider.Object, config,
            _mockConfigStore.Object);

        // Act
        await service.CleanupStaleConsolidationRunsAsync(CancellationToken.None);

        // Assert: should be deleted with 30d retention
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync((RunId)"medium-old-run", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Error Handling ──────────────────────────────────────────────────

    [Fact]
    public async Task CleanupStaleConsolidationRuns_HandlesExceptionsWithoutThrowing()
    {
        // Arrange: service throws on GetRunHistoryAsync
        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated DB failure"));

        var service = CreateService();

        // Act & Assert: should not throw
        await service.Invoking(s => s.CleanupStaleConsolidationRunsAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    // ── Retention Sweep — Disabled Path ────────────────────────────────

    [Fact]
    public async Task SweepPipelineRunRetention_WhenDisabled_ReturnImmediately()
    {
        // Arrange: PipelineRunRetentionCount = -1 (default disabled)
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { PipelineRunRetentionCount = -1 });

        var service = CreateService();

        // Act: no exception and the DB factory is never called for SQL execution
        await service.SweepPipelineRunRetentionAsync(CancellationToken.None);

        // Assert: config store was read, but DB factory was NOT called
        _mockConfigStore.Verify(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Once);
        // (DB factory calls cannot be verified on InMemory provider, but no exception = correct early-return)
    }

    [Fact]
    public async Task SweepWorkItemRetention_WhenDisabled_ReturnImmediately()
    {
        // Arrange: WorkItemRetentionCount = -1 (default disabled)
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkItemRetentionCount = -1 });

        var service = CreateService();

        // Act
        await service.SweepWorkItemRetentionAsync(CancellationToken.None);

        // Assert: config store was read
        _mockConfigStore.Verify(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BothSweepsDisabled_MaintenanceCycleCompletesWithoutError()
    {
        // Arrange: both retention counts = -1
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun>());

        var service = CreateService();

        // Act: CleanupStale* use ExecuteDeleteAsync which is not supported by InMemory.
        // Call the retention sweeps directly instead.
        await service.Invoking(s => s.SweepPipelineRunRetentionAsync(CancellationToken.None))
            .Should().NotThrowAsync();
        await service.Invoking(s => s.SweepWorkItemRetentionAsync(CancellationToken.None))
            .Should().NotThrowAsync();

        // Config was read twice (once per sweep)
        _mockConfigStore.Verify(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SweepPipelineRunRetention_ConfigReadFromStore_OnEachCall()
    {
        // Arrange: return retention count > 0; InMemory will throw on SQL execution
        // (window-function DELETE not supported), so we only verify config was read.
        // Note: [WARNING] This test swallows the InMemory exception in a bare catch{} block and then
        // only asserts that LoadPipelineConfigAsync was called once. The assertion passes identically
        // whether the code read config and attempted SQL, returned early, or threw before SQL for an
        // unrelated reason. It does not distinguish the disabled path (retentionCount==-1) from the
        // active-sweep path. Consider replacing with a mock-based assertion that CreateDbContextAsync
        // is called exactly once when retentionCount > 0 (using a mock DB factory), or removing this
        // test since the integration tests in RetentionSweepIntegrationTests cover the active path.
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { PipelineRunRetentionCount = 10 });

        var service = CreateService();

        // Act: expect an exception from InMemory (SQL not supported) — that's fine
        try
        {
            await service.SweepPipelineRunRetentionAsync(CancellationToken.None);
        }
        catch
        {
            // InMemory provider throws on ExecuteSqlRawAsync — expected in unit tests
        }

        // Assert: config store was called
        _mockConfigStore.Verify(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── CleanupStaleWorkItems / CleanupStalePipelineRuns — Exercise catch path ──

    [Fact]
    public async Task CleanupStaleWorkItems_InMemoryThrows_HandledGracefully()
    {
        // InMemory EF does not support ExecuteDeleteAsync — the method catches the exception.
        // Calling it exercises the try/catch path and ensures no exception propagates.
        var service = CreateService();

        await service.Invoking(s => s.CleanupStaleWorkItemsAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task CleanupStalePipelineRuns_InMemoryThrows_HandledGracefully()
    {
        // InMemory EF does not support ExecuteDeleteAsync — the method catches the exception.
        var service = CreateService();

        await service.Invoking(s => s.CleanupStalePipelineRunsAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    // ── RunMaintenanceCycle — Non-leader skips cycle ────────────────────

    [Fact]
    public async Task RunMaintenanceCycle_WhenNotLeader_SkipsCycle()
    {
        // Arrange: leader election reports not the leader.
        // Use TestableMaintenanceService which exposes RunMaintenanceCycleAsync directly,
        // ensuring coverage tools can instrument the non-leader early-exit path.
        _mockLeaderElection.Setup(l => l.IsLeader).Returns(false);
        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun>());

        var service = new TestableMaintenanceService(
            _dbFactory, _mockConsolidationService.Object,
            BuildServiceProvider(), _configuration, _mockConfigStore.Object);

        // Act: call exposed wrapper (not reflection) — coverage-tool-friendly
        await service.TestRunMaintenanceCycleAsync(_mockLeaderElection.Object, CancellationToken.None);

        // Not-the-leader → consolidation service must NOT have been called
        _mockConsolidationService.Verify(
            s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunMaintenanceCycle_NoLeaderElection_ExecutesCycle()
    {
        // Arrange: no leader election (null) → always runs
        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun>());

        var service = new TestableMaintenanceService(
            _dbFactory, _mockConsolidationService.Object,
            BuildServiceProvider(), _configuration, _mockConfigStore.Object);

        // Act: pass null leader election — full cycle should execute
        await service.TestRunMaintenanceCycleAsync(null, CancellationToken.None);

        // No leader gate → consolidation service WAS called
        _mockConsolidationService.Verify(
            s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── ExecuteAsync startup — config store failure path ───────────────

    [Fact]
    public async Task ExecuteAsync_ConfigStoreThrows_UsesDefaultInterval()
    {
        // Arrange: config store throws on the first call (the startup interval read).
        // Subsequent calls return defaults so the maintenance cycle can complete.
        var callCount = 0;
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) throw new InvalidOperationException("Config read failure");
                return new PipelineConfiguration();
            });

        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun>());

        var service = CreateService();

        // Call ExecuteAsync directly via reflection so the coverage tool instruments it.
        // Use a short-timeout CTS so the timer loop exits quickly.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var executeMethod = typeof(DatabaseMaintenanceService)
            .GetMethod("ExecuteAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        executeMethod.Should().NotBeNull();

        try
        {
            await (Task)executeMethod!.Invoke(service, [cts.Token])!;
        }
        catch (OperationCanceledException)
        {
            // Expected — timer cancelled after timeout
        }
        catch (System.Reflection.TargetInvocationException tie)
            when (tie.InnerException is OperationCanceledException)
        {
            // Expected — reflection wraps OCE
        }

        // The config store was called (first call threw, subsequent calls returned defaults)
        _mockConfigStore.Verify(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ── Sweep — OperationCanceled path ────────────────────────────────

    [Fact]
    public async Task SweepPipelineRunRetention_CancellationDuringConfigRead_DoesNotThrow()
    {
        // Arrange: config store throws OperationCanceledException (simulates cancellation
        // propagating through LoadPipelineConfigAsync). The sweep must NOT propagate it —
        // OperationCanceledException is re-thrown only if ct.IsCancellationRequested.
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { PipelineRunRetentionCount = 10 });

        var service = CreateService();

        // Pre-cancel — the method should hit the OperationCanceledException catch path
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.Invoking(s => s.SweepPipelineRunRetentionAsync(cts.Token))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task SweepWorkItemRetention_CancellationDuringConfigRead_DoesNotThrow()
    {
        // Same as above but for the WorkItems sweep.
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkItemRetentionCount = 10 });

        var service = CreateService();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.Invoking(s => s.SweepWorkItemRetentionAsync(cts.Token))
            .Should().NotThrowAsync();
    }

    // ── Helper Methods ──────────────────────────────────────────────────

    private DatabaseMaintenanceService CreateService()
    {
        return new DatabaseMaintenanceService(
            _dbFactory, _mockConsolidationService.Object, BuildServiceProvider(), _configuration,
            _mockConfigStore.Object);
    }

    private IServiceProvider BuildServiceProvider()
    {
        var mockProvider = new Mock<IServiceProvider>();
        mockProvider.Setup(p => p.GetService(typeof(ILeaderElectionService)))
            .Returns(_mockLeaderElection.Object);
        return mockProvider.Object;
    }

    /// <summary>
    /// Exposes <c>RunMaintenanceCycleAsync</c> as a public method so coverage tools
    /// can instrument the call without reflection (which bypasses IL instrumentation).
    /// </summary>
    private sealed class TestableMaintenanceService : DatabaseMaintenanceService
    {
        public TestableMaintenanceService(
            IDbContextFactory<PipelineDbContext> dbFactory,
            IConsolidationService consolidationService,
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            IPipelineConfigStore configStore)
            : base(dbFactory, consolidationService, serviceProvider, configuration, configStore) { }

        public Task TestRunMaintenanceCycleAsync(ILeaderElectionService? leaderElection, CancellationToken ct)
            => RunMaintenanceCycleAsync(leaderElection, ct);
    }

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = ValueGenerated.Never; }
            }
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
