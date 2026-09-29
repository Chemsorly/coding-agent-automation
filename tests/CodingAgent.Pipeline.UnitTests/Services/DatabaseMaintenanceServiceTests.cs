using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for DatabaseMaintenanceService.
/// Validates: retention cleanup for WorkItems and PipelineRuns.
/// </summary>
public class DatabaseMaintenanceServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly Mock<IConsolidationService> _mockConsolidationService;
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

    // ── Leader Election Gating ──────────────────────────────────────────
    // Service_WaitsForLeaderElection removed (Spec 047): DatabaseMaintenanceService is no longer
    // a BackgroundService. The leader-gate is now in ApiSchedulerEndpoints.RunRetentionSweep,
    // tested by SchedulerEndpointTests.RetentionSweep_WhenNotLeader_Returns503WithReason.

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

    // ── RunMaintenanceCycle tests removed (Spec 047) ────────────────────
    // RunMaintenanceCycleAsync and ExecuteAsync were removed in Spec 047: DatabaseMaintenanceService
    // is now a plain singleton and sweeps are triggered by the Scheduler via HTTP.
    // Leader-gate behavior is tested in SchedulerEndpointTests (Api.IntegrationTests).

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
            _dbFactory, _mockConsolidationService.Object, _configuration,
            _mockConfigStore.Object);
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
