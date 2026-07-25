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
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for DatabaseMaintenanceService.
/// Validates: retention cleanup for WorkItems, PipelineRuns, and ConsolidationRuns.
/// </summary>
// TODO: CleanupStaleWorkItemsAsync and CleanupStalePipelineRunsAsync have no unit test coverage.
// ExecuteDeleteAsync is unsupported by the EF Core InMemory provider, so these methods cannot be
// tested with the current in-memory setup. Consider adding integration tests with a real DB provider
// (e.g., SQLite or Testcontainers/PostgreSQL) to verify the WorkItem/PipelineRun retention logic.
public class DatabaseMaintenanceServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly Mock<IConsolidationService> _mockConsolidationService;
    private readonly Mock<ILeaderElectionService> _mockLeaderElection;
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
            s => s.DeleteRunAsync("old-run-1", It.IsAny<CancellationToken>()),
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
            s => s.DeleteRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
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
            s => s.DeleteRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
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
            s => s.DeleteRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
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
            s => s.DeleteRunAsync("failed-run-1", It.IsAny<CancellationToken>()),
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
            s => s.DeleteRunAsync("cancelled-run-1", It.IsAny<CancellationToken>()),
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
            s => s.DeleteRunAsync("old-success", It.IsAny<CancellationToken>()),
            Times.Once);
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync("old-failed", It.IsAny<CancellationToken>()),
            Times.Once);
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync("recent-success", It.IsAny<CancellationToken>()),
            Times.Never);
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync("old-running", It.IsAny<CancellationToken>()),
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

        var service = CreateServiceWithLeaderElection();

        // Act: run one cycle manually via ExecuteAsync (will skip because not leader)
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try { await service.StartAsync(cts.Token); await Task.Delay(50); await service.StopAsync(CancellationToken.None); }
        catch (OperationCanceledException) { /* expected */ }

        // Assert: no cleanup was attempted
        _mockConsolidationService.Verify(
            s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()),
            Times.Never);
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
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
            _dbFactory, _mockConsolidationService.Object, mockProvider.Object, config);

        // Act
        await service.CleanupStaleConsolidationRunsAsync(CancellationToken.None);

        // Assert: should be deleted with 30d retention
        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync("medium-old-run", It.IsAny<CancellationToken>()),
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

    // ── Helper Methods ──────────────────────────────────────────────────

    private DatabaseMaintenanceService CreateService()
    {
        var mockProvider = new Mock<IServiceProvider>();
        mockProvider.Setup(p => p.GetService(typeof(ILeaderElectionService)))
            .Returns(_mockLeaderElection.Object);

        return new DatabaseMaintenanceService(
            _dbFactory, _mockConsolidationService.Object, mockProvider.Object, _configuration);
    }

    private DatabaseMaintenanceService CreateServiceWithLeaderElection()
    {
        var mockProvider = new Mock<IServiceProvider>();
        mockProvider.Setup(p => p.GetService(typeof(ILeaderElectionService)))
            .Returns(_mockLeaderElection.Object);

        return new DatabaseMaintenanceService(
            _dbFactory, _mockConsolidationService.Object, mockProvider.Object, _configuration);
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
