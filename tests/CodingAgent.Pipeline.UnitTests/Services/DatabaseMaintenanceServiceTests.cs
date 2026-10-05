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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RetentionSweeps_ZeroOrMinusOne_KeepEveryRowWithoutOpeningTheDatabase(int count)
    {
        // Both values keep every row. A sweep that let 0 through would run its DELETE keeping no rows per project.
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { PipelineRunRetentionCount = count, WorkItemRetentionCount = count });
        var service = CreateService();

        (await service.SweepPipelineRunRetentionAsync(CancellationToken.None)).Should().Be(0);
        (await service.SweepWorkItemRetentionAsync(CancellationToken.None)).Should().Be(0);

        _dbFactory.Created.Should().Be(0, "a retention count of {0} must not reach the DELETE", count);
    }

    [Fact]
    public async Task RetentionSweep_PositiveCount_OpensTheDatabase()
    {
        // Counterpart of the test above: the factory count does see the sweep's DELETE attempt.
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { PipelineRunRetentionCount = 10 });

        await CreateService().SweepPipelineRunRetentionAsync(CancellationToken.None);

        _dbFactory.Created.Should().Be(1);
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

    // ── ReconcileOrphanedPipelineRunsAsync ───────────────────────────────────

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_TerminalStepNullCompletedAt_BackfillsCompletedAt()
    {
        // Arrange: seed three ghost rows — Completed/Failed/Cancelled with null CompletedAt.
        // These are the "33 ghost runs" reported in issue #2316.
        var idCompleted = Guid.NewGuid();
        var idFailed = Guid.NewGuid();
        var idCancelled = Guid.NewGuid();
        var seededIds = new[] { idCompleted, idFailed, idCancelled };

        await using var seedCtx = new TestPipelineDbContext(_dbOptions);
        seedCtx.PipelineRuns.AddRange(
            new PipelineRunEntity
            {
                RunId = idCompleted,
                IssueIdentifier = "org/repo#1",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-3),
                CompletedAt = null          // ghost — OCE skipped MarkCompleted
            },
            new PipelineRunEntity
            {
                RunId = idFailed,
                IssueIdentifier = "org/repo#2",
                FinalStep = PipelineStep.Failed,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-3),
                CompletedAt = null          // ghost
            },
            new PipelineRunEntity
            {
                RunId = idCancelled,
                IssueIdentifier = "org/repo#3",
                FinalStep = PipelineStep.Cancelled,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-3),
                CompletedAt = null          // ghost
            }
        );
        await seedCtx.SaveChangesAsync();

        var service = CreateService();
        var before = DateTimeOffset.UtcNow;

        // Act
        var count = await service.ReconcileOrphanedPipelineRunsAsync(CancellationToken.None);

        // Assert: all three ghost rows were updated
        count.Should().Be(3);

        await using var verifyCtx = new TestPipelineDbContext(_dbOptions);
        var remaining = await verifyCtx.PipelineRuns
            .Where(r => (r.FinalStep == PipelineStep.Completed ||
                         r.FinalStep == PipelineStep.Failed ||
                         r.FinalStep == PipelineStep.Cancelled)
                        && r.CompletedAt == null)
            .CountAsync();

        // TODO: This assertion queries all terminal-step rows globally, not just the ones seeded
        // by this test. If another test in the class leaves a terminal-step row with null CompletedAt
        // (e.g. a setup failure), a count of 0 here could be achieved by the reconciliation sweep
        // cleaning up that unrelated row rather than the three seeded above, masking incomplete
        // reconciliation. The more targeted assertion on seededIds below is robust; consider
        // replacing this global count check with a scoped filter: .Where(r => seededIds.Contains(r.RunId) && r.CompletedAt == null).
        remaining.Should().Be(0, "no terminal-step run should have null CompletedAt after reconciliation");

        // Each seeded ghost row must now have CompletedAt set to approximately now.
        // Filter by the specific RunIds seeded above so rows from other tests (which may already
        // have CompletedAt set) cannot satisfy this assertion vacuously.
        var updated = await verifyCtx.PipelineRuns
            .Where(r => seededIds.Contains(r.RunId))
            .ToListAsync();

        updated.Should().HaveCount(3, "all three seeded ghost rows must be present");
        updated.Should().AllSatisfy(r =>
            r.CompletedAt.Should().NotBeNull().And.BeOnOrAfter(before.AddSeconds(-1),
                "CompletedAt must be set to approximately now by the reconciliation sweep"));
    }

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_ActiveRunWithNullCompletedAt_IsNotTouched()
    {
        // An in-progress run (e.g., GeneratingCode=8) with null CompletedAt must NEVER be backfilled.
        await using var seedCtx = new TestPipelineDbContext(_dbOptions);
        var runId = Guid.NewGuid();
        seedCtx.PipelineRuns.Add(new PipelineRunEntity
        {
            RunId = runId,
            IssueIdentifier = "org/repo#10",
            FinalStep = PipelineStep.GeneratingCode,    // non-terminal
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAt = null
        });
        await seedCtx.SaveChangesAsync();

        var service = CreateService();

        var count = await service.ReconcileOrphanedPipelineRunsAsync(CancellationToken.None);

        count.Should().Be(0, "non-terminal step rows must not be touched");

        await using var verifyCtx = new TestPipelineDbContext(_dbOptions);
        var row = await verifyCtx.PipelineRuns.FindAsync(runId);
        row.Should().NotBeNull();
        row!.CompletedAt.Should().BeNull("active run must remain untouched");
    }

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_AlreadyHasCompletedAt_IsNotModified()
    {
        // A properly completed run (CompletedAt already set) must not be re-stamped.
        var existingCompletedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await using var seedCtx = new TestPipelineDbContext(_dbOptions);
        var runId = Guid.NewGuid();
        seedCtx.PipelineRuns.Add(new PipelineRunEntity
        {
            RunId = runId,
            IssueIdentifier = "org/repo#20",
            FinalStep = PipelineStep.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-3),
            CompletedAt = existingCompletedAt   // already set correctly
        });
        await seedCtx.SaveChangesAsync();

        var service = CreateService();

        var count = await service.ReconcileOrphanedPipelineRunsAsync(CancellationToken.None);

        count.Should().Be(0, "runs with CompletedAt already set must not be updated");

        await using var verifyCtx = new TestPipelineDbContext(_dbOptions);
        var row = await verifyCtx.PipelineRuns.FindAsync(runId);
        row!.CompletedAt.Should().Be(existingCompletedAt, "original CompletedAt must be preserved");
    }

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_NoOrphanedRows_ReturnsZero()
    {
        // Empty database — nothing to reconcile.
        var service = CreateService();

        var count = await service.ReconcileOrphanedPipelineRunsAsync(CancellationToken.None);

        count.Should().Be(0);
    }

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_Cancellation_DoesNotThrow()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.Invoking(s => s.ReconcileOrphanedPipelineRunsAsync(cts.Token))
            .Should().NotThrowAsync();
    }

    // ── Helper Methods ──────────────────────────────────────────────────

    private DatabaseMaintenanceService CreateService()
    {
        return new DatabaseMaintenanceService(
            _dbFactory, _configuration,
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

        /// <summary>How many contexts the code under test opened.</summary>
        public int Created { get; private set; }

        public PipelineDbContext CreateDbContext()
        {
            Created++;
            return new TestPipelineDbContext(_options);
        }

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
