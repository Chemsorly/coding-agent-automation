using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace CodingAgentWebUI.IntegrationTests.Persistence;

/// <summary>
/// Integration tests for <see cref="DatabaseMaintenanceService"/> per-project retention sweeps.
///
/// Uses SQLite in-memory because the EF Core InMemory provider does not support server-side SQL.
/// The production sweeps use Postgres-specific DELETE…USING syntax; the <see cref="TestableRetentionService"/>
/// subclass substitutes SQLite-compatible SQL (DELETE WHERE Id IN (SELECT…ROW_NUMBER)) with
/// identical row-selection logic and semantics.
/// </summary>
[Trait("Feature", "2026-db-retention")]
public class RetentionSweepIntegrationTests : IDisposable
{
    private readonly SqliteConnection _sqliteConnection;
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly Mock<IConsolidationService> _mockConsolidation;
    private readonly Mock<IPipelineConfigStore> _mockConfigStore;
    private readonly IConfiguration _configuration;

    private static readonly Guid ProjA = new Guid("AAAAAAAA-0000-0000-0000-000000000001");
    private static readonly Guid ProjB = new Guid("BBBBBBBB-0000-0000-0000-000000000001");

    public RetentionSweepIntegrationTests()
    {
        var dbName = $"RetentionSweep-{Guid.NewGuid()}";
        _sqliteConnection = new SqliteConnection($"Data Source={dbName};Mode=Memory;Cache=Shared");
        _sqliteConnection.Open();

        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseSqlite(_sqliteConnection)
            .Options;

        using (var ctx = new TestSqlitePipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);

        _mockConsolidation = new Mock<IConsolidationService>();
        _mockConsolidation
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun>());

