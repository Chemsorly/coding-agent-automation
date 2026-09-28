using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
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
/// Tests for <see cref="DatabaseMaintenanceService.BackfillConsolidationRunsAsync"/>.
/// Validates that historical ConsolidationRun rows are correctly copied to PipelineRuns
/// with the right identity key (ConsolidationRun.RunId from the Data blob, not entity.Id),
/// correct status mapping, idempotency, and field preservation.
/// </summary>
public class DatabaseMaintenanceServiceBackfillTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly Mock<IConsolidationService> _mockConsolidationService = new();
    private readonly Mock<IPipelineConfigStore> _mockConfigStore = new();
    private readonly Mock<IPipelineRunHistoryService> _mockRunHistoryService = new();

    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkDistribution:Reconciliation:StaleRetentionDays"] = "7",
            ["WorkDistribution:Reconciliation:PipelineRunRetentionDays"] = "90",
            ["WorkDistribution:Reconciliation:ConsolidationRunRetentionDays"] = "90"
        })
        .Build();

    public DatabaseMaintenanceServiceBackfillTests()
    {
        var dbName = $"DatabaseMaintenance-Backfill-{Guid.NewGuid()}";
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

    private DatabaseMaintenanceService CreateService()
        => new(_dbFactory, _mockConsolidationService.Object, _configuration, _mockConfigStore.Object, _mockRunHistoryService.Object);

    private async Task SeedConsolidationRunEntity(ConsolidationRun run)
    {
        await using var db = new TestPipelineDbContext(_dbOptions);
        var entity = new ConsolidationRunEntity
        {
            Id = Guid.NewGuid(), // intentionally DIFFERENT from run.RunId
            Data = JsonSerializer.Serialize(run, PipelineJsonOptions.Default)
        };
        db.ConsolidationRuns.Add(entity);
        await db.SaveChangesAsync();
    }

    private async Task SeedPipelineRunEntity(Guid runId)
    {
        await using var db = new TestPipelineDbContext(_dbOptions);
        db.PipelineRuns.Add(new PipelineRunEntity
        {
            RunId = runId,
            IssueIdentifier = "test:existing",
            FinalStep = PipelineStep.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            RunType = PipelineRunType.Consolidation
        });
        await db.SaveChangesAsync();
    }

    private static ConsolidationRun MakeRun(
        ConsolidationRunStatus status = ConsolidationRunStatus.Succeeded,
        ConsolidationRunType type = ConsolidationRunType.BrainConsolidation,
        string? templateId = "t1",
        string? templateName = "Template One",
        string? summary = "test summary",
        string? projectId = null,
        string? workItemId = null) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        Type = type,
        TemplateId = templateId,
        TemplateName = templateName,
        StartedAtUtc = DateTimeOffset.UtcNow.AddHours(-2),
        CompletedAtUtc = status is ConsolidationRunStatus.Succeeded or ConsolidationRunStatus.Failed or ConsolidationRunStatus.Cancelled
            ? DateTimeOffset.UtcNow.AddHours(-1)
            : null,
        Status = status,
        Summary = summary,
        ProjectId = projectId,
        WorkItemId = workItemId
    };

    // ── CRITICAL: identity key is ConsolidationRun.RunId from Data blob, NOT entity.Id ──

    /// <summary>
    /// Critical correctness test: idempotency must use the RunId from the Data JSON blob,
    /// not entity.Id (which is a Postgres-generated sequence PK with no relationship to the
    /// logical run identity). Confusing these two values would silently produce duplicate rows.
    /// </summary>
    [Fact]
    public async Task BackfillConsolidationRunsAsync_UsesRunIdFromDataBlob_NotEntityId()
    {
        var run = MakeRun();
        var runGuid = Guid.Parse(run.RunId);

        // Pre-seed a PipelineRun with the logical RunId (from the data blob)
        await SeedPipelineRunEntity(runGuid);
        // Seed a ConsolidationRunEntity with a DIFFERENT entity.Id
        await SeedConsolidationRunEntity(run);

        var capturedSummaries = new List<PipelineRunSummary>();
        _mockRunHistoryService
            .Setup(s => s.AddRunSummaryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineRunSummary, CancellationToken>((summary, _) => capturedSummaries.Add(summary))
            .Returns(Task.CompletedTask);

        var svc = CreateService();
        var count = await svc.BackfillConsolidationRunsAsync(CancellationToken.None);

        // RunId already exists in PipelineRuns — must be skipped (idempotency)
        count.Should().Be(0, "run with matching RunId already in PipelineRuns — must skip (idempotency uses Data.RunId, not entity.Id)");
        capturedSummaries.Should().BeEmpty();
    }

    // ── Idempotency ──────────────────────────────────────────────────────────

    [Fact]
    public async Task BackfillConsolidationRunsAsync_SkipsRunIdsAlreadyInPipelineRuns()
    {
        var run = MakeRun();
        var runGuid = Guid.Parse(run.RunId);
        await SeedPipelineRunEntity(runGuid);
        await SeedConsolidationRunEntity(run);

        var svc = CreateService();
        var count = await svc.BackfillConsolidationRunsAsync(CancellationToken.None);

        count.Should().Be(0);
        _mockRunHistoryService.Verify(s => s.AddRunSummaryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BackfillConsolidationRunsAsync_IsIdempotent()
    {
        var run = MakeRun();
        await SeedConsolidationRunEntity(run);

        _mockRunHistoryService
            .Setup(s => s.AddRunSummaryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService();
        var count1 = await svc.BackfillConsolidationRunsAsync(CancellationToken.None);

        // Seed the PipelineRun that the first backfill "would have written"
        // TODO [WARNING]: _mockRunHistoryService.AddRunSummaryAsync is a no-op mock — it never
        // actually writes to the in-memory database. The idempotency check (step 2: load existing
        // PipelineRuns) therefore relies on the manually-seeded SeedPipelineRunEntity call below,
        // not on what BackfillConsolidationRunsAsync actually wrote. The real idempotency code path
        // (read PipelineRuns after the service's own write) is never exercised. Consider using a
        // real IPipelineRunHistoryService backed by the in-memory EF context so the second sweep
        // sees what the first sweep actually wrote. (TestQualityReviewer review)
        await SeedPipelineRunEntity(Guid.Parse(run.RunId));

        var count2 = await svc.BackfillConsolidationRunsAsync(CancellationToken.None);

        count1.Should().Be(1, "first backfill should process the run");
        count2.Should().Be(0, "second backfill must skip already-present run");
    }

    // ── Status mapping ────────────────────────────────────────────────────────

    [Fact]
    public async Task BackfillConsolidationRunsAsync_MapsSucceededToCompleted()
    {
        var run = MakeRun(status: ConsolidationRunStatus.Succeeded);
        await SeedConsolidationRunEntity(run);

        PipelineRunSummary? captured = null;
        _mockRunHistoryService
            .Setup(s => s.AddRunSummaryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineRunSummary, CancellationToken>((s, _) => captured = s)
            .Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.BackfillConsolidationRunsAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task BackfillConsolidationRunsAsync_MapsFailedToFailed()
    {
        var run = MakeRun(status: ConsolidationRunStatus.Failed);
        await SeedConsolidationRunEntity(run);

        PipelineRunSummary? captured = null;
        _mockRunHistoryService
            .Setup(s => s.AddRunSummaryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineRunSummary, CancellationToken>((s, _) => captured = s)
            .Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.BackfillConsolidationRunsAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.FinalStep.Should().Be(PipelineStep.Failed);
    }

    [Fact]
    public async Task BackfillConsolidationRunsAsync_MapsCancelledToCancelled()
    {
        var run = MakeRun(status: ConsolidationRunStatus.Cancelled);
        await SeedConsolidationRunEntity(run);

        PipelineRunSummary? captured = null;
        _mockRunHistoryService
            .Setup(s => s.AddRunSummaryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineRunSummary, CancellationToken>((s, _) => captured = s)
            .Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.BackfillConsolidationRunsAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.FinalStep.Should().Be(PipelineStep.Cancelled);
    }

    // ── Non-terminal skipping ─────────────────────────────────────────────────

    [Fact]
    public async Task BackfillConsolidationRunsAsync_SkipsNonTerminalRuns()
    {
        await SeedConsolidationRunEntity(MakeRun(status: ConsolidationRunStatus.Running));
        await SeedConsolidationRunEntity(MakeRun(status: ConsolidationRunStatus.Pending));
        await SeedConsolidationRunEntity(MakeRun(status: ConsolidationRunStatus.Queued));

        var svc = CreateService();
        var count = await svc.BackfillConsolidationRunsAsync(CancellationToken.None);

        count.Should().Be(0, "non-terminal runs must be skipped");
        _mockRunHistoryService.Verify(s => s.AddRunSummaryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Field preservation ────────────────────────────────────────────────────

    [Fact]
    public async Task BackfillConsolidationRunsAsync_PreservesConsolidationFields()
    {
        var workItemId = Guid.NewGuid().ToString();
        var run = MakeRun(
            status: ConsolidationRunStatus.Succeeded,
            type: ConsolidationRunType.RefactoringDetection,
            templateId: "tmpl-123",
            templateName: "My Template",
            summary: "great summary",
            projectId: Guid.NewGuid().ToString(),
            workItemId: workItemId);

        await SeedConsolidationRunEntity(run);

        PipelineRunSummary? captured = null;
        _mockRunHistoryService
            .Setup(s => s.AddRunSummaryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineRunSummary, CancellationToken>((s, _) => captured = s)
            .Returns(Task.CompletedTask);

        var svc = CreateService();
        await svc.BackfillConsolidationRunsAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.RunId.Should().Be(run.RunId);
        captured.RunType.Should().Be(PipelineRunType.Consolidation);
        captured.ConsolidationType.Should().Be(ConsolidationRunType.RefactoringDetection);
        captured.ConsolidationTemplateId.Should().Be("tmpl-123");
        captured.ConsolidationTemplateName.Should().Be("My Template");
        captured.ConsolidationResultSummary.Should().Be("great summary");
        captured.ProjectId.Should().Be(run.ProjectId);
        captured.WorkItemId.Should().Be(Guid.Parse(workItemId));
    }

    // ── RetentionSweepResult integration ─────────────────────────────────────

    [Fact]
    public async Task BackfillConsolidationRunsAsync_ReturnsCountInRetentionSweepResult()
    {
        var run = MakeRun();
        await SeedConsolidationRunEntity(run);

        _mockRunHistoryService
            .Setup(s => s.AddRunSummaryAsync(It.IsAny<PipelineRunSummary>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConsolidationService
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConsolidationRun>());

        var svc = CreateService();
        var result = await svc.RunRetentionSweepAsync(CancellationToken.None);

        // TODO [WARNING]: This test only asserts result.ConsolidationRunsBackfilled == 1 but does not
        // verify that _mockRunHistoryService.AddRunSummaryAsync was actually called. A counting bug
        // that returned 1 without writing would still pass. Add a Verify call:
        //   _mockRunHistoryService.Verify(s => s.AddRunSummaryAsync(It.IsAny<PipelineRunSummary>(),
        //       It.IsAny<CancellationToken>()), Times.Once);
        // (TestQualityReviewer review)
        result.ConsolidationRunsBackfilled.Should().Be(1, "RetentionSweepResult must carry the backfill count");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<PipelineDbContext>(new TestPipelineDbContext(_options));
    }
}
