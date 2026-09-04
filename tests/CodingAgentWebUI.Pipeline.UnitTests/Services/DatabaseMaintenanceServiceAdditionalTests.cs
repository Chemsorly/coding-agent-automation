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
        new(_dbFactory, _mockConsolidationService.Object, _configuration, _mockConfigStore.Object);

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

    // ── ReconcileOrphanedPipelineRunsAsync ────────────────────────────────────
    // The LINQ-based tests below use TestableMaintenanceServiceForReconciliation to verify
    // the reconciliation logic against the InMemory EF provider (which cannot run raw SQL).
    // The two production exception-path tests at the end of this section call CreateService()
    // directly so the production ExecuteSqlRawAsync code path is exercised.

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_TerminalStepWithNullCompletedAt_BackfillsCompletedAt()
    {
        // Arrange: insert a PipelineRunEntity with FinalStep=Completed (16) and null CompletedAt,
        // simulating a ghost run left by an OCE in RunFullPrCreationAsync.
        // InMemory EF does not support ExecuteSqlRawAsync, so we use a subclass that overrides
        // ReconcileOrphanedPipelineRunsAsync to perform the equivalent operation via LINQ for unit tests.
        var ghostRunId = Guid.NewGuid();
        await using (var ctx = new TestPipelineDbContext(_dbOptions))
        {
            ctx.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = ghostRunId,
                IssueIdentifier = "org/repo#1",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
                CompletedAt = null  // ghost: terminal step but no CompletedAt
            });
            await ctx.SaveChangesAsync();
        }

        var service = new TestableMaintenanceServiceForReconciliation(_dbFactory, _mockConsolidationService.Object, _configuration, _mockConfigStore.Object);

        // Act
        var count = await service.ReconcileOrphanedPipelineRunsAsync(CancellationToken.None);

        // Assert: one row was backfilled
        count.Should().Be(1);
        await using var readCtx = new TestPipelineDbContext(_dbOptions);
        var row = await readCtx.PipelineRuns.FindAsync(ghostRunId);
        row.Should().NotBeNull();
        row!.CompletedAt.Should().NotBeNull("ReconcileOrphanedPipelineRunsAsync must backfill CompletedAt for terminal ghost runs");
    }

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_AlreadyHasCompletedAt_NotUpdated()
    {
        // Arrange: insert a run that already has CompletedAt set — it is NOT orphaned.
        var completedRunId = Guid.NewGuid();
        var originalCompletedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await using (var ctx = new TestPipelineDbContext(_dbOptions))
        {
            ctx.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = completedRunId,
                IssueIdentifier = "org/repo#2",
                FinalStep = PipelineStep.Completed,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
                CompletedAt = originalCompletedAt
            });
            await ctx.SaveChangesAsync();
        }

        var service = new TestableMaintenanceServiceForReconciliation(_dbFactory, _mockConsolidationService.Object, _configuration, _mockConfigStore.Object);

        // Act
        var count = await service.ReconcileOrphanedPipelineRunsAsync(CancellationToken.None);

        // Assert: zero rows updated (no ghost runs)
        count.Should().Be(0);
        await using var readCtx = new TestPipelineDbContext(_dbOptions);
        var row = await readCtx.PipelineRuns.FindAsync(completedRunId);
        row!.CompletedAt.Should().Be(originalCompletedAt, "existing CompletedAt must not be overwritten by the reconciliation sweep");
    }

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_NonTerminalStepWithNullCompletedAt_NotUpdated()
    {
        // Arrange: an in-progress run (FinalStep not in 16/17/18) with null CompletedAt.
        // These are legitimately active runs and must never be touched.
        var activeRunId = Guid.NewGuid();
        await using (var ctx = new TestPipelineDbContext(_dbOptions))
        {
            ctx.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = activeRunId,
                IssueIdentifier = "org/repo#3",
                FinalStep = PipelineStep.GeneratingCode,  // non-terminal step (ordinal 8)
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                CompletedAt = null
            });
            await ctx.SaveChangesAsync();
        }

        var service = new TestableMaintenanceServiceForReconciliation(_dbFactory, _mockConsolidationService.Object, _configuration, _mockConfigStore.Object);

        var count = await service.ReconcileOrphanedPipelineRunsAsync(CancellationToken.None);

        count.Should().Be(0);
        await using var readCtx = new TestPipelineDbContext(_dbOptions);
        var row = await readCtx.PipelineRuns.FindAsync(activeRunId);
        row!.CompletedAt.Should().BeNull("active runs with non-terminal FinalStep must not be touched");
    }

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_MixedRows_BackfillsOnlyGhostOnes()
    {
        // Arrange: four rows covering all three terminal steps:
        //   1. ghost: FinalStep=Completed (16), CompletedAt=null  → must be backfilled
        //   2. ghost: FinalStep=Failed (17), CompletedAt=null      → must be backfilled
        //   3. ghost: FinalStep=Cancelled (18), CompletedAt=null   → must be backfilled
        //   4. normal: FinalStep=Completed (16), CompletedAt=set   → must NOT be changed
        var ghostId1 = Guid.NewGuid();
        var ghostId2 = Guid.NewGuid();
        var ghostId3 = Guid.NewGuid();
        var normalId = Guid.NewGuid();
        var existingCompletedAt = DateTimeOffset.UtcNow.AddHours(-5);
        await using (var ctx = new TestPipelineDbContext(_dbOptions))
        {
            ctx.PipelineRuns.AddRange(
                new PipelineRunEntity { RunId = ghostId1, IssueIdentifier = "org/repo#10", FinalStep = PipelineStep.Completed,  StartedAt = DateTimeOffset.UtcNow.AddDays(-1), CompletedAt = null },
                new PipelineRunEntity { RunId = ghostId2, IssueIdentifier = "org/repo#11", FinalStep = PipelineStep.Failed,     StartedAt = DateTimeOffset.UtcNow.AddDays(-1), CompletedAt = null },
                new PipelineRunEntity { RunId = ghostId3, IssueIdentifier = "org/repo#13", FinalStep = PipelineStep.Cancelled,  StartedAt = DateTimeOffset.UtcNow.AddDays(-1), CompletedAt = null },
                new PipelineRunEntity { RunId = normalId, IssueIdentifier = "org/repo#12", FinalStep = PipelineStep.Completed,  StartedAt = DateTimeOffset.UtcNow.AddDays(-1), CompletedAt = existingCompletedAt }
            );
            await ctx.SaveChangesAsync();
        }

        var service = new TestableMaintenanceServiceForReconciliation(_dbFactory, _mockConsolidationService.Object, _configuration, _mockConfigStore.Object);

        var count = await service.ReconcileOrphanedPipelineRunsAsync(CancellationToken.None);

        count.Should().Be(3, "exactly the three ghost rows (Completed, Failed, Cancelled) should be backfilled");
        await using var readCtx = new TestPipelineDbContext(_dbOptions);
        (await readCtx.PipelineRuns.FindAsync(ghostId1))!.CompletedAt.Should().NotBeNull();
        (await readCtx.PipelineRuns.FindAsync(ghostId2))!.CompletedAt.Should().NotBeNull();
        (await readCtx.PipelineRuns.FindAsync(ghostId3))!.CompletedAt.Should().NotBeNull();
        (await readCtx.PipelineRuns.FindAsync(normalId))!.CompletedAt.Should().Be(existingCompletedAt);
    }

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_CancellationRequested_DoesNotThrow()
    {
        // Exercises the testable override's early-exit path (pre-cancelled token → return 0 immediately).
        var service = new TestableMaintenanceServiceForReconciliation(_dbFactory, _mockConsolidationService.Object, _configuration, _mockConfigStore.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.Invoking(s => s.ReconcileOrphanedPipelineRunsAsync(cts.Token))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_ProductionPath_InMemoryThrows_HandledGracefully()
    {
        // Calls the *production* ReconcileOrphanedPipelineRunsAsync (not the testable override)
        // via CreateService(). InMemory EF does not support ExecuteSqlRawAsync, so it throws an
        // InvalidOperationException, which must be caught by the catch (Exception ex) block and
        // swallowed gracefully (returns 0, logs a warning).
        var service = CreateService();

        var result = await service.ReconcileOrphanedPipelineRunsAsync(CancellationToken.None);

        result.Should().Be(0, "non-fatal exceptions from the production SQL path must be caught and return 0");
    }

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_ProductionPath_OperationCancelled_ReturnsZero()
    {
        // Calls the *production* ReconcileOrphanedPipelineRunsAsync with a factory that throws
        // OperationCanceledException on CreateDbContextAsync, exercising the
        // catch (OperationCanceledException) when (ct.IsCancellationRequested) block.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var throwingFactory = new Mock<IDbContextFactory<PipelineDbContext>>();
        throwingFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var service = new DatabaseMaintenanceService(
            throwingFactory.Object,
            _mockConsolidationService.Object,
            _configuration,
            _mockConfigStore.Object);

        var result = await service.ReconcileOrphanedPipelineRunsAsync(cts.Token);

        result.Should().Be(0, "OperationCanceledException must be caught and return 0");
    }

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_NoRows_ReturnsZero()
    {
        // Empty database — nothing to reconcile.
        var service = new TestableMaintenanceServiceForReconciliation(_dbFactory, _mockConsolidationService.Object, _configuration, _mockConfigStore.Object);

        var count = await service.ReconcileOrphanedPipelineRunsAsync(CancellationToken.None);

        count.Should().Be(0);
    }

    /// <summary>
    /// Testable override that substitutes the raw SQL UPDATE with an equivalent LINQ-based
    /// implementation so the reconciliation logic can be exercised against the InMemory provider,
    /// which does not support <c>ExecuteSqlRawAsync</c>.
    /// </summary>
    private sealed class TestableMaintenanceServiceForReconciliation : DatabaseMaintenanceService
    {
        public TestableMaintenanceServiceForReconciliation(
            IDbContextFactory<PipelineDbContext> dbFactory,
            IConsolidationService consolidationService,
            IConfiguration configuration,
            IPipelineConfigStore configStore)
            : base(dbFactory, consolidationService, configuration, configStore) { }

        internal override async Task<int> ReconcileOrphanedPipelineRunsAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return 0;

            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);

                // LINQ-equivalent of the production SQL:
                //   UPDATE PipelineRuns SET CompletedAt = NOW()
                //   WHERE FinalStep IN (16, 17, 18) AND CompletedAt IS NULL
                var terminalSteps = new[] { PipelineStep.Completed, PipelineStep.Failed, PipelineStep.Cancelled };
                var now = DateTimeOffset.UtcNow;
                var orphans = await db.PipelineRuns
                    .Where(r => terminalSteps.Contains(r.FinalStep) && r.CompletedAt == null)
                    .ToListAsync(ct);

                foreach (var run in orphans)
                    run.CompletedAt = now;

                await db.SaveChangesAsync(ct);
                return orphans.Count;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return 0;
            }
        }
    }

    // ── RunMaintenanceCycle — leader but consolidation throws ────────────────
    // ── RunMaintenanceCycle test removed (Spec 047) ────────────────────────────
    // RunMaintenanceCycleAsync was removed when DatabaseMaintenanceService was converted
    // from a hosted BackgroundService to a plain singleton triggered by HTTP. The behaviour
    // it tested (consolidation exception swallowed, cycle continues) is now covered by
    // RunRetentionSweepAsync being called directly — each sweep method catches independently.

    // ── TestableMaintenanceService helper removed (Spec 047) ─────────────────
    // No longer needed — TestableMaintenanceService called RunMaintenanceCycleAsync which
    // was deleted. Sweep method override pattern (SweepPipelineRunRetentionAsync etc.)
    // is retained in RetentionSweepIntegrationTests where the SQLite-compatible SQL is used.

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
