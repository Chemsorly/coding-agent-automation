using AwesomeAssertions;
using CodingAgentWebUI.Api.Dispatch;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for AC4 (PVC double-assignment regression):
/// "Two concurrent calls to POST /api/work-items/dispatch for the same kiro PVC pool result in
/// exactly one 200 and one 503; the DispatchLifecycleService._pvcSelectLock serializes both."
///
/// The <see cref="DispatchLifecycleService._pvcSelectLock"/> (SemaphoreSlim(1,1)) spans
/// <see cref="DispatchLifecycleService.QueryAvailablePvcsAsync"/> through the
/// <c>SaveChangesAsync</c> that writes the WorkItem row with <c>ClaimedPvcName</c> set.
/// This ensures the second concurrent caller sees the first caller's claimed PVC as unavailable.
///
/// Note: The lock is an in-process <see cref="System.Threading.SemaphoreSlim"/>. With a single
/// API replica (which is the current production deployment), it correctly serializes all concurrent
/// dispatch requests. Multi-replica scenarios require a distributed lock (Postgres advisory lock);
/// that caveat is noted in the issue requirements and is out of scope for this test.
/// </summary>
public sealed class DispatchLifecycleServicePvcConcurrencyTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly Mock<IOrchestratorRunService> _runService = new();

    public DispatchLifecycleServicePvcConcurrencyTests()
    {
        var dbName = $"PvcConcurrency-{Guid.NewGuid()}";
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

    /// <summary>
    /// AC2: "A Review WorkItem is dispatched (K8s Job created) before a same-age or newer
    /// Implementation WorkItem when the new synchronous endpoint is called."
    ///
    /// The synchronous dispatch endpoint is order-preserving: the first call to
    /// <see cref="DispatchLifecycleService.ExecuteSynchronousDispatchAsync"/> completes before the
    /// second, so when the Scheduler calls Review first (per its priority ordering), the Review
    /// WorkItem is created and dispatched before the Implementation WorkItem.
    ///
    /// This test calls the method sequentially (Review, then Implementation) and verifies:
    /// 1. Both calls succeed.
    /// 2. The Review WorkItem has an earlier-or-equal <c>CreatedAt</c> timestamp than Implementation.
    /// 3. Neither creates a Pending WorkItem (both are Dispatched directly).
    /// </summary>
    [Fact]
    public async Task ReviewDispatchedBeforeImplementation_WhenReviewCalledFirst()
    {
        // Arrange
        var options = new DispatchServiceOptions { KiroPvcPool = ["pvc-0", "pvc-1"] };
        var svc = new DispatchLifecycleService(
            kubeClient: null,
            transitionService: BuildTransitionService(),
            options: options,
            templateProvider: BuildTemplateStore("kiro", maxConcurrent: 10));

        var reviewId = Guid.NewGuid();
        var implId = Guid.NewGuid();

        var reviewRequest = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("PR-review-1"),
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Review,
            AgentSelector = "kiro",
            TimeoutSeconds = 3600,
            RunId = Guid.NewGuid().ToString()
        };

        var implRequest = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("ISSUE-impl-1"),
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro",
            TimeoutSeconds = 3600,
            RunId = Guid.NewGuid().ToString()
        };

        // Act: Scheduler sends Review first (higher priority), then Implementation
        var (reviewWorkItemId, reviewStatus) = await svc.ExecuteSynchronousDispatchAsync(
            reviewRequest, reviewId, "{}", _dbFactory, _runService.Object, CancellationToken.None);

        var (implWorkItemId, implStatus) = await svc.ExecuteSynchronousDispatchAsync(
            implRequest, implId, "{}", _dbFactory, _runService.Object, CancellationToken.None);

        // Assert: both dispatches succeeded
        reviewStatus.Should().BeNull("Review dispatch must succeed");
        implStatus.Should().BeNull("Implementation dispatch must succeed");
        reviewWorkItemId.Should().Be(reviewId, "returned WorkItemId must match the Review request's id");
        implWorkItemId.Should().Be(implId, "returned WorkItemId must match the Implementation request's id");

        // Verify both WorkItems exist as Dispatched (not Pending)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var reviewItem = await db.WorkItems.FindAsync(reviewId);
        var implItem = await db.WorkItems.FindAsync(implId);

        reviewItem.Should().NotBeNull();
        implItem.Should().NotBeNull();
        reviewItem!.Status.Should().Be(WorkItemStatus.Dispatched, "Review must be Dispatched, not Pending");
        implItem!.Status.Should().Be(WorkItemStatus.Dispatched, "Implementation must be Dispatched, not Pending");
        reviewItem.TaskType.Should().Be(WorkItemTaskType.Review);
        implItem.TaskType.Should().Be(WorkItemTaskType.Implementation);

        // Ordering: Review CreatedAt must be <= Implementation CreatedAt
        // (Review was dispatched first, so it was inserted first)
        reviewItem.CreatedAt.Should().BeOnOrBefore(implItem.CreatedAt,
            "Review dispatched before Implementation must have an earlier-or-equal CreatedAt timestamp");
    }

    // ── AC4: PVC double-assignment serialisation ─────────────────────────────────────────────────

    /// <summary>
    /// AC4: Two concurrent calls to ExecuteSynchronousDispatchAsync targeting the same single-PVC
    /// kiro pool must result in exactly one success (null statusCode) and one 503.
    /// The <see cref="DispatchLifecycleService._pvcSelectLock"/> serializes the PVC query + DB write
    /// so the second caller sees the first caller's claim and returns 503.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentDispatches_SingleKiroPvc_ExactlyOneSuccessOneFailure()
    {
        // Arrange — single PVC pool; no K8s client (skips actual job creation, tests lock serialization)
        const string singlePvc = "kiro-pvc-0";
        var options = new DispatchServiceOptions { KiroPvcPool = [singlePvc] };
        var transitionSvc = BuildTransitionService();
        var svc = new DispatchLifecycleService(
            kubeClient: null,             // no K8s — focuses test on lock + DB write
            transitionService: transitionSvc,
            options: options,
            templateProvider: BuildTemplateStore("kiro", maxConcurrent: 10));

        var request1 = MakeKiroRequest("GH-1001");
        var request2 = MakeKiroRequest("GH-1002");  // different issue to avoid dedup 409
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        // Act — issue both calls concurrently
        var task1 = svc.ExecuteSynchronousDispatchAsync(request1, id1, "{}", _dbFactory, _runService.Object, CancellationToken.None);
        var task2 = svc.ExecuteSynchronousDispatchAsync(request2, id2, "{}", _dbFactory, _runService.Object, CancellationToken.None);

        var (r1, r2) = await (task1, task2).WhenBoth();

        // Assert: exactly one 200 and one 503 (order not guaranteed — depends on scheduling)
        var results = new[] { r1, r2 };
        var successes = results.Count(r => r.statusCode is null);
        var rejections = results.Count(r => r.statusCode == 503);

        successes.Should().Be(1, "exactly one dispatch should succeed when only one PVC is available");
        rejections.Should().Be(1, "the other dispatch should be rejected with 503 (no PVC)");

        // Verify only one WorkItem was created as Dispatched with the PVC claimed
        await using var db = await _dbFactory.CreateDbContextAsync();
        var workItems = await db.WorkItems.ToListAsync();
        workItems.Should().HaveCount(1, "only the successful dispatch creates a WorkItem");
        workItems[0].Status.Should().Be(WorkItemStatus.Dispatched);
        workItems[0].ClaimedPvcName.Should().Be(singlePvc);
    }

    /// <summary>
    /// AC4 (sequential variant): Verifies that after a dispatch claims the only PVC, a subsequent
    /// (not concurrent) dispatch for a different issue also receives 503.
    /// This confirms the DB-persisted claim is read correctly by the next caller.
    /// </summary>
    [Fact]
    public async Task SecondDispatch_AfterPvcClaimed_Returns503()
    {
        // Arrange
        const string singlePvc = "kiro-pvc-0";
        var options = new DispatchServiceOptions { KiroPvcPool = [singlePvc] };
        var svc = new DispatchLifecycleService(
            kubeClient: null,
            transitionService: BuildTransitionService(),
            options: options,
            templateProvider: BuildTemplateStore("kiro", maxConcurrent: 10));

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        // Act: first dispatch claims the PVC
        var (_, firstStatus) = await svc.ExecuteSynchronousDispatchAsync(
            MakeKiroRequest("GH-2001"), id1, "{}", _dbFactory, _runService.Object, CancellationToken.None);

        // Act: second dispatch finds no available PVC
        var (secondId, secondStatus) = await svc.ExecuteSynchronousDispatchAsync(
            MakeKiroRequest("GH-2002"), id2, "{}", _dbFactory, _runService.Object, CancellationToken.None);

        // Assert
        firstStatus.Should().BeNull("first dispatch should succeed");
        secondStatus.Should().Be(503, "second dispatch should fail with 503 when no PVC remains");
        secondId.Should().BeNull("rejected dispatch returns null WorkItemId");
    }

    /// <summary>
    /// AC4 (pool with two PVCs): Two concurrent dispatches targeting a two-PVC pool should both succeed,
    /// each claiming a distinct PVC. No PVC double-assignment should occur.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentDispatches_TwoPvcPool_BothSucceedWithDistinctPvcs()
    {
        // Arrange
        var options = new DispatchServiceOptions { KiroPvcPool = ["pvc-a", "pvc-b"] };
        var svc = new DispatchLifecycleService(
            kubeClient: null,
            transitionService: BuildTransitionService(),
            options: options,
            templateProvider: BuildTemplateStore("kiro", maxConcurrent: 10));

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        // Act — concurrent dispatches
        var task1 = svc.ExecuteSynchronousDispatchAsync(
            MakeKiroRequest("GH-3001"), id1, "{}", _dbFactory, _runService.Object, CancellationToken.None);
        var task2 = svc.ExecuteSynchronousDispatchAsync(
            MakeKiroRequest("GH-3002"), id2, "{}", _dbFactory, _runService.Object, CancellationToken.None);

        var (r1, r2) = await (task1, task2).WhenBoth();

        // Assert: both succeed (no 503 since two PVCs are available)
        r1.statusCode.Should().BeNull("first dispatch should succeed with two PVCs available");
        r2.statusCode.Should().BeNull("second dispatch should succeed with two PVCs available");

        // Verify both WorkItems exist as Dispatched with distinct PVCs
        await using var db = await _dbFactory.CreateDbContextAsync();
        var workItems = await db.WorkItems.ToListAsync();
        workItems.Should().HaveCount(2);
        workItems.All(w => w.Status == WorkItemStatus.Dispatched).Should().BeTrue();

        var claimedPvcs = workItems.Select(w => w.ClaimedPvcName).ToList();
        claimedPvcs.Should().OnlyHaveUniqueItems("each dispatch must claim a distinct PVC");
        claimedPvcs.Should().BeSubsetOf(["pvc-a", "pvc-b"]);
    }

    /// <summary>
    /// AC4 (non-kiro selector): Non-kiro dispatches bypass the PVC lock; concurrency limit is the
    /// only guard. Verify that two concurrent non-kiro dispatches both succeed when under the limit,
    /// and neither creates a WorkItem with a PVC claim.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentDispatches_NonKiroSelector_BothSucceedNoPvcClaimed()
    {
        // Arrange — no PVC pool configured for non-kiro selector
        var options = new DispatchServiceOptions { KiroPvcPool = [] };
        var svc = new DispatchLifecycleService(
            kubeClient: null,
            transitionService: BuildTransitionService(),
            options: options,
            templateProvider: BuildTemplateStore("opencode", maxConcurrent: 10));

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var task1 = svc.ExecuteSynchronousDispatchAsync(
            MakeRequest("GH-4001", "opencode"), id1, "{}", _dbFactory, _runService.Object, CancellationToken.None);
        var task2 = svc.ExecuteSynchronousDispatchAsync(
            MakeRequest("GH-4002", "opencode"), id2, "{}", _dbFactory, _runService.Object, CancellationToken.None);

        var (r1, r2) = await (task1, task2).WhenBoth();

        // Both should succeed (non-kiro, no PVC constraint)
        r1.statusCode.Should().BeNull();
        r2.statusCode.Should().BeNull();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var workItems = await db.WorkItems.ToListAsync();
        workItems.Should().HaveCount(2);
        workItems.All(w => w.ClaimedPvcName == null).Should().BeTrue(
            "non-kiro dispatches must not claim a PVC");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static JobDistributionRequest MakeKiroRequest(string issueId) =>
        MakeRequest(issueId, "kiro");

    private static JobDistributionRequest MakeRequest(string issueId, string agentSelector) => new()
    {
        IssueIdentifier = new IssueIdentifier(issueId),
        IssueProviderConfigId = "github",
        RepoProviderConfigId = "github-repo",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = agentSelector,
        TimeoutSeconds = 3600,
        RunId = Guid.NewGuid().ToString()
    };

    private WorkItemTransitionService BuildTransitionService() =>
        new WorkItemTransitionService(_dbFactory, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkItemTransitionService>.Instance);

    /// <summary>
    /// Builds a <see cref="JobTemplateStore"/> with a single template matching <paramref name="selector"/>.
    /// The template's ProviderType is set so that the kiro-vs-non-kiro branch in
    /// <see cref="DispatchLifecycleService.ExecuteSynchronousDispatchAsync"/> resolves correctly.
    /// </summary>
    private static JobTemplateStore BuildTemplateStore(string selector, int maxConcurrent)
    {
        // JobTemplateStore normalizes the Labels field for lookup key.
        // ProviderType must be "kiro" (case-insensitive) to trigger _pvcSelectLock path.
        var json = $$"""
            [
              {
                "labels": "{{selector}}",
                "image": "test-image:latest",
                "providerType": "{{selector}}",
                "maxConcurrent": {{maxConcurrent}}
              }
            ]
            """;
        return JobTemplateStore.LoadFromJson(json);
    }

    // ── Inner types ───────────────────────────────────────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Disable concurrency tokens (InMemory doesn't support row-version concurrency)
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            // Remove filtered unique indexes (InMemory does not enforce them)
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

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}

/// <summary>Extension to await two tasks concurrently and return both results.</summary>
internal static class TaskExtensions
{
    public static async Task<(T1, T2)> WhenBoth<T1, T2>(this (Task<T1>, Task<T2>) tasks)
    {
        await Task.WhenAll(tasks.Item1, tasks.Item2);
        return (tasks.Item1.Result, tasks.Item2.Result);
    }
}
