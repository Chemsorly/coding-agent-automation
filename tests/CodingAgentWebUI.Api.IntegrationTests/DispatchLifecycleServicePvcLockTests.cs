using AwesomeAssertions;
using CodingAgentWebUI.Api.Dispatch;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Unit-level tests for <see cref="DispatchLifecycleService.SelectPvcFromDbAsync"/>.
///
/// Verifies AC4 (issue #2322): two concurrent callers sharing a single
/// <see cref="DispatchLifecycleService"/> instance cannot both claim the same PVC,
/// because <c>SelectPvcFromDbAsync</c> re-queries the DB INSIDE <c>_pvcSelectLock</c>.
///
/// These tests operate on an InMemory EF context so no real PostgreSQL or K8s is required.
/// They exercise the internal <c>SelectPvcFromDbAsync</c> method directly, bypassing the
/// HTTP layer, to provide deterministic concurrency assertions.
/// </summary>
public sealed class DispatchLifecycleServicePvcLockTests : IDisposable
{
    // Each test instance gets its own DB to prevent cross-test state contamination
    private readonly string _dbName = $"PvcLockTest-{Guid.NewGuid():N}";

    private readonly DispatchLifecycleService _svc;
    private readonly InMemoryDbContextFactory _dbFactory;

    private static readonly List<string> SinglePvcPool = ["kiro-pvc-0"];
    private static readonly List<string> TwoPvcPool = ["kiro-pvc-0", "kiro-pvc-1"];

    public DispatchLifecycleServicePvcLockTests()
    {
        _dbFactory = new InMemoryDbContextFactory(_dbName);

        var options = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            KiroPvcPool = SinglePvcPool,
            RateLimitPerSecond = 100
        };

