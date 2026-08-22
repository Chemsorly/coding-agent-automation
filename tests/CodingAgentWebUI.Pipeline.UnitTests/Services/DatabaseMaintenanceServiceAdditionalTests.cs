using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Additional unit tests for DatabaseMaintenanceService covering branches not exercised by DatabaseMaintenanceServiceTests:
/// CleanupStaleConsolidationRuns cancellation-token path,
/// CleanupStaleConsolidationRuns exception path from DeleteRunAsync,
/// SweepPipelineRunRetention/SweepWorkItemRetention non-cancellation exception (catch block),
/// SweepWorkItemRetention active path (retentionCount > 0 with InMemory throwing),
/// CleanupStaleWorkItems/CleanupStalePipelineRuns cancellation path.
/// </summary>
public class DatabaseMaintenanceServiceAdditionalTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly Mock<IConsolidationService> _mockConsolidationService = new();
    private readonly Mock<ILeaderElectionService> _mockLeaderElection = new();
    private readonly Mock<IPipelineConfigStore> _mockConfigStore = new();

    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkDistribution:Reconciliation:StaleRetentionDays"] = "7",
            ["WorkDistribution:Reconciliation:PipelineRunRetentionDays"] = "90",
            ["WorkDistribution:Reconciliation:ConsolidationRunRetentionDays"] = "90"
        })
        .Build();

    public DatabaseMaintenanceServiceAdditionalTests()
    {
        var dbName = $"DbMaintenanceAdditional-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new TestPipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _mockLeaderElection.Setup(l => l.IsLeader).Returns(true);
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    private DatabaseMaintenanceService CreateService() =>
        new(_dbFactory, _mockConsolidationService.Object, BuildServiceProvider(), _configuration, _mockConfigStore.Object);

    private IServiceProvider BuildServiceProvider()
    {
        var mock = new Mock<IServiceProvider>();
        mock.Setup(p => p.GetService(typeof(ILeaderElectionService))).Returns(_mockLeaderElection.Object);
        return mock.Object;
    }

    // ── CleanupStaleConsolidationRuns — cancellation path ────────────────────

    [Fact]
    public async Task CleanupStaleConsolidationRuns_CancellationRequested_DoesNotThrow()
    {
        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun>
            {
                new() {
                    RunId = "old-1",
                    Type = ConsolidationRunType.BrainConsolidation,
                    StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-100),
                    CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-95),
                    Status = ConsolidationRunStatus.Succeeded
                }
            });

        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // OperationCanceledException must be swallowed — method catches it
        await service.Invoking(s => s.CleanupStaleConsolidationRunsAsync(cts.Token))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task CleanupStaleConsolidationRuns_CancellationDuringIteration_StopsProcessing()
    {
        // Arrange: GetRunHistoryAsync returns two old runs. CancellationToken is pre-cancelled
        // so the foreach loop should break on the first ct.IsCancellationRequested check.
        var runs = new List<ConsolidationRun>
        {
            new() {
                RunId = "run-a",
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-100),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-95),
                Status = ConsolidationRunStatus.Succeeded
            },
            new() {
                RunId = "run-b",
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-100),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-95),
                Status = ConsolidationRunStatus.Succeeded
            }
        };

        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(runs);

        using var cts = new CancellationTokenSource();
        cts.Cancel();  // pre-cancel

        var service = CreateService();

        // When ct is cancelled before iteration, the break fires on first check — no deletes
        await service.CleanupStaleConsolidationRunsAsync(cts.Token);

        _mockConsolidationService.Verify(
            s => s.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "No deletes should occur when token is pre-cancelled");
    }

    [Fact]
    public async Task CleanupStaleConsolidationRuns_DeleteRunThrows_HandledGracefully()
    {
        // Arrange: DeleteRunAsync throws — the outer exception catch should swallow it
        var run = new ConsolidationRun
        {
            RunId = "failing-delete",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-100),
            CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-95),
            Status = ConsolidationRunStatus.Succeeded
        };

        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { run });
        _mockConsolidationService
            .Setup(s => s.DeleteRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Delete failed"));

        var service = CreateService();

        await service.Invoking(s => s.CleanupStaleConsolidationRunsAsync(CancellationToken.None))
            .Should().NotThrowAsync("exceptions from DeleteRunAsync must be caught and logged");
    }

    // ── CleanupStaleWorkItems — cancellation path ────────────────────────────

    [Fact]
    public async Task CleanupStaleWorkItems_CancellationRequested_DoesNotThrow()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.Invoking(s => s.CleanupStaleWorkItemsAsync(cts.Token))
            .Should().NotThrowAsync();
    }

    // ── CleanupStalePipelineRuns — cancellation path ─────────────────────────

    [Fact]
    public async Task CleanupStalePipelineRuns_CancellationRequested_DoesNotThrow()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.Invoking(s => s.CleanupStalePipelineRunsAsync(cts.Token))
            .Should().NotThrowAsync();
    }

    // ── SweepPipelineRunRetention — non-cancellation exception path ──────────

    [Fact]
    public async Task SweepPipelineRunRetention_NonCancellationException_HandledGracefully()
    {
        // Config store throws non-cancellation exception (simulates DB failure reading config)
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Config store failure"));

        var service = CreateService();

        await service.Invoking(s => s.SweepPipelineRunRetentionAsync(CancellationToken.None))
            .Should().NotThrowAsync("non-cancellation exceptions from config read must be caught");
    }

    [Fact]
    public async Task SweepWorkItemRetention_NonCancellationException_HandledGracefully()
    {
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Config store failure"));

        var service = CreateService();

        await service.Invoking(s => s.SweepWorkItemRetentionAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    // ── SweepPipelineRunRetention — active path (retentionCount > 0) ─────────

    [Fact]
    public async Task SweepPipelineRunRetention_ActivePath_InMemoryThrows_HandledGracefully()
    {
        // retentionCount = 5 → method attempts ExecuteSqlRawAsync which InMemory doesn't support
        // → the catch block for the general exception should swallow it
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { PipelineRunRetentionCount = 5 });

        var service = CreateService();

        await service.Invoking(s => s.SweepPipelineRunRetentionAsync(CancellationToken.None))
            .Should().NotThrowAsync("ExecuteSqlRawAsync failure must be caught and logged");
    }

    [Fact]
    public async Task SweepWorkItemRetention_ActivePath_InMemoryThrows_HandledGracefully()
    {
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkItemRetentionCount = 5 });

        var service = CreateService();

        await service.Invoking(s => s.SweepWorkItemRetentionAsync(CancellationToken.None))
            .Should().NotThrowAsync("ExecuteSqlRawAsync failure must be caught and logged");
    }

    // ── RunMaintenanceCycle — leader but consolidation throws ────────────────

    [Fact]
    public async Task RunMaintenanceCycle_WhenLeader_ConsolidationThrows_CycleCompletes()
    {
        // Arrange: consolidation service throws — but cycle should continue (it's in its own try/catch)
        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Consolidation failure"));

        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var service = new TestableMaintenanceService(
            _dbFactory, _mockConsolidationService.Object, BuildServiceProvider(),
            _configuration, _mockConfigStore.Object);

        // Leader = true → cycle runs; consolidation throws → caught; sweep calls may throw too (InMemory)
        await service.Invoking(s => s.TestRunMaintenanceCycleAsync(_mockLeaderElection.Object, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    // ── TestableMaintenanceService helper ────────────────────────────────────

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

        // Override sweep methods to no-ops so InMemory SQL exceptions don't mask coverage
        internal override Task SweepPipelineRunRetentionAsync(CancellationToken ct) => Task.CompletedTask;
        internal override Task SweepWorkItemRetentionAsync(CancellationToken ct) => Task.CompletedTask;
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
        private readonly DbContextOptions<PipelineDbContext> _opts;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_opts);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
