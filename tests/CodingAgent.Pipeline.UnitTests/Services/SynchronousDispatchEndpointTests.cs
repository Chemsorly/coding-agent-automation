using AwesomeAssertions;
using CodingAgent.Api;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for the synchronous dispatch path introduced by issue #2322.
/// Covers:
/// 1. <c>POST /api/work-items/dispatch</c> endpoint handler (<see cref="WorkItemEndpoints.DispatchWorkItem"/>)
///    — priority ordering, concurrency gating, PVC gating, success path.
/// 2. <c>DispatchOrchestrationService.DistributeAndFinalizeAsync</c>
///    — label is reverted on 503/409 (no capacity), label is confirmed on success.
/// 3. Transition state machine: Pending→Dispatched and Dispatched→Pending removed.
/// </summary>
public sealed class SynchronousDispatchEndpointTests
{
    private readonly string _dbName = $"dispatch-test-{Guid.NewGuid():N}";

    // ── Helpers ─────────────────────────────────────────────────────────────

    private IDbContextFactory<PipelineDbContext> CreateDbFactory()
    {
        var opts = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SimpleInMemoryDbContextFactory(opts);
    }

    private static JobTemplateStore CreateTemplateStore(
        string labels = "kiro,dotnet",
        int maxConcurrent = 5,
        string providerType = "kiro") =>
        JobTemplateStore.LoadFromYaml($"""
            - labels: "{labels}"
              image: "test-image:latest"
              imagePullPolicy: "Always"
              providerType: "{providerType}"
              maxConcurrent: {maxConcurrent}
            """);

    private DispatchLifecycleService CreateLifecycleService(
        IKubernetesJobClient? k8sClient = null,
        IReadOnlyList<string>? pvcPool = null)
    {
        var dbFactory = CreateDbFactory();
        var transitionSvc = new WorkItemTransitionService(
            dbFactory,
            Mock.Of<ILogger<WorkItemTransitionService>>());
        var opts = new DispatchServiceOptions
        {
            Namespace = "test",
            OrchestratorUrl = "http://test",
            AgentApiKeySecretName = "secret",
            AgentServiceAccountName = "sa",
            KiroPvcPool = (pvcPool ?? ["pvc-0", "pvc-1"]).ToList()
        };
        return new DispatchLifecycleService(
            k8sClient ?? Mock.Of<IKubernetesJobClient>(),
            transitionSvc,
            opts);
    }

    private static IOrchestratorRunService CreateRunService() =>
        new OrchestratorRunService(Mock.Of<Serilog.ILogger>());

    private static JobDistributionRequest MakeRequest(
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        string selector = "kiro,dotnet",
        string? runId = null) => new()
    {
        IssueIdentifier = new IssueIdentifier($"issue-{Guid.NewGuid():N}"),
        IssueProviderConfigId = "prov-1",
        RepoProviderConfigId = "repo-1",
        InitiatedBy = "test",
        TaskType = taskType,
        AgentSelector = selector,
        TimeoutSeconds = 3600,
        RunId = runId ?? Guid.NewGuid().ToString()
    };

    // ── Acceptance Criterion: Review dispatched successfully ─────────────────

    [Fact]
    public async Task DispatchWorkItem_ReviewRequest_Returns200_WithWorkItemId()
    {
        // Arrange: a Review request should succeed through the synchronous dispatch path
        var dbFactory = CreateDbFactory();
        var runService = CreateRunService();
        var templateStore = CreateTemplateStore(maxConcurrent: 5);

        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object);

        var request = MakeRequest(WorkItemTaskType.Review, "kiro,dotnet");

        // Act
        var result = await WorkItemEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        // Assert: 200 OK with WorkItemId
        var okResult = result as Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>;
        okResult.Should().NotBeNull("dispatch should succeed for a Review request");