        _svc = new DispatchLifecycleService(
            Mock.Of<IKubernetesJobClient>(),
            new WorkItemTransitionService(
                _dbFactory,
                Mock.Of<ILogger<WorkItemTransitionService>>()),
            options);
    }

    public void Dispose() => _svc.Dispose();

    // ── AC4: single PVC pool — exactly one caller gets the PVC ────────────

    /// <summary>
    /// AC4: With a single PVC in the pool and no pre-claimed rows, two concurrent
    /// callers sharing the same <see cref="DispatchLifecycleService"/> instance must
    /// produce exactly one non-null result and one null result. <c>SelectPvcFromDbAsync</c>
    /// holds <c>_pvcSelectLock</c> across the query AND the <c>ClaimedPvcName</c> DB write,
    /// so the second caller's re-query inside the lock sees the first caller's claim.
    /// </summary>
    [Fact]
    public async Task SelectPvcFromDbAsync_WhenPoolHasOnePvc_ConcurrentCallersGetExactlyOneSuccessAndOneNull()
    {
        // Two different workItemIds — concurrent calls for different work items
        // both competing for the single PVC in the pool.
        var workItemId1 = Guid.NewGuid();
        var workItemId2 = Guid.NewGuid();

        // Pre-insert both WorkItem rows as Pending so SelectPvcFromDbAsync can write ClaimedPvcName.
        await using (var seedDb = _dbFactory.CreateDbContext())
        {
            seedDb.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId1,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"concurrent-1-{workItemId1:N}",
                IssueProviderConfigId = "prov",
                Status = WorkItemStatus.Pending,
                Payload = "{}",
                AgentSelector = "kiro,dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow
            });
            seedDb.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId2,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"concurrent-2-{workItemId2:N}",
                IssueProviderConfigId = "prov",
                Status = WorkItemStatus.Pending,
                Payload = "{}",
                AgentSelector = "kiro,dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await seedDb.SaveChangesAsync();
        }

        // Fire two concurrent calls — they share a single DispatchLifecycleService (and its lock).
        var task1 = _svc.SelectPvcFromDbAsync(_dbFactory, SinglePvcPool, workItemId1, "test1 ", CancellationToken.None);
        var task2 = _svc.SelectPvcFromDbAsync(_dbFactory, SinglePvcPool, workItemId2, "test2 ", CancellationToken.None);

        var results = await Task.WhenAll(task1, task2);
        var result1 = results[0];
        var result2 = results[1];

        // Exactly one caller should have succeeded (got kiro-pvc-0) and one should have gotten null.
        // The lock serializes both calls: the winner writes ClaimedPvcName=kiro-pvc-0 to the DB
        // while still holding the lock; the loser re-queries inside the lock and sees the claim.
        var successCount = results.Count(r => r == "kiro-pvc-0");
        var nullCount = results.Count(r => r is null);

        successCount.Should().Be(1,
            "exactly one of two concurrent callers must claim the single available PVC");
        nullCount.Should().Be(1,
            "the second concurrent caller must see the first's claim in the DB and return null");

        // Verify DB state: exactly one WorkItem has ClaimedPvcName=kiro-pvc-0
        await using var verifyDb = _dbFactory.CreateDbContext();
        var claimedCount = await verifyDb.WorkItems
            .CountAsync(w => w.ClaimedPvcName == "kiro-pvc-0");
        claimedCount.Should().Be(1,
            "exactly one WorkItem should have ClaimedPvcName written to the DB");
    }

    /// <summary>
    /// AC4: With a single PVC in the pool and no pre-claimed rows, a single call
    /// returns that PVC and writes ClaimedPvcName to the WorkItem row inside the lock.
    /// A subsequent call (after the first's ClaimedPvcName is now in the DB) returns null.
    /// </summary>
    [Fact]
    public async Task SelectPvcFromDbAsync_WhenPoolHasOneFreeAndOneClaimedByFirstDispatch_SecondGetsNull()
    {
        // Pre-insert a WorkItem row — SelectPvcFromDbAsync writes ClaimedPvcName to this row.
        var firstWorkItemId = Guid.NewGuid();
        await using (var seedDb = _dbFactory.CreateDbContext())
        {
            seedDb.WorkItems.Add(new WorkItemEntity
            {
                Id = firstWorkItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"sequential-first-{firstWorkItemId:N}",
                IssueProviderConfigId = "prov",
                Status = WorkItemStatus.Pending,
                Payload = "{}",
                AgentSelector = "kiro,dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await seedDb.SaveChangesAsync();
        }

        // First call — pool has one free PVC; WorkItem row exists so ClaimedPvcName is written.
        var firstResult = await _svc.SelectPvcFromDbAsync(
            _dbFactory, SinglePvcPool, firstWorkItemId, "test ", CancellationToken.None);

        firstResult.Should().Be("kiro-pvc-0", "pool has one free PVC and the WorkItem row exists");

        // Verify ClaimedPvcName was written to the DB by the first call (inside the lock).
        await using (var verifyDb = _dbFactory.CreateDbContext())
        {
            var written = await verifyDb.WorkItems.FindAsync(firstWorkItemId);
            written!.ClaimedPvcName.Should().Be("kiro-pvc-0",
                "SelectPvcFromDbAsync must write ClaimedPvcName to the DB inside the lock");
        }

        // Second call (different WorkItem, same pool) — DB now shows kiro-pvc-0 as claimed.
        var secondWorkItemId = Guid.NewGuid();
        await using (var seedDb2 = _dbFactory.CreateDbContext())
        {
            seedDb2.WorkItems.Add(new WorkItemEntity
            {
                Id = secondWorkItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"sequential-second-{secondWorkItemId:N}",
                IssueProviderConfigId = "prov",
                Status = WorkItemStatus.Pending,
                Payload = "{}",
                AgentSelector = "kiro,dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await seedDb2.SaveChangesAsync();
        }

        var secondResult = await _svc.SelectPvcFromDbAsync(
            _dbFactory, SinglePvcPool, secondWorkItemId, "test ", CancellationToken.None);

        secondResult.Should().BeNull(
            "kiro-pvc-0 was claimed by the first call and written to the DB inside the lock; " +
            "the second call's re-query inside the lock sees it as claimed");
    }

    /// <summary>
    /// AC4: With two PVCs in the pool and no pre-claimed rows, two sequential
    /// calls each get a distinct PVC. The first call writes ClaimedPvcName=kiro-pvc-0
    /// to the DB inside the lock; the second call's re-query sees that claim and
    /// returns kiro-pvc-1.
    /// </summary>
    [Fact]
    public async Task SelectPvcFromDbAsync_WhenPoolHasTwoPvcs_TwoConcurrentCallsGetDistinctPvcs()
    {
        // Pre-insert a WorkItem for the first call
        var firstId = Guid.NewGuid();
        await using (var db = _dbFactory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = firstId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"two-pvc-first-{firstId:N}",
                IssueProviderConfigId = "prov",
                Status = WorkItemStatus.Pending,
                Payload = "{}",
                AgentSelector = "kiro,dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // First call gets kiro-pvc-0 and writes it to the DB inside the lock
        var first = await _svc.SelectPvcFromDbAsync(
            _dbFactory, TwoPvcPool, firstId, "test ", CancellationToken.None);
        first.Should().Be("kiro-pvc-0");

        // Pre-insert a WorkItem for the second call
        var secondId = Guid.NewGuid();
        await using (var db2 = _dbFactory.CreateDbContext())
        {
            db2.WorkItems.Add(new WorkItemEntity
            {
                Id = secondId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"two-pvc-second-{secondId:N}",
                IssueProviderConfigId = "prov",
                Status = WorkItemStatus.Pending,
                Payload = "{}",
                AgentSelector = "kiro,dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db2.SaveChangesAsync();
        }

        // Second call re-queries under lock — kiro-pvc-0 is now claimed (written by the first call),
        // so it selects kiro-pvc-1 and writes it to the DB.
        var second = await _svc.SelectPvcFromDbAsync(
            _dbFactory, TwoPvcPool, secondId, "test ", CancellationToken.None);
        second.Should().Be("kiro-pvc-1",
            "kiro-pvc-0 was claimed and written to DB by the first call; the second call gets kiro-pvc-1");

        first.Should().NotBe(second, "two callers must receive distinct PVCs from the pool");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal IDbContextFactory for tests — uses InMemory EF.
    /// </summary>
    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly string _dbName;
        public InMemoryDbContextFactory(string dbName) => _dbName = dbName;

        public PipelineDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new TestPipelineDbContext(options);
        }

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// PipelineDbContext subclass that disables Postgres-specific features for InMemory compatibility.
    /// </summary>
    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexesToRemove = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var index in indexesToRemove)
                    entityType.RemoveIndex(index);
            }
        }
    }
}