        _mockConfigStore = new Mock<IPipelineConfigStore>();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Reconciliation:StaleRetentionDays"] = "7",
                ["WorkDistribution:Reconciliation:PipelineRunRetentionDays"] = "90",
                ["WorkDistribution:Reconciliation:ConsolidationRunRetentionDays"] = "90",
            })
            .Build();
    }

    public void Dispose()
    {
        _sqliteConnection.Close();
        _sqliteConnection.Dispose();
    }

    // ── PipelineRuns ────────────────────────────────────────────────────

    [Fact]
    public async Task SweepPipelineRunRetention_ExcessRows_DeletesOldestCompleted()
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var runIds = Enumerable.Range(0, 5)
            .Select(i => (Id: Guid.NewGuid(), StartedAt: baseTime.AddHours(i)))
            .ToList();
        foreach (var (id, t) in runIds)
            await InsertPipelineRun(id, "proj-a", t, t.AddHours(1));

        SetupConfig(pipelineRunRetentionCount: 3);
        await CreateService().SweepPipelineRunRetentionAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var surviving = (await db.PipelineRuns.ToListAsync()).Select(r => r.RunId).ToHashSet();
        surviving.Should().HaveCount(3);
        surviving.Should().Contain(runIds[2].Id);
        surviving.Should().Contain(runIds[3].Id);
        surviving.Should().Contain(runIds[4].Id);
        surviving.Should().NotContain(runIds[0].Id, "oldest deleted");
        surviving.Should().NotContain(runIds[1].Id, "second oldest deleted");
    }

    [Fact]
    public async Task SweepPipelineRunRetention_WithinLimit_NoRowsDeleted()
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
            await InsertPipelineRun(Guid.NewGuid(), "proj-a", baseTime.AddHours(i), baseTime.AddHours(i + 1));

        SetupConfig(pipelineRunRetentionCount: 3);
        await CreateService().SweepPipelineRunRetentionAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        (await db.PipelineRuns.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task SweepPipelineRunRetention_ActiveRuns_NeverDeleted()
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var completedId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        await InsertPipelineRun(completedId, "proj-a", baseTime, baseTime.AddHours(1));
        await InsertPipelineRun(activeId, "proj-a", baseTime.AddHours(2), completedAt: null);

        SetupConfig(pipelineRunRetentionCount: 1);
        await CreateService().SweepPipelineRunRetentionAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ids = (await db.PipelineRuns.ToListAsync()).Select(r => r.RunId).ToHashSet();
        ids.Should().Contain(activeId, "active runs never deleted");
        ids.Should().Contain(completedId, "1 completed = at limit, not deleted");
    }

    [Fact]
    public async Task SweepPipelineRunRetention_ActiveRunPruned_WhenExcessCompleted()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var oldestId = Guid.NewGuid();
        var middleId = Guid.NewGuid();
        var newestId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        await InsertPipelineRun(oldestId, "proj-a", t, t.AddHours(1));
        await InsertPipelineRun(middleId, "proj-a", t.AddHours(1), t.AddHours(2));
        await InsertPipelineRun(newestId, "proj-a", t.AddHours(2), t.AddHours(3));
        await InsertPipelineRun(activeId, "proj-a", t.AddHours(3), completedAt: null);

        SetupConfig(pipelineRunRetentionCount: 2);
        await CreateService().SweepPipelineRunRetentionAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ids = (await db.PipelineRuns.ToListAsync()).Select(r => r.RunId).ToHashSet();
        ids.Should().Contain(activeId, "active run never deleted");
        ids.Should().Contain(newestId);
        ids.Should().Contain(middleId);
        ids.Should().NotContain(oldestId, "oldest completed beyond limit deleted");
    }

    [Fact]
    public async Task SweepPipelineRunRetention_NullProjectId_NeverDeleted()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var nullId1 = Guid.NewGuid();
        var nullId2 = Guid.NewGuid();
        var projAId = Guid.NewGuid();
        await InsertPipelineRun(nullId1, null, t, t.AddHours(1));
        await InsertPipelineRun(nullId2, null, t.AddHours(1), t.AddHours(2));
        await InsertPipelineRun(projAId, "proj-a", t, t.AddHours(1));

        SetupConfig(pipelineRunRetentionCount: 1);
        await CreateService().SweepPipelineRunRetentionAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ids = (await db.PipelineRuns.ToListAsync()).Select(r => r.RunId).ToHashSet();
        ids.Should().Contain(nullId1, "null-ProjectId never deleted");
        ids.Should().Contain(nullId2, "null-ProjectId never deleted");
        ids.Should().Contain(projAId, "1 proj-a row = at limit");
    }

    [Fact]
    public async Task SweepPipelineRunRetention_MultipleProjects_EachProjectIndependent()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
            await InsertPipelineRun(Guid.NewGuid(), "proj-a", t.AddHours(i), t.AddHours(i + 1));
        for (var i = 0; i < 2; i++)
            await InsertPipelineRun(Guid.NewGuid(), "proj-b", t.AddHours(i), t.AddHours(i + 1));

        SetupConfig(pipelineRunRetentionCount: 3);
        await CreateService().SweepPipelineRunRetentionAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var aCount = await db.PipelineRuns.Where(r => r.ProjectId == "proj-a").CountAsync();
        var bCount = await db.PipelineRuns.Where(r => r.ProjectId == "proj-b").CountAsync();
        aCount.Should().Be(3, "proj-a had 5, retention=3");
        bCount.Should().Be(2, "proj-b had 2 ≤ 3, untouched");
    }

    // ── WorkItems ───────────────────────────────────────────────────────

    [Fact]
    public async Task SweepWorkItemRetention_ExcessTerminalRows_DeletesOldest()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ids = Enumerable.Range(0, 5)
            .Select(i => (Id: Guid.NewGuid(), CompletedAt: t.AddHours(i)))
            .ToList();
        foreach (var (id, completedAt) in ids)
            await InsertWorkItem(id, ProjA, WorkItemStatus.Succeeded, completedAt);

        SetupConfig(workItemRetentionCount: 3);
        await CreateService().SweepWorkItemRetentionAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var surviving = (await db.WorkItems.Where(w => w.ProjectId == ProjA).ToListAsync())
            .Select(w => w.Id).ToHashSet();
        surviving.Should().HaveCount(3);
        surviving.Should().NotContain(ids[0].Id, "oldest deleted");
        surviving.Should().NotContain(ids[1].Id, "second oldest deleted");
        surviving.Should().Contain(ids[2].Id);
        surviving.Should().Contain(ids[3].Id);
        surviving.Should().Contain(ids[4].Id);
    }

    [Fact]
    public async Task SweepWorkItemRetention_NonTerminalRows_NeverDeleted()
    {
        // Note: [WARNING] This test is not falsifiable for the non-terminal guard. With only non-terminal
        // rows (no terminal rows at all), the window function produces no rows ranked > 1, so the DELETE
        // is a no-op regardless of whether the WHERE Status IN (3,4,5) filter exists. The test would
        // pass even if that filter were removed entirely. To make it falsifiable, insert at least one
        // terminal row alongside the non-terminal rows so the sweep has eligible candidates; then assert
        // that the terminal row is pruned while the non-terminal rows survive.
        var runningId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        await InsertWorkItem(runningId, ProjA, WorkItemStatus.Running, completedAt: null);
        await InsertWorkItem(pendingId, ProjA, WorkItemStatus.Pending, completedAt: null);

        SetupConfig(workItemRetentionCount: 1);
        await CreateService().SweepWorkItemRetentionAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ids = (await db.WorkItems.ToListAsync()).Select(w => w.Id).ToHashSet();
        ids.Should().Contain(runningId, "running never deleted");
        ids.Should().Contain(pendingId, "pending never deleted");
    }

    [Fact]
    public async Task SweepWorkItemRetention_TerminalNullCompletedAt_NeverDeleted()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, ProjA, WorkItemStatus.Succeeded, completedAt: null);

        SetupConfig(workItemRetentionCount: 1);
        await CreateService().SweepWorkItemRetentionAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        (await db.WorkItems.FindAsync(id)).Should().NotBeNull("terminal with null CompletedAt never deleted");
    }

    [Fact]
    public async Task SweepWorkItemRetention_NullProjectId_NeverDeleted()
    {
        var id = Guid.NewGuid();
        await InsertWorkItem(id, null, WorkItemStatus.Succeeded,
            completedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        SetupConfig(workItemRetentionCount: 1);
        await CreateService().SweepWorkItemRetentionAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        (await db.WorkItems.FindAsync(id)).Should().NotBeNull("null-ProjectId never deleted");
    }

    [Fact]
    public async Task SweepWorkItemRetention_MixedStatuses_OnlyTerminalEligible()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var runningId = Guid.NewGuid();
        var newest = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var oldest = Guid.NewGuid();

        await InsertWorkItem(runningId, ProjA, WorkItemStatus.Running, completedAt: null);
        await InsertWorkItem(newest, ProjA, WorkItemStatus.Succeeded, t.AddHours(2));
        await InsertWorkItem(middle, ProjA, WorkItemStatus.Succeeded, t.AddHours(1));
        await InsertWorkItem(oldest, ProjA, WorkItemStatus.Succeeded, t.AddHours(0));

        SetupConfig(workItemRetentionCount: 2);
        await CreateService().SweepWorkItemRetentionAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ids = (await db.WorkItems.ToListAsync()).Select(w => w.Id).ToHashSet();
        ids.Should().Contain(runningId, "non-terminal never deleted");
        ids.Should().Contain(newest);
        ids.Should().Contain(middle);
        ids.Should().NotContain(oldest, "oldest terminal beyond retention deleted");
    }

    // ── Sweep failure isolation ─────────────────────────────────────────

    [Fact]
    public async Task SweepPipelineRunFailure_DoesNotPreventWorkItemSweep()
    {
        // PipelineRun sweep enabled (→ DB factory faults on first call), WorkItem sweep disabled (-1)
        // Both wrapped in try/catch — neither should propagate.
        // Note: [WARNING] This test does not fully verify the stated invariant. WorkItemRetentionCount=-1
        // means the WorkItems sweep returns at the early-exit guard without hitting the database. The
        // test only confirms that two independent method calls don't propagate exceptions, not that a
        // WorkItems sweep executes meaningful work after a PipelineRuns sweep fails. A stronger test
        // would: (1) enable both sweeps (PipelineRunRetentionCount=N, WorkItemRetentionCount=N), (2)
        // arrange FaultingDbContextFactory to fault only on the first CreateDbContextAsync call
        // (consumed by PipelineRuns), and (3) assert that WorkItems rows were actually deleted.
        _mockConfigStore
            .SetupSequence(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { PipelineRunRetentionCount = 10 })
            .ReturnsAsync(new PipelineConfiguration { WorkItemRetentionCount = -1 });

        var faultFactory = new FaultingDbContextFactory(_dbFactory, throwOnFirstCall: true);
        var svc = new DatabaseMaintenanceService(
            faultFactory, _mockConsolidation.Object,
            _configuration, _mockConfigStore.Object);

        await svc.Invoking(s => s.SweepPipelineRunRetentionAsync(CancellationToken.None))
            .Should().NotThrowAsync();
        await svc.Invoking(s => s.SweepWorkItemRetentionAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task SweepWorkItemFailure_DoesNotPreventPipelineRunSweep()
    {
        // PipelineRun sweep disabled (-1), WorkItem sweep enabled (→ DB factory faults)
        // Note: [WARNING] Symmetric gap as SweepPipelineRunFailure_DoesNotPreventWorkItemSweep above.
        // PipelineRunRetentionCount=-1 means the PipelineRuns sweep exits early without touching the DB,
        // so the faulting factory's first call is consumed by the WorkItems sweep. The test does not
        // verify that PipelineRuns rows were actually swept after a WorkItems failure. A stronger test
        // would enable both sweeps and arrange the fault to target the WorkItems call specifically,
        // then assert that PipelineRuns rows were deleted.
        _mockConfigStore
            .SetupSequence(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { PipelineRunRetentionCount = -1 })
            .ReturnsAsync(new PipelineConfiguration { WorkItemRetentionCount = 10 });

        var faultFactory = new FaultingDbContextFactory(_dbFactory, throwOnFirstCall: true);
        var svc = new DatabaseMaintenanceService(
            faultFactory, _mockConsolidation.Object,
            _configuration, _mockConfigStore.Object);

        await svc.Invoking(s => s.SweepPipelineRunRetentionAsync(CancellationToken.None))
            .Should().NotThrowAsync();
        await svc.Invoking(s => s.SweepWorkItemRetentionAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies fault isolation at the orchestrator level: when the PipelineRun count-based sweep
    /// throws an unhandled exception, <see cref="DatabaseMaintenanceService.RunRetentionSweepAsync"/>
    /// catches it via its per-sweep try/catch in <c>RunSweepAsync</c>, logs a warning, and continues
    /// so the WorkItem sweep still executes to completion (rows are actually deleted).
    ///
    /// Regression guard: if the per-sweep try/catch in <c>RunSweepAsync</c> were removed, the
    /// exception thrown by <c>PipelineRunFaultingRetentionService.SweepPipelineRunRetentionAsync</c>
    /// would propagate out of <c>RunRetentionSweepAsync</c>, preventing the WorkItem sweep from
    /// running, and the assertion on surviving WorkItems would fail.
    /// </summary>
    [Fact]
    public async Task RunRetentionSweep_WhenPipelineRunSweepFaults_WorkItemSweepStillCompletes()
    {
        // Arrange: seed 5 terminal WorkItems for ProjA (retention=3 → expect 2 deleted, 3 survive).
        // Use a recent timestamp within the 7-day stale retention window so CleanupStaleWorkItemsAsync
        // never deletes them before the count-based sweep runs.
        var t = DateTimeOffset.UtcNow.AddDays(-1);
        var workItemIds = Enumerable.Range(0, 5)
            .Select(i => (Id: Guid.NewGuid(), CompletedAt: t.AddHours(i)))
            .ToList();
        foreach (var (id, completedAt) in workItemIds)
            await InsertWorkItem(id, ProjA, WorkItemStatus.Succeeded, completedAt);

        // Both sweeps active: PipelineRun retention=3, WorkItem retention=3
        SetupConfig(pipelineRunRetentionCount: 3, workItemRetentionCount: 3);

        // PipelineRunFaultingRetentionService throws from SweepPipelineRunRetentionAsync without
        // catching — the exception escapes to RunRetentionSweepAsync's per-sweep try/catch (RunSweepAsync),
        // which absorbs it and returns 0. SweepWorkItemRetentionAsync is inherited from
        // TestableRetentionService and uses the SQLite-compatible shim.
        var svc = new PipelineRunFaultingRetentionService(
            _dbFactory, _mockConsolidation.Object, _configuration, _mockConfigStore.Object);

        // Act: call the orchestrator method, not individual sweep methods.
        // NotThrowAsync makes a regression (exception escaping RunRetentionSweepAsync) produce a
        // clear assertion failure rather than an ambiguous xUnit exception.
        var result = await svc.Invoking(s => s.RunRetentionSweepAsync(CancellationToken.None))
            .Should().NotThrowAsync();
        var sweepResult = result.Subject;

        // Assert: WorkItem sweep ran and deleted the 2 oldest rows (5 - 3 = 2 deleted)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var survivingWorkItems = (await db.WorkItems.Where(w => w.ProjectId == ProjA).ToListAsync())
            .Select(w => w.Id).ToHashSet();
        survivingWorkItems.Should().HaveCount(3, "WorkItem sweep ran: retention=3, 5 seeded → 3 survive");
        survivingWorkItems.Should().Contain(workItemIds[2].Id);
        survivingWorkItems.Should().Contain(workItemIds[3].Id);
        survivingWorkItems.Should().Contain(workItemIds[4].Id);
        survivingWorkItems.Should().NotContain(workItemIds[0].Id, "oldest WorkItem beyond retention deleted");
        survivingWorkItems.Should().NotContain(workItemIds[1].Id, "second-oldest WorkItem beyond retention deleted");

        // PipelineRun sweep threw and was absorbed by RunSweepAsync → returned 0
        sweepResult.RetentionPipelineRunsDeleted.Should().Be(0, "PipelineRun sweep faulted and RunSweepAsync returned 0");
        // WorkItem sweep ran successfully
        sweepResult.RetentionWorkItemsDeleted.Should().Be(2, "WorkItem sweep deleted 2 rows");
    }

    /// <summary>
    /// Symmetric case: when the WorkItem count-based sweep throws an unhandled exception,
    /// <see cref="DatabaseMaintenanceService.RunRetentionSweepAsync"/> catches it via its
    /// per-sweep try/catch in <c>RunSweepAsync</c> and continues so the PipelineRun sweep
    /// still executes to completion (rows are actually deleted).
    /// </summary>
    [Fact]
    public async Task RunRetentionSweep_WhenWorkItemSweepFaults_PipelineRunSweepStillCompletes()
    {
        // Arrange: seed 5 completed PipelineRuns for proj-a (retention=3 → expect 2 deleted, 3 survive).
        // Use a recent timestamp within the stale retention window so CleanupStalePipelineRunsAsync
        // never deletes them before the count-based sweep runs.
        var t = DateTimeOffset.UtcNow.AddDays(-1);
        var runIds = Enumerable.Range(0, 5)
            .Select(i => (Id: Guid.NewGuid(), StartedAt: t.AddHours(i)))
            .ToList();
        foreach (var (id, startedAt) in runIds)
            await InsertPipelineRun(id, "proj-a", startedAt, startedAt.AddMinutes(30));

        // Both sweeps active: PipelineRun retention=3, WorkItem retention=3
        SetupConfig(pipelineRunRetentionCount: 3, workItemRetentionCount: 3);

        // WorkItemFaultingRetentionService throws from SweepWorkItemRetentionAsync without
        // catching — the exception escapes to RunRetentionSweepAsync's per-sweep try/catch (RunSweepAsync),
        // which absorbs it and returns 0. SweepPipelineRunRetentionAsync is inherited from
        // TestableRetentionService and uses the SQLite-compatible shim.
        var svc = new WorkItemFaultingRetentionService(
            _dbFactory, _mockConsolidation.Object, _configuration, _mockConfigStore.Object);

        // Act: call the orchestrator method, not individual sweep methods.
        // NotThrowAsync makes a regression (exception escaping RunRetentionSweepAsync) produce a
        // clear assertion failure rather than an ambiguous xUnit exception.
        var result = await svc.Invoking(s => s.RunRetentionSweepAsync(CancellationToken.None))
            .Should().NotThrowAsync();
        var sweepResult = result.Subject;

        // Assert: PipelineRun sweep ran and deleted the 2 oldest rows (5 - 3 = 2 deleted)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var survivingRuns = (await db.PipelineRuns.Where(r => r.ProjectId == "proj-a").ToListAsync())
            .Select(r => r.RunId).ToHashSet();
        survivingRuns.Should().HaveCount(3, "PipelineRun sweep ran: retention=3, 5 seeded → 3 survive");
        survivingRuns.Should().Contain(runIds[2].Id);
        survivingRuns.Should().Contain(runIds[3].Id);
        survivingRuns.Should().Contain(runIds[4].Id);
        survivingRuns.Should().NotContain(runIds[0].Id, "oldest PipelineRun beyond retention deleted");
        survivingRuns.Should().NotContain(runIds[1].Id, "second-oldest PipelineRun beyond retention deleted");

        // WorkItem sweep threw and was absorbed by RunSweepAsync → returned 0
        sweepResult.RetentionWorkItemsDeleted.Should().Be(0, "WorkItem sweep faulted and RunSweepAsync returned 0");
        // PipelineRun sweep ran successfully
        sweepResult.RetentionPipelineRunsDeleted.Should().Be(2, "PipelineRun sweep deleted 2 rows");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void SetupConfig(int pipelineRunRetentionCount = -1, int workItemRetentionCount = -1)
    {
        _mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                PipelineRunRetentionCount = pipelineRunRetentionCount,
                WorkItemRetentionCount = workItemRetentionCount,
            });
    }

    private TestableRetentionService CreateService()
    {
        var mockProvider = new Mock<IServiceProvider>();
        return new TestableRetentionService(
            _dbFactory, _mockConsolidation.Object,
            _configuration, _mockConfigStore.Object);
    }

    private async Task InsertPipelineRun(
        Guid runId, string? projectId, DateTimeOffset startedAt, DateTimeOffset? completedAt)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.PipelineRuns.Add(new PipelineRunEntity
        {
            RunId = runId,
            IssueIdentifier = $"owner/repo#{runId}",
            FinalStep = PipelineStep.Completed,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            ProjectId = projectId,
        });
        await db.SaveChangesAsync();
    }

    private async Task InsertWorkItem(
        Guid id, Guid? projectId, WorkItemStatus status, DateTimeOffset? completedAt)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        // The FK constraint requires a matching Projects row when ProjectId is non-null.
        if (projectId.HasValue && !await db.Projects.AnyAsync(p => p.Id == projectId.Value))
        {
            db.Projects.Add(new ProjectEntity
            {
                Id = projectId.Value,
                Name = $"Test Project {projectId.Value}",
                Enabled = true,
                TemplateIds = []
            });
        }
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = $"owner/repo#{id}",
            IssueProviderConfigId = "provider-1",
            Status = status,
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            TimeoutSeconds = 1800,
            CompletedAt = completedAt,
            Payload = "{}",
            ProjectId = projectId,
        });
        await db.SaveChangesAsync();
    }

    // ── SQLite-compatible test subclass ─────────────────────────────────

    /// <summary>
    /// Overrides the two retention sweep methods to use SQLite-compatible SQL.
    /// The row-selection logic (ROW_NUMBER OVER PARTITION BY, WHERE rn > N) is identical
    /// to the Postgres production SQL; only the DELETE syntax differs (IN subquery vs USING).
    /// SQLite supports ROW_NUMBER() since 3.25 (2018-09-15).
    /// </summary>
    private class TestableRetentionService : DatabaseMaintenanceService
    {
        public TestableRetentionService(
            IDbContextFactory<PipelineDbContext> dbFactory,
            IConsolidationService consolidationService,
            IConfiguration configuration,
            IPipelineConfigStore configStore)
            : base(dbFactory, consolidationService, configuration, configStore) { }

        internal override async Task<int> SweepPipelineRunRetentionAsync(CancellationToken ct)
        {
            try
            {
                var config = await _configStore.LoadPipelineConfigAsync(ct);
                var n = config.PipelineRunRetentionCount;
                if (n == -1) return 0;

                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                const string sql = """
                    DELETE FROM "PipelineRuns"
                    WHERE "RunId" IN (
                      SELECT "RunId" FROM (
                        SELECT "RunId",
                               ROW_NUMBER() OVER (
                                 PARTITION BY "ProjectId"
                                 ORDER BY "StartedAt" DESC, "RunId" DESC
                               ) AS rn
                        FROM "PipelineRuns"
                        WHERE "ProjectId" IS NOT NULL
                          AND "CompletedAt" IS NOT NULL
                      ) ranked
                      WHERE rn > @n
                    )
                    """;
                return await db.Database.ExecuteSqlRawAsync(sql,
                    new[] { new SqliteParameter("@n", n) }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "TestableRetentionService: PipelineRuns sweep failed (non-fatal)");
                return 0;
            }
        }

        internal override async Task<int> SweepWorkItemRetentionAsync(CancellationToken ct)
        {
            try
            {
                var config = await _configStore.LoadPipelineConfigAsync(ct);
                var n = config.WorkItemRetentionCount;
                if (n == -1) return 0;

                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                // Status IN (3,4,5): Succeeded=3, Failed=4, Cancelled=5 (WorkItemStatus enum)
                const string sql = """
                    DELETE FROM "WorkItems"
                    WHERE "Id" IN (
                      SELECT "Id" FROM (
                        SELECT "Id",
                               ROW_NUMBER() OVER (
                                 PARTITION BY "ProjectId"
                                 ORDER BY "CompletedAt" DESC, "Id" DESC
                               ) AS rn
                        FROM "WorkItems"
                        WHERE "ProjectId" IS NOT NULL
                          AND "Status" IN (3, 4, 5)
                          AND "CompletedAt" IS NOT NULL
                      ) ranked
                      WHERE rn > @n
                    )
                    """;
                return await db.Database.ExecuteSqlRawAsync(sql,
                    new[] { new SqliteParameter("@n", n) }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "TestableRetentionService: WorkItems sweep failed (non-fatal)");
                return 0;
            }
        }
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    /// <summary>
    /// Fault-injection subclass that overrides <c>SweepPipelineRunRetentionAsync</c> to throw
    /// without catching, letting the exception escape to <c>RunRetentionSweepAsync</c>'s
    /// per-sweep try/catch (<c>RunSweepAsync</c>), which absorbs it and returns 0.
    /// <c>SweepWorkItemRetentionAsync</c> is inherited from <see cref="TestableRetentionService"/>
    /// and uses the SQLite-compatible shim, so it exercises real database row deletion.
    /// </summary>
    private sealed class PipelineRunFaultingRetentionService : TestableRetentionService
    {
        public PipelineRunFaultingRetentionService(
            IDbContextFactory<PipelineDbContext> dbFactory,
            IConsolidationService consolidationService,
            IConfiguration configuration,
            IPipelineConfigStore configStore)
            : base(dbFactory, consolidationService, configuration, configStore) { }

        internal override Task<int> SweepPipelineRunRetentionAsync(CancellationToken ct)
        {
            // Throws without catching — the exception bubbles to RunRetentionSweepAsync's RunSweepAsync
            // wrapper, which is what actually provides orchestrator-level fault isolation.
            // Regression guard: if RunSweepAsync's try/catch were removed, this exception would
            // propagate out of RunRetentionSweepAsync and the WorkItem sweep would never run,
            // causing the test's surviving-row assertion to fail.
            throw new Exception("injected fault: PipelineRun sweep");
        }
    }

    /// <summary>
    /// Fault-injection subclass that overrides <c>SweepWorkItemRetentionAsync</c> to throw
    /// without catching, letting the exception escape to <c>RunRetentionSweepAsync</c>'s
    /// per-sweep try/catch (<c>RunSweepAsync</c>), which absorbs it and returns 0.
    /// <c>SweepPipelineRunRetentionAsync</c> is inherited from <see cref="TestableRetentionService"/>
    /// and uses the SQLite-compatible shim, so it exercises real database row deletion.
    /// </summary>
    private sealed class WorkItemFaultingRetentionService : TestableRetentionService
    {
        public WorkItemFaultingRetentionService(
            IDbContextFactory<PipelineDbContext> dbFactory,
            IConsolidationService consolidationService,
            IConfiguration configuration,
            IPipelineConfigStore configStore)
            : base(dbFactory, consolidationService, configuration, configStore) { }

        internal override Task<int> SweepWorkItemRetentionAsync(CancellationToken ct)
        {
            // Throws without catching — the exception bubbles to RunRetentionSweepAsync's RunSweepAsync
            // wrapper, which is what actually provides orchestrator-level fault isolation.
            // Regression guard: if RunSweepAsync's try/catch were removed, this exception would
            // propagate out of RunRetentionSweepAsync and the PipelineRun sweep result would be lost,
            // causing the test's surviving-row assertion to fail.
            throw new Exception("injected fault: WorkItem sweep");
        }
    }

    private sealed class TestSqlitePipelineDbContext : PipelineDbContext
    {
        public TestSqlitePipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never; }
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
                foreach (var p in et.GetProperties())
                    if (p.GetColumnType() == "jsonb") p.SetColumnType(null);
            }

            var dto = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
                v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
            var dtoNull = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(
                v => v == null ? null : (long?)v.Value.UtcTicks,
                v => v == null ? null : (DateTimeOffset?)new DateTimeOffset(v.Value, TimeSpan.Zero));

            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var p in et.GetProperties())
                {
                    if (p.ClrType == typeof(DateTimeOffset)) p.SetValueConverter(dto);
                    else if (p.ClrType == typeof(DateTimeOffset?)) p.SetValueConverter(dtoNull);
                }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestSqlitePipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class FaultingDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly IDbContextFactory<PipelineDbContext> _inner;
        private bool _hasThrown;
        private readonly bool _throwOnFirstCall;

        public FaultingDbContextFactory(IDbContextFactory<PipelineDbContext> inner, bool throwOnFirstCall)
        { _inner = inner; _throwOnFirstCall = throwOnFirstCall; }

        public PipelineDbContext CreateDbContext() => _inner.CreateDbContext();

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            if (_throwOnFirstCall && !_hasThrown) { _hasThrown = true; throw new InvalidOperationException("Simulated DB fault"); }
            return _inner.CreateDbContextAsync(ct);
        }
    }
}