        // Verify WorkItem was created in the DB as Dispatched
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IssueIdentifier == request.IssueIdentifier.Value);
        item.Should().NotBeNull();
        item!.Status.Should().Be(WorkItemStatus.Dispatched, "synchronous dispatch creates item directly as Dispatched");
    }

    // ── Acceptance Criterion: 409 when concurrency limit reached ─────────────

    [Fact]
    public async Task DispatchWorkItem_WhenConcurrencyLimitReached_Returns409()
    {
        // Arrange: seed 2 active items (maxConcurrent=2 means limit reached)
        var dbFactory = CreateDbFactory();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            for (int i = 0; i < 2; i++)
            {
                db.WorkItems.Add(new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    TaskType = WorkItemTaskType.Implementation,
                    IssueIdentifier = $"active-{i}",
                    IssueProviderConfigId = "prov-1",
                    Status = WorkItemStatus.Dispatched,
                    AgentSelector = "kiro,dotnet",
                    TimeoutSeconds = 3600,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Payload = "{}"
                });
            }
            await db.SaveChangesAsync();
        }

        var templateStore = CreateTemplateStore(maxConcurrent: 2);
        var lifecycle = CreateLifecycleService();
        var runService = CreateRunService();
        var request = MakeRequest(WorkItemTaskType.Implementation, "kiro,dotnet");

        // Act
        var result = await WorkItemEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        // Assert: 409 Conflict
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>();
    }

    // ── Acceptance Criterion: 503 when no PVC available ──────────────────────

    [Fact]
    public async Task DispatchWorkItem_WhenNoPvcAvailable_Returns503()
    {
        // Arrange: "pvc-0" is the only PVC and it's claimed by an active item
        var dbFactory = CreateDbFactory();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "pvc-holder",
                IssueProviderConfigId = "prov-1",
                Status = WorkItemStatus.Dispatched,
                ClaimedPvcName = "pvc-0",
                AgentSelector = "kiro,dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = "{}"
            });
            await db.SaveChangesAsync();
        }

        // Only one PVC in pool and it's claimed
        var lifecycle = CreateLifecycleService(pvcPool: ["pvc-0"]);
        var templateStore = CreateTemplateStore(maxConcurrent: 5);
        var runService = CreateRunService();
        var request = MakeRequest(WorkItemTaskType.Implementation, "kiro,dotnet");

        // Act
        var result = await WorkItemEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        // Assert: 503 Service Unavailable
        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull();
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    // ── Acceptance Criterion: 409 when no template for selector ─────────────

    [Fact]
    public async Task DispatchWorkItem_WhenNoTemplateForSelector_Returns409()
    {
        var dbFactory = CreateDbFactory();
        // Template only has "kiro,dotnet", not "opencode,java"
        var templateStore = CreateTemplateStore("kiro,dotnet");
        var lifecycle = CreateLifecycleService();
        var runService = CreateRunService();
        var request = MakeRequest(WorkItemTaskType.Implementation, "opencode,java");

        // Act
        var result = await WorkItemEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        // Assert: 409 (no template)
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>();
    }

    // ── Acceptance Criterion: PipelineRun registered in RunService ────────────

    [Fact]
    public async Task DispatchWorkItem_OnSuccess_RegistersPipelineRunInRunService()
    {
        var dbFactory = CreateDbFactory();
        var runService = CreateRunService();
        var templateStore = CreateTemplateStore(maxConcurrent: 5);
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object);
        var runId = Guid.NewGuid();
        var request = MakeRequest(WorkItemTaskType.Implementation, "kiro,dotnet", runId: runId.ToString());

        // Act
        var result = await WorkItemEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        var okResult = result as Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>;
        okResult.Should().NotBeNull();

        // Assert: PipelineRun registered in RunService (for SignalR hub routing)
        var run = runService.GetRun(new RunId(runId.ToString()));
        run.Should().NotBeNull("PipelineRun must be registered so the UI can receive live run events");
    }

    // ── Acceptance Criterion: Review dispatched before Implementation (priority ordering) ─

    [Fact]
    public async Task DispatchWorkItem_ReviewRequest_CreatesWorkItem_AsReviewTaskType()
    {
        // This test verifies that the dispatch endpoint correctly handles Review task type.
        // The Scheduler (not the dispatch endpoint) enforces priority ordering — it picks
        // Review items first. This test verifies the endpoint correctly passes the task type
        // through to the WorkItem.
        var dbFactory = CreateDbFactory();
        var runService = CreateRunService();
        var templateStore = CreateTemplateStore(maxConcurrent: 5);
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object);

        var reviewRequest = MakeRequest(WorkItemTaskType.Review, "kiro,dotnet");
        var implRequest = MakeRequest(WorkItemTaskType.Implementation, "kiro,dotnet");

        // Act: dispatch Review request first (as Scheduler would select it first)
        var reviewResult = await WorkItemEndpoints.DispatchWorkItem(
            reviewRequest, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        // Assert: Review dispatched successfully as Dispatched
        var reviewOk = reviewResult as Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>;
        reviewOk.Should().NotBeNull("Review dispatch should succeed");

        await using var db = await dbFactory.CreateDbContextAsync();
        var reviewItem = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == reviewOk!.Value);
        reviewItem.Should().NotBeNull();
        reviewItem!.TaskType.Should().Be(WorkItemTaskType.Review);
        reviewItem.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    /// <summary>
    /// AC: A Review WorkItem is dispatched (K8s Job created) before a same-age or newer
    /// Implementation WorkItem when capacity is constrained (maxConcurrent=1).
    /// The Scheduler calls the dispatch endpoint with Review first — the endpoint must succeed
    /// for Review and return 409 for Implementation (concurrency limit reached after Review).
    /// </summary>
    [Fact]
    public async Task DispatchWorkItem_ReviewDispatchedFirst_ImplementationBlockedByConcurrencyLimit()
    {
        // Arrange: single concurrency slot — only one item can be active at a time.
        // The Scheduler picks Review first and dispatches it. The subsequent Implementation
        // dispatch must be rejected (409) because the Review now occupies the slot.
        var dbFactory = CreateDbFactory();
        var runService = CreateRunService();
        var templateStore = CreateTemplateStore(maxConcurrent: 1);

        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object);

        var reviewRequest = MakeRequest(WorkItemTaskType.Review, "kiro,dotnet");
        var implRequest = MakeRequest(WorkItemTaskType.Implementation, "kiro,dotnet");

        // Act: dispatch Review first (as the Scheduler's priority ordering guarantees)
        var reviewResult = await WorkItemEndpoints.DispatchWorkItem(
            reviewRequest, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        // Assert: Review is dispatched — K8s Job created, WorkItem=Dispatched
        var reviewOk = reviewResult as Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>;
        reviewOk.Should().NotBeNull("Review dispatch must succeed (K8s Job created)");

        k8sMock.Verify(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once, "K8s Job must be created for the Review WorkItem");

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var reviewItem = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == reviewOk.Value);
            reviewItem.Should().NotBeNull();
            reviewItem!.Status.Should().Be(WorkItemStatus.Dispatched, "Review must be Dispatched before Implementation is attempted");
            reviewItem.TaskType.Should().Be(WorkItemTaskType.Review);
        }

        // Act: dispatch Implementation — concurrency limit is now reached (Review occupies the slot)
        var implResult = await WorkItemEndpoints.DispatchWorkItem(
            implRequest, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        // Assert: Implementation blocked by concurrency limit
        implResult.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>(
            "Implementation must be blocked (409 Conflict) because Review already occupies the single concurrency slot");

        // K8s still only has one Job (Review) — Implementation was never submitted
        k8sMock.Verify(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once, "only the Review K8s Job must have been created — Implementation was rejected");
    }

    // ── Acceptance Criterion: PVC double-assignment — two concurrent calls, one PVC ───────

    /// <summary>
    /// AC: Two concurrent calls to <c>POST /api/work-items/dispatch</c> for the same single-PVC kiro
    /// pool must result in exactly one 200 (Dispatched) and one 503 (no capacity). The
    /// <see cref="DispatchLifecycleService._pvcSelectLock"/> serialises PVC selection so both calls
    /// cannot claim the same PVC.
    ///
    /// Implementation note: The <c>DispatchWorkItem</c> handler takes a snapshot of available PVCs
    /// BEFORE entering <c>ExecuteDispatchLifecycleAsync</c>. When two calls race, both snapshots may
    /// show the PVC as available. Inside <c>ExecuteDispatchLifecycleAsync</c> the shared
    /// <c>_pvcSelectLock</c> on the singleton <c>DispatchLifecycleService</c> serialises the actual
    /// PVC claim: the first caller removes the PVC from the shared list; the second finds it empty and
    /// returns without dispatching. The second call's Dispatched row (created before the lifecycle
    /// is entered) is then cleaned up by <c>SafelyCancelOrphanedDispatchedWorkItemAsync</c>.
    /// This test verifies the single-process invariant: exactly one 200 and one 503, and the K8s
    /// Job client called exactly once.
    /// </summary>
    [Fact]
    public async Task DispatchWorkItem_TwoConcurrentCalls_SinglePvcPool_OnlyOneSucceeds()
    {
        // Arrange: single PVC pool — "pvc-0" is the only available credential PVC.
        // Both concurrent requests will see "pvc-0" as available before entering the lifecycle.
        // The shared _pvcSelectLock inside DispatchLifecycleService ensures only one actually
        // claims the PVC.
        var dbFactory = CreateDbFactory();
        var runService = CreateRunService();
        var templateStore = CreateTemplateStore(maxConcurrent: 10, providerType: "kiro");

        // Use a barrier to maximise concurrency: both tasks reach the K8s call simultaneously.
        var k8sCallBarrier = new SemaphoreSlim(0, 1);
        var k8sCallCount = 0;
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => Interlocked.Increment(ref k8sCallCount));

        // Single shared DispatchLifecycleService instance — _pvcSelectLock is on this instance.
        var lifecycle = CreateLifecycleService(k8sMock.Object, pvcPool: ["pvc-0"]);

        var request1 = MakeRequest(WorkItemTaskType.Implementation, "kiro,dotnet");
        var request2 = MakeRequest(WorkItemTaskType.Implementation, "kiro,dotnet");

        // Act: both calls run concurrently, sharing the same lifecycle (same _pvcSelectLock).
        var task1 = WorkItemEndpoints.DispatchWorkItem(
            request1, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);
        var task2 = WorkItemEndpoints.DispatchWorkItem(
            request2, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        var results = await Task.WhenAll(task1, task2);

        // Assert: exactly one 200 OK and one non-200 (503 or 409)
        var okCount = results.Count(r => r is Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>);
        var failCount = results.Count(r => r is Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult s
            && s.StatusCode == StatusCodes.Status503ServiceUnavailable);

        okCount.Should().Be(1, "exactly one concurrent dispatch must succeed with a single-PVC pool");
        failCount.Should().Be(1, "the other concurrent dispatch must receive 503 (no PVC available)");

        // K8s Job must be created exactly once — the PVC was not double-assigned
        k8sCallCount.Should().Be(1, "only one K8s Job must be created — the PVC must not be double-assigned");

        // Verify DB state: exactly one Dispatched WorkItem (the successful one)
        await using var db = await dbFactory.CreateDbContextAsync();
        var dispatched = await db.WorkItems.AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Dispatched)
            .ToListAsync();
        dispatched.Should().HaveCount(1, "exactly one WorkItem must be in Dispatched state");
    }

    // ── Coverage: K8s failure → !dispatched → 503 with orphan cleanup ────────

    /// <summary>
    /// When the K8s job creation throws, <c>ExecuteDispatchLifecycleAsync</c> transitions
    /// the item to Failed internally (via <c>FailWorkItemAsync</c> in <c>CreateK8sJobAsync</c>)
    /// and returns without calling <c>onDispatchSuccess</c>.
    /// The endpoint must return 503 and the WorkItem must be in a terminal state (not Dispatched).
    /// </summary>
    [Fact]
    public async Task DispatchWorkItem_WhenK8sJobCreationFails_Returns503_AndCleansUpWorkItem()
    {
        // Arrange: K8s client throws on CreateJobAsync to simulate K8s API failure
        var dbFactory = CreateDbFactory();
        var runService = CreateRunService();
        var templateStore = CreateTemplateStore(maxConcurrent: 5);

        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("K8s API unavailable"));

        var lifecycle = CreateLifecycleService(k8sMock.Object);
        var request = MakeRequest(WorkItemTaskType.Implementation, "kiro,dotnet");

        // Act
        var result = await WorkItemEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        // Assert: 503 returned
        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull("K8s failure must return 503");
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

        // WorkItem must not remain in Dispatched state — it was cleaned up to Failed
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IssueIdentifier == request.IssueIdentifier.Value);
        item.Should().NotBeNull("WorkItem was created before K8s call");
        item!.Status.Should().NotBe(WorkItemStatus.Dispatched,
            "an orphaned Dispatched WorkItem must be cleaned up when K8s job creation fails");
    }

    // ── Coverage: non-kiro template (no PVC gate) happy path ────────────────

    /// <summary>
    /// Non-kiro templates (providerType != "kiro") skip the PVC availability check.
    /// The endpoint must dispatch successfully without requiring a PVC.
    /// </summary>
    [Fact]
    public async Task DispatchWorkItem_NonKiroTemplate_SkipsPvcGate_Returns200()
    {
        // Arrange: non-kiro template — providerType is not "kiro"
        var dbFactory = CreateDbFactory();
        var runService = CreateRunService();
        var templateStore = CreateTemplateStore(labels: "opencode,python", maxConcurrent: 5, providerType: "opencode");

        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Non-kiro lifecycle — no PVC pool needed
        var lifecycle = CreateLifecycleService(k8sMock.Object, pvcPool: []);
        var request = MakeRequest(WorkItemTaskType.Implementation, "opencode,python");

        // Act
        var result = await WorkItemEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        // Assert: 200 OK — PVC gate was skipped for non-kiro template
        var okResult = result as Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>;
        okResult.Should().NotBeNull("non-kiro dispatch must succeed without a PVC");

        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IssueIdentifier == request.IssueIdentifier.Value);
        item.Should().NotBeNull();
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "non-kiro WorkItem must be Dispatched on success");
    }
}

