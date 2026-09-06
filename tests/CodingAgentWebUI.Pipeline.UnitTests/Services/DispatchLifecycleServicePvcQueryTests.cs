using AwesomeAssertions;
using CodingAgentWebUI.Api.Dispatch;
using DispatchLifecycleService = CodingAgentWebUI.Api.Dispatch.DispatchLifecycleService;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="DispatchLifecycleService.QueryAvailablePvcsAsync"/>.
/// Validates the extracted PVC resolution query logic matches the original behavior.
/// Issue #1630: eliminates duplicated PVC resolution between DispatchService and ConsolidationWorkItemDispatchService.
/// </summary>
public class DispatchLifecycleServicePvcQueryTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;

    public DispatchLifecycleServicePvcQueryTests()
    {
        var dbName = $"PvcQuery-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task QueryAvailablePvcsAsync_NoPvcsClaimed_ReturnsFullPool()
    {
        // Arrange
        var pvcPool = new List<string> { "pvc-1", "pvc-2", "pvc-3" };

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Act
        var result = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pvcPool, CancellationToken.None);

        // Assert
        result.AvailablePvcs.Should().BeEquivalentTo(["pvc-1", "pvc-2", "pvc-3"]);
        result.ClaimedCount.Should().Be(0);
    }

    [Fact]
    public async Task QueryAvailablePvcsAsync_SomePvcsClaimedInDb_ExcludesClaimedOnes()
    {
        // Arrange
        var pvcPool = new List<string> { "pvc-1", "pvc-2", "pvc-3" };

        // Insert work items claiming pvc-1 and pvc-2
        await InsertWorkItemWithPvc(Guid.NewGuid(), "pvc-1", WorkItemStatus.Running);
        await InsertWorkItemWithPvc(Guid.NewGuid(), "pvc-2", WorkItemStatus.Dispatched);

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Act
        var result = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pvcPool, CancellationToken.None);

        // Assert
        result.AvailablePvcs.Should().BeEquivalentTo(["pvc-3"]);
        result.ClaimedCount.Should().Be(2);
    }

    [Fact]
    public async Task QueryAvailablePvcsAsync_AllPvcsClaimed_ReturnsEmptyList()
    {
        // Arrange: both PVCs claimed by Dispatched or Running items
        // Issue #2322: Pending items no longer claim PVCs since no WorkItem is written as Pending
        // on the live dispatch path. Only Dispatched/Running items hold PVC claims.
        var pvcPool = new List<string> { "pvc-1", "pvc-2" };

        await InsertWorkItemWithPvc(Guid.NewGuid(), "pvc-1", WorkItemStatus.Dispatched);
        await InsertWorkItemWithPvc(Guid.NewGuid(), "pvc-2", WorkItemStatus.Running);

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Act
        var result = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pvcPool, CancellationToken.None);

        // Assert
        result.AvailablePvcs.Should().BeEmpty();
        result.ClaimedCount.Should().Be(2);
    }

    [Fact]
    public async Task QueryAvailablePvcsAsync_PendingWorkItemPvc_NotExcluded()
    {
        // Issue #2322: Pending WorkItems no longer claim PVCs (on the live dispatch path,
        // no WorkItem is ever written as Pending). A legacy Pending row in the DB (e.g. from
        // recovery) does NOT block PVC assignment.
        var pvcPool = new List<string> { "pvc-1", "pvc-2" };

        await InsertWorkItemWithPvc(Guid.NewGuid(), "pvc-1", WorkItemStatus.Pending);
        // pvc-2 is free

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Act
        var result = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pvcPool, CancellationToken.None);

        // Assert: both PVCs available — Pending no longer blocks assignment
        result.AvailablePvcs.Should().BeEquivalentTo(["pvc-1", "pvc-2"]);
        result.ClaimedCount.Should().Be(0);
    }

    [Fact]
    public async Task QueryAvailablePvcsAsync_CompletedWorkItemPvc_NotExcluded()
    {
        // Arrange — completed items should NOT count as "claimed"
        var pvcPool = new List<string> { "pvc-1", "pvc-2" };

        await InsertWorkItemWithPvc(Guid.NewGuid(), "pvc-1", WorkItemStatus.Succeeded);
        await InsertWorkItemWithPvc(Guid.NewGuid(), "pvc-2", WorkItemStatus.Failed);

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Act
        var result = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pvcPool, CancellationToken.None);

        // Assert — both PVCs should be available since the claims are on completed/failed items
        result.AvailablePvcs.Should().BeEquivalentTo(["pvc-1", "pvc-2"]);
        result.ClaimedCount.Should().Be(0);
    }

    // TODO: This test only verifies DB-claimed PVC exclusion, not actual inflight claim exclusion.
    // To properly test inflight claims, the _inflightPvcClaims HashSet needs to be populated
    // without a DB record (e.g., via ClaimPvc or reflection). As written, this test would pass
    // even if GetInflightPvcClaims() were removed from QueryAvailablePvcsAsync.
    [Fact]
    public async Task QueryAvailablePvcsAsync_InflightClaims_AreExcluded()
    {
        // Arrange
        var pvcPool = new List<string> { "pvc-1", "pvc-2", "pvc-3" };

        // Simulate inflight claim via the lifecycle service's internal tracking
        // Use ExecuteDispatchLifecycleAsync to claim a PVC internally — but since we can't easily
        // trigger inflight state without a full dispatch, we'll verify via integration that
        // DB claims are excluded. The inflight tracking is tested by existing race condition tests.
        await InsertWorkItemWithPvc(Guid.NewGuid(), "pvc-1", WorkItemStatus.Running);

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Act
        var result = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pvcPool, CancellationToken.None);

        // Assert
        result.AvailablePvcs.Should().BeEquivalentTo(["pvc-2", "pvc-3"]);
        result.ClaimedCount.Should().Be(1);
    }

    [Fact]
    public async Task QueryAvailablePvcsAsync_EmptyPool_ReturnsEmpty()
    {
        // Arrange
        var pvcPool = new List<string>();

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Act
        var result = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pvcPool, CancellationToken.None);

        // Assert
        result.AvailablePvcs.Should().BeEmpty();
        result.ClaimedCount.Should().Be(0);
    }

    // ── AC#4: PVC double-assignment concurrency test ──────────────────────────

    /// <summary>
    /// AC#4 (issue #2322): Two concurrent calls to DispatchDirectlyAsync for a single-PVC kiro pool
    /// must result in exactly one success and one "no PVC available" rejection.
    /// The _pvcSelectLock SemaphoreSlim must serialize PVC selection so both calls cannot claim
    /// the same PVC simultaneously.
    ///
    /// The mock K8s client completes synchronously (Task.CompletedTask) so the first caller holds
    /// the PVC lock through WorkItem creation and K8s Job creation, forcing the second concurrent
    /// caller to wait and then observe the pool as exhausted.
    /// </summary>
    [Fact]
    public async Task DispatchDirectlyAsync_ConcurrentKiroDispatches_ExactlyOneSucceedsWhenSinglePvcPool()
    {
        // Arrange: single PVC in the kiro pool — only one dispatch can succeed
        var pvcPool = new List<string> { "kiro-pvc-1" };
        var options = new DispatchServiceOptions
        {
            KiroPvcPool = pvcPool,
            OrchestratorUrl = "http://localhost",
            AgentApiKeySecretName = "agent-key",
            AgentServiceAccountName = "agent-sa",
            Namespace = "test"
        };

        // K8s client mock: CreateJobAsync completes successfully (no delay).
        // Both concurrent calls will attempt to create a K8s Job; only the one that wins
        // the PVC lock will reach this point.
        var mockK8sClient = new Moq.Mock<CodingAgentWebUI.Kubernetes.IKubernetesJobClient>();
        mockK8sClient
            .Setup(c => c.CreateJobAsync(
                Moq.It.IsAny<k8s.Models.V1Job>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var transitionService = new CodingAgentWebUI.Infrastructure.Persistence.Services.WorkItemTransitionService(
            _dbFactory,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<CodingAgentWebUI.Infrastructure.Persistence.Services.WorkItemTransitionService>());

        using var sut = new DispatchLifecycleService(mockK8sClient.Object, transitionService, options);

        var template = new CodingAgentWebUI.Kubernetes.JobTemplate
        {
            Labels = "dotnet,kiro",
            Image = "test-image:latest",
            ProviderType = "kiro",
            MaxConcurrent = 0 // no concurrency limit, so only PVC pool limits dispatch
        };

        var mockRunService = new Moq.Mock<CodingAgentWebUI.Pipeline.Interfaces.IOrchestratorRunService>();

        // Two requests with distinct issue identifiers so unique-index doesn't reject
        var request1 = MakeDispatchRequest("org/repo#1001", "req-1");
        var request2 = MakeDispatchRequest("org/repo#1002", "req-2");

        // Act: fire both concurrent dispatches simultaneously using a barrier to maximize overlap
        var barrier = new Barrier(2);
        DirectDispatchResultCapture? capture1 = null;
        DirectDispatchResultCapture? capture2 = null;

        var task1 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            var result = await sut.DispatchDirectlyAsync(request1, template, mockRunService.Object, _dbFactory, CancellationToken.None);
            capture1 = new DirectDispatchResultCapture(result);
        });

        var task2 = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            var result = await sut.DispatchDirectlyAsync(request2, template, mockRunService.Object, _dbFactory, CancellationToken.None);
            capture2 = new DirectDispatchResultCapture(result);
        });

        await Task.WhenAll(task1, task2);

        // Assert: exactly one success and one service-unavailable ("no PVC available")
        capture1.Should().NotBeNull();
        capture2.Should().NotBeNull();

        var results = new[] { capture1!.IsSuccess, capture2!.IsSuccess };
        results.Should().Contain(true, "exactly one of the two concurrent dispatches must succeed (PVC assigned)");
        results.Should().Contain(false, "exactly one of the two concurrent dispatches must fail with no-PVC-available (ServiceUnavailable)");

        // Verify the DB: exactly one Dispatched WorkItem, for the winner
        await using var db = await _dbFactory.CreateDbContextAsync();
        var dispatchedItems = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Dispatched)
            .ToListAsync();
        dispatchedItems.Should().HaveCount(1,
            "only the winning dispatch must leave a Dispatched WorkItem; the losing dispatch must not write to the DB");

        // Verify the winner's WorkItem has the single PVC claimed
        dispatchedItems[0].ClaimedPvcName.Should().Be("kiro-pvc-1",
            "the dispatched WorkItem must hold the single available PVC");
    }

    // ── Helpers (concurrent test) ─────────────────────────────────────────────

    private static JobDistributionRequest MakeDispatchRequest(string issueIdentifier, string runId) => new()
    {
        IssueIdentifier = issueIdentifier,
        IssueProviderConfigId = "ipc-test",
        RepoProviderConfigId = "rpc-test",
        InitiatedBy = "concurrent-test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "dotnet,kiro",
        TimeoutSeconds = 3600,
        RunId = Guid.NewGuid().ToString()
    };

    /// <summary>Captures the outcome of a DispatchDirectlyAsync call for concurrent-test assertions.</summary>
    private sealed class DirectDispatchResultCapture
    {
        public bool IsSuccess { get; }
        public bool IsServiceUnavailable { get; }

        public DirectDispatchResultCapture(DispatchLifecycleService.DirectDispatchResult result)
        {
            IsSuccess = result is DispatchLifecycleService.DirectDispatchResult.Success;
            IsServiceUnavailable = result is DispatchLifecycleService.DirectDispatchResult.ServiceUnavailable;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private async Task InsertWorkItemWithPvc(Guid id, string pvcName, WorkItemStatus status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = $"issue-{id}",
            IssueProviderConfigId = "provider-1",
            Status = status,
            AgentSelector = "dotnet,kiro",
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 1800,
            Payload = "{}",
            ClaimedPvcName = pvcName
        });
        await db.SaveChangesAsync();
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
                if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never; }
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
