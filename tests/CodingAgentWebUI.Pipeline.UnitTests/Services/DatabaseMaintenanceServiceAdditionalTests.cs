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

    // ── ReconcileOrphanedPipelineRunsAsync ───────────────────────────────────

    [Fact]
    public async Task ReconcileOrphanedPipelineRuns_TerminalStepNullCompletedAt_BackfillsCompletedAt()
    {
        // Arrange: seed three ghost rows — Completed/Failed/Cancelled with null CompletedAt.
        // These are the "33 ghost runs" reported in issue #2316.
        var idCompleted  = Guid.NewGuid();
        var idFailed     = Guid.NewGuid();
        var idCancelled  = Guid.NewGuid();
        var seededIds    = new[] { idCompleted, idFailed, idCancelled };

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

    // ── RunMaintenanceCycle — leader but consolidation throws ────────────────

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