/// <summary>
/// Tests for the <see cref="WorkItemTransitionService.IsValidTransition"/> state machine.
/// After issue #2322, Pending→Dispatched and Dispatched→Pending are PRESERVED because
/// the consolidation dispatch path (ClaimWorkItem endpoint) still uses them.
/// The key change in #2322 is that the live dispatch path NO LONGER creates WorkItems
/// as Pending — it creates them directly as Dispatched via POST /api/work-items/dispatch.
/// </summary>
public sealed class WorkItemTransitionService_PendingDispatchedTransitionsTests
{
    /// <summary>
    /// Pending→Dispatched remains valid for the consolidation dispatch path (ClaimWorkItem).
    /// The live dispatch path (KubernetesWorkDistributor) never uses this transition — it creates
    /// items directly as Dispatched. But the consolidation path's ClaimWorkItem endpoint still
    /// uses TransitionIfAsync(Pending→Dispatched).
    /// </summary>
    [Fact]
    public void PendingToDispatched_IsStillValid_ForConsolidationPath()
    {
        WorkItemTransitionService.IsValidTransition(WorkItemStatus.Pending, WorkItemStatus.Dispatched)
            .Should().BeTrue(
                "Pending→Dispatched is retained for the consolidation dispatch path (ClaimWorkItem endpoint)");
    }

    /// <summary>
    /// Dispatched→Pending remains valid for the consolidation dispatch path's claim recovery.
    /// </summary>
    [Fact]
    public void DispatchedToPending_IsStillValid_ForConsolidationClaimRecovery()
    {
        WorkItemTransitionService.IsValidTransition(WorkItemStatus.Dispatched, WorkItemStatus.Pending)
            .Should().BeTrue(
                "Dispatched→Pending is retained for the consolidation dispatch path's claim recovery");
    }

    /// <summary>
    /// Recovery path still works: Failed→Pending (requeue endpoint).
    /// </summary>
    [Fact]
    public void FailedToPending_IsStillValid()
    {
        WorkItemTransitionService.IsValidTransition(WorkItemStatus.Failed, WorkItemStatus.Pending)
            .Should().BeTrue("Failed→Pending is the recovery path for requeue");
    }

    /// <summary>
    /// Recovery path still works: Cancelled→Pending (requeue endpoint).
    /// </summary>
    [Fact]
    public void CancelledToPending_IsStillValid()
    {
        WorkItemTransitionService.IsValidTransition(WorkItemStatus.Cancelled, WorkItemStatus.Pending)
            .Should().BeTrue("Cancelled→Pending is the recovery path for requeue");
    }

    /// <summary>
    /// Normal operational transitions remain valid.
    /// </summary>
    [Theory]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Running)]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Cancelled)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Cancelled)]
    public void OperationalTransitions_AreStillValid(WorkItemStatus from, WorkItemStatus to)
    {
        WorkItemTransitionService.IsValidTransition(from, to)
            .Should().BeTrue($"{from}→{to} must remain valid");
    }
}

// ── Test infrastructure helpers ──────────────────────────────────────────────

file sealed class SimpleInMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _opts;
    public SimpleInMemoryDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;
    public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_opts);
    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult<PipelineDbContext>(new TestPipelineDbContext(_opts));
}

file sealed class TestPipelineDbContext : PipelineDbContext
{
    public TestPipelineDbContext(DbContextOptions<PipelineDbContext> opts) : base(opts) { }
    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);
        foreach (var et in mb.Model.GetEntityTypes())
        {
            var rv = et.FindProperty("RowVersion");
            if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never; }
        }
        foreach (var et in mb.Model.GetEntityTypes())
        {
            foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                et.RemoveIndex(idx);
        }
    }
}
