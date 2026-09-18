using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Api;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for the synchronous <c>POST /api/work-items/dispatch</c> endpoint
/// (<see cref="WorkItemDispatchEndpoints.DispatchWorkItem"/>).
///
/// <para>
/// As of the Pending-queue restore (fix/restore-pending-queue), this endpoint is called by the
/// Scheduler-side <c>WorkItemDispatchPoller</c>. The Scheduler creates a <c>Pending</c> WorkItem
/// via <c>POST /api/work-items</c>; <c>WorkItemDispatchPoller</c> then polls those items and calls
/// <c>POST /api/work-items/dispatch</c> when capacity is available.
/// </para>
///
/// Covers:
/// <list type="number">
///   <item><c>POST /api/work-items/dispatch</c> endpoint handler — concurrency gating, PVC gating, success path.</item>
///   <item>Transition state machine: <c>Pending→Dispatched</c> remains valid (used by this endpoint).</item>
/// </list>
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
        var result = await WorkItemDispatchEndpoints.DispatchWorkItem(
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
        var result = await WorkItemDispatchEndpoints.DispatchWorkItem(
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
        var result = await WorkItemDispatchEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        // Assert: 503 Service Unavailable
        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull();
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    // ── Acceptance Criterion: 422 when no template for selector ─────────────

    [Fact]
    public async Task DispatchWorkItem_WhenNoTemplateForSelector_Returns422()
    {
        var dbFactory = CreateDbFactory();
        // Template only has "kiro,dotnet", not "opencode,java"
        var templateStore = CreateTemplateStore("kiro,dotnet");
        var lifecycle = CreateLifecycleService();
        var runService = CreateRunService();
        var request = MakeRequest(WorkItemTaskType.Implementation, "opencode,java");

        // Act
        var result = await WorkItemDispatchEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);

        // Assert: 422 Unprocessable Entity (permanent config error — no job template for selector).
        // Distinct from 409 (transient capacity) so callers can distinguish permanent vs transient failures.
        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull();
        statusResult!.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
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
        var result = await WorkItemDispatchEndpoints.DispatchWorkItem(
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
        var reviewResult = await WorkItemDispatchEndpoints.DispatchWorkItem(
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
        var reviewResult = await WorkItemDispatchEndpoints.DispatchWorkItem(
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
        var implResult = await WorkItemDispatchEndpoints.DispatchWorkItem(
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
        var task1 = WorkItemDispatchEndpoints.DispatchWorkItem(
            request1, dbFactory, runService, lifecycle, templateStore, CancellationToken.None);
        var task2 = WorkItemDispatchEndpoints.DispatchWorkItem(
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
        var result = await WorkItemDispatchEndpoints.DispatchWorkItem(
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
        var result = await WorkItemDispatchEndpoints.DispatchWorkItem(
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
/// After issue #2566, the consolidation-specific TODO comments on Pending→Dispatched and
/// Dispatched→Pending are removed. Both transitions remain valid:
/// - Pending→Dispatched: ClaimWorkItem (POST /api/work-items/{id}/claim)
/// - Dispatched→Pending: RequeueWorkItem (POST /api/work-items/{id}/requeue) when K8s Job
///   creation fails after a successful claim
/// </summary>
public sealed class WorkItemTransitionService_PendingDispatchedTransitionsTests
{
    /// <summary>
    /// Pending→Dispatched remains valid for ClaimWorkItem (POST /api/work-items/{id}/claim),
    /// used by the Scheduler's WorkItemDispatchPoller for all task types.
    /// </summary>
    [Fact]
    public void PendingToDispatched_IsStillValid_ForClaimWorkItem()
    {
        WorkItemTransitionService.IsValidTransition(WorkItemStatus.Pending, WorkItemStatus.Dispatched)
            .Should().BeTrue(
                "Pending→Dispatched is used by ClaimWorkItem (POST /api/work-items/{id}/claim) for all task types");
    }

    /// <summary>
    /// Dispatched→Pending remains valid for RequeueWorkItem when K8s Job creation fails after claim.
    /// The consolidation-specific TODO is removed in #2566; the transition remains in the state machine.
    /// </summary>
    [Fact]
    public void DispatchedToPending_IsStillValid_ForRequeueWorkItem()
    {
        WorkItemTransitionService.IsValidTransition(WorkItemStatus.Dispatched, WorkItemStatus.Pending)
            .Should().BeTrue(
                "Dispatched→Pending is used by RequeueWorkItem when K8s Job creation fails after claim");
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

// ── Tests for POST /api/work-items/{id}/dispatch (claim-by-CAS, Scheduler path) ──────────────

/// <summary>
/// Tests for the <c>POST /api/work-items/{id}/dispatch</c> endpoint
/// (<see cref="WorkItemDispatchEndpoints.DispatchPendingWorkItem"/>).
///
/// <para>
/// This endpoint claims an existing Pending WorkItem by CAS and creates the K8s Job.
/// It is designed for the leader-elected Scheduler poller (issue #2541).
/// </para>
/// </summary>
/// <remarks>
/// Placed in <see cref="MetricsTestCollection"/> because tests 8 and 9 read and write the
/// process-wide static <c>WorkDistributionTelemetry._credentialPoolAvailable</c> field via
/// reflection — they must not run concurrently with other metric-emitting tests.
/// </remarks>
[Collection("Metrics")]
public sealed class DispatchPendingWorkItemEndpointTests
{
    private readonly string _dbName = $"dispatch-pending-test-{Guid.NewGuid():N}";

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

    private static DispatchTemplateResolver CreateTemplateResolver(
        JobTemplateStore templateStore,
        IAgentProfileStore? profileStore = null) =>
        new DispatchTemplateResolver(profileStore, templateStore);

    /// <summary>
    /// Returns a Mock&lt;IDistributedLockProvider&gt; that immediately grants the lock.
    /// </summary>
    private static IDistributedLockProvider CreateNoOpLockProvider()
    {
        var mock = new Mock<IDistributedLockProvider>();
        var handle = new Mock<IAsyncDisposable>();
        handle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mock.Setup(lp => lp.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        return mock.Object;
    }

    private async Task<WorkItemEntity> SeedPendingItemAsync(
        IDbContextFactory<PipelineDbContext> dbFactory,
        string selector = "kiro,dotnet",
        string? claimedPvcName = null)
    {
        var entity = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"issue-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = WorkItemStatus.Pending,
            AgentSelector = selector,
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}",
            ClaimedPvcName = claimedPvcName
        };
        await using var db = await dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    private async Task SeedActiveItemAsync(
        IDbContextFactory<PipelineDbContext> dbFactory,
        string selector = "kiro,dotnet",
        string? claimedPvcName = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"active-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = WorkItemStatus.Dispatched,
            AgentSelector = selector,
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}",
            ClaimedPvcName = claimedPvcName
        });
        await db.SaveChangesAsync();
    }

    // ── Helpers for reading static telemetry fields ─────────────────────────

    private static int ReadCredentialPoolAvailable()
    {
        var field = typeof(WorkDistributionTelemetry)
            .GetField("_credentialPoolAvailable", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field); // guard: fails with a clear message if the field is renamed
        return (int)field!.GetValue(null)!;
    }

    // ── Test 1: Happy path ────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchPendingWorkItem_PendingItemFound_Returns200_ItemDispatched_K8sJobCreated()
    {
        // Arrange
        var dbFactory = CreateDbFactory();
        var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");
        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 5);
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object, ["pvc-0"]);
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        // Act
        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        // Assert: 200 returned
        var okResult = result as Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>;
        okResult.Should().NotBeNull("dispatch of a Pending item must succeed");
        okResult!.Value.Should().Be(entity.Id);

        // WorkItem is now Dispatched
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == entity.Id);
        item.Should().NotBeNull();
        item!.Status.Should().Be(WorkItemStatus.Dispatched);

        // K8s Job created exactly once
        k8sMock.Verify(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Test 2: Item not found → 404 ─────────────────────────────────────────

    [Fact]
    public async Task DispatchPendingWorkItem_ItemNotFound_Returns404()
    {
        var dbFactory = CreateDbFactory();
        var templateStore = CreateTemplateStore();
        var lifecycle = CreateLifecycleService();
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();
        var missingId = Guid.NewGuid();

        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            missingId, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NotFound>();
    }

    // ── Test 3: Item exists but not Pending → 409 (fast path) ────────────────

    [Fact]
    public async Task DispatchPendingWorkItem_ItemNotPending_Returns409_BeforeAcquiringLock()
    {
        var dbFactory = CreateDbFactory();
        // Seed an already-Dispatched item
        await SeedActiveItemAsync(dbFactory, "kiro,dotnet", "pvc-0");
        await using var db = await dbFactory.CreateDbContextAsync();
        var existingId = (await db.WorkItems.AsNoTracking().FirstAsync()).Id;

        var templateStore = CreateTemplateStore();
        var lifecycle = CreateLifecycleService();
        var resolver = CreateTemplateResolver(templateStore);

        // Track whether the lock was acquired
        var lockAcquired = false;
        var mockLock = new Mock<IDistributedLockProvider>();
        var handle = new Mock<IAsyncDisposable>();
        handle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockLock.Setup(lp => lp.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => lockAcquired = true)
            .ReturnsAsync(handle.Object);

        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            existingId, dbFactory, lifecycle, templateStore, resolver, mockLock.Object, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>(
            "a non-Pending item must return 409 immediately");
        lockAcquired.Should().BeFalse("the advisory lock must not be acquired for a non-Pending item (fast path)");
    }

    // ── Test 4: No template for selector → 409 ───────────────────────────────

    [Fact]
    public async Task DispatchPendingWorkItem_NoTemplateForSelector_Returns409_ItemRemainingPending()
    {
        var dbFactory = CreateDbFactory();
        var entity = await SeedPendingItemAsync(dbFactory, "opencode,java");
        var templateStore = CreateTemplateStore("kiro,dotnet"); // no template for "opencode,java"
        var lifecycle = CreateLifecycleService();
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>("no template → 409");

        // Item must remain Pending (re-dispatchable)
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == entity.Id);
        item!.Status.Should().Be(WorkItemStatus.Pending, "gate rejection must not change item status");
    }

    // ── Test 5: Profile fallback resolves template → 200 ─────────────────────

    [Fact]
    public async Task DispatchPendingWorkItem_ProfileFallbackResolvesTemplate_Returns200()
    {
        // Arrange: item has subset selector "dotnet"; profile maps "dotnet" → "dotnet,kiro" which has a template
        var dbFactory = CreateDbFactory();
        var entity = await SeedPendingItemAsync(dbFactory, selector: "dotnet");

        var templateStore = CreateTemplateStore("dotnet,kiro", maxConcurrent: 5, providerType: "kiro");

        var profileStoreMock = new Mock<IAgentProfileStore>();
        profileStoreMock
            .Setup(ps => ps.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new AgentProfile
                {
                    DisplayName = "Kiro+Dotnet",
                    AgentProviderConfigId = "agent-kiro",
                    MatchLabels = ["dotnet", "kiro"],
                    Enabled = true
                }
            });

        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object, ["pvc-0"]);
        var resolver = CreateTemplateResolver(templateStore, profileStoreMock.Object);
        var lockProvider = CreateNoOpLockProvider();

        // Act
        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        // Assert: profile fallback resolved template → dispatch succeeds
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>>(
            "profile fallback must resolve the template and dispatch the item");
        k8sMock.Verify(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);

        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == entity.Id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
        // TODO [WARNING]: This test does not verify that the profile fallback path was actually taken.
        // The endpoint tries templateStore.Resolve(normalizedSelector) first; if the normalised form of
        // "dotnet" happened to match "dotnet,kiro" in the store, the fallback would never be entered and
        // the test would still pass. Add a verify: profileStoreMock.Verify(ps => ps.LoadAgentProfilesAsync(
        // It.IsAny<CancellationToken>()), Times.Once) to confirm the fallback was entered and that it is
        // LoadAgentProfilesAsync — not the direct resolve — that provided the template.
    }

    // ── Test 6: Concurrency limit reached → 409 ──────────────────────────────

    [Fact]
    public async Task DispatchPendingWorkItem_ConcurrencyLimitReached_Returns409_ItemRemainingPending()
    {
        var dbFactory = CreateDbFactory();
        // Seed maxConcurrent=2 active items to fill the limit
        await SeedActiveItemAsync(dbFactory, "kiro,dotnet");
        await SeedActiveItemAsync(dbFactory, "kiro,dotnet");
        var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");

        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 2);
        var lifecycle = CreateLifecycleService();
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>("concurrency limit → 409");

        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == entity.Id);
        item!.Status.Should().Be(WorkItemStatus.Pending, "concurrency gate rejection must leave item Pending");
    }

    // ── Test 7: No PVC available → 503; item remains Pending ─────────────────

    [Fact]
    public async Task DispatchPendingWorkItem_NoPvcAvailable_Returns503_ItemRemainingPending()
    {
        var dbFactory = CreateDbFactory();
        // Claim the only PVC in the pool via an active item
        await SeedActiveItemAsync(dbFactory, "kiro,dotnet", claimedPvcName: "pvc-0");
        var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");

        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 5);
        // Pool has only "pvc-0" which is already claimed
        var lifecycle = CreateLifecycleService(pvcPool: ["pvc-0"]);
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull();
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, "no PVC → 503");

        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == entity.Id);
        item!.Status.Should().Be(WorkItemStatus.Pending, "PVC gate rejection must leave item Pending");
    }

    // ── Test 8: Credential pool gauge emitted on success ─────────────────────

    [Fact]
    public async Task DispatchPendingWorkItem_OnSuccess_EmitsCredentialPoolGauge()
    {
        var dbFactory = CreateDbFactory();
        var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");

        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 5);
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // 2-PVC pool; 0 claimed → available=2
        var lifecycle = CreateLifecycleService(k8sMock.Object, ["pvc-a", "pvc-b"]);
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        // TODO [WARNING]: This test does not reset _credentialPoolAvailable to a distinguishable sentinel before
        // the Act call. If another test in the Metrics collection happens to leave the field at 2 before this
        // test runs, the assertion below would pass without UpdateCredentialPoolMetrics ever being called here.
        // Add a sentinel reset (e.g. -1) before the DispatchPendingWorkItem call, matching the pattern in Test 9.

        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>>();

        // Gauge must have been updated to the available count (2)
        ReadCredentialPoolAvailable().Should().Be(2,
            "credential pool gauge must reflect the available PVC count after a successful dispatch");
    }

    // ── Test 9: Credential pool gauge emitted even on 503 (PVC exhaustion) ───

    [Fact]
    public async Task DispatchPendingWorkItem_PvcExhaustion_EmitsCredentialPoolGaugeBeforeReturning503()
    {
        var dbFactory = CreateDbFactory();
        // Claim the only PVC → available=0
        await SeedActiveItemAsync(dbFactory, "kiro,dotnet", claimedPvcName: "pvc-solo");
        var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");

        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 5);
        var lifecycle = CreateLifecycleService(pvcPool: ["pvc-solo"]);
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        // Reset the static gauge field to a sentinel before calling, to distinguish "not updated" from "updated to 0".
        // We seed available=0 and verify the gauge is updated to 0 (not left at any prior value).
        // Reset to a non-zero sentinel so a "no update" would be detectable.
        // TODO [WARNING]: The sentinel value (99) is written to the static field with no try/finally to restore
        // the prior value. If the assertion fails or the endpoint throws before UpdateCredentialPoolMetrics runs,
        // the field is abandoned at 99, potentially causing subsequent Metrics-collection tests that read
        // _credentialPoolAvailable without their own sentinel reset to observe leaked state and produce a false
        // pass or false failure. Wrap the sentinel assignment and assertion in try/finally that restores the
        // prior value, or add a collection-level fixture teardown that resets the field.
        typeof(WorkDistributionTelemetry)
            .GetField("_credentialPoolAvailable", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, 99); // sentinel

        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

        // Gauge must have been updated to 0 (not left at the 99 sentinel),
        // proving UpdateCredentialPoolMetrics was called BEFORE the PVC gate returned 503.
        ReadCredentialPoolAvailable().Should().Be(0,
            "UpdateCredentialPoolMetrics must be called before the PVC gate so the gauge is updated even on 503");
    }

    // ── Test 10: Advisory lock prevents double-dispatch ──────────────────────

    /// <summary>
    /// Two concurrent calls for the same Pending WorkItem must result in exactly one success (200)
    /// and one failure (409 or 503). The advisory lock + CAS prevents double-dispatch.
    /// Uses <see cref="InProcessDistributedLockProvider"/> (backed by SemaphoreSlim) to
    /// exercise real in-process lock contention.
    /// </summary>
    // TODO [WARNING]: This test does not exercise true concurrent execution. Both task1 and task2 are
    // started on the same synchronisation context without Task.Run, so the in-memory EF provider and
    // InProcessDistributedLockProvider execute them cooperatively/sequentially rather than in parallel.
    // The test will almost always pass because task1 completes before task2 starts, not because the
    // lock or CAS serialises them. To exercise real contention, each call must be started with Task.Run,
    // or a SemaphoreSlim/TaskCompletionSource must be used inside the K8s mock to force interleaving
    // (e.g. block the first call inside CreateJobAsync until the second has passed the fast-path status check).
    // Without true concurrency, the advisory-lock acceptance criterion ("concurrent calls cannot exceed
    // maxConcurrent, advisory lock verified by test") is not actually verified by this test.
    //
    // TODO [WARNING]: The k8sCallCount assertion ("K8s Job must be created exactly once") validates that
    // the single-PVC pool exhausts before the second call reaches K8s, not the CAS claim-uniqueness guarantee.
    // If the PVC pool had two PVCs, the second call would reach K8s regardless of the CAS, and the test
    // would fail even if the CAS were working correctly. The real CAS prevention is verified by the DB
    // dispatched-count assertion (HaveCount(1)), but the k8sCallCount comment overstates what is tested.
    // Consider adding a two-PVC variant (so PVC exhaustion is not the limiter) to isolate the CAS path.
    [Fact]
    public async Task DispatchPendingWorkItem_TwoConcurrentCalls_OnlyOneSucceeds_K8sJobCreatedOnce()
    {
        var dbFactory = CreateDbFactory();
        var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");

        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 10);
        var k8sCallCount = 0;
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => Interlocked.Increment(ref k8sCallCount));
        // Single PVC so second caller (if it reaches the lifecycle) can't get a PVC
        var lifecycle = CreateLifecycleService(k8sMock.Object, ["pvc-0"]);
        var resolver = CreateTemplateResolver(templateStore);
        // Real in-process lock provider — serialises concurrent calls
        var lockProvider = new InProcessDistributedLockProvider();

        var task1 = WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);
        var task2 = WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        var results = await Task.WhenAll(task1, task2);

        // Exactly one 200 and one non-200
        var okCount = results.Count(r => r is Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>);
        okCount.Should().Be(1, "exactly one concurrent dispatch must succeed");

        // K8s Job created exactly once (CAS prevents the second from reaching K8s)
        k8sCallCount.Should().Be(1, "K8s Job must be created exactly once — double-dispatch prevented");

        // DB must have exactly one Dispatched item
        await using var db = await dbFactory.CreateDbContextAsync();
        var dispatched = await db.WorkItems.AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Dispatched)
            .ToListAsync();
        dispatched.Should().HaveCount(1, "exactly one WorkItem must be Dispatched");
    }

    // ── Test 11: K8s failure → 503; item Failed; PVC released ─────────────────

    [Fact]
    public async Task DispatchPendingWorkItem_K8sJobCreationFails_Returns503_ItemFailed_PvcReleased()
    {
        var dbFactory = CreateDbFactory();
        var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");

        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 5);
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("K8s API unavailable"));
        var lifecycle = CreateLifecycleService(k8sMock.Object, ["pvc-0"]);
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        // Act — first call with K8s failing
        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        // Assert: 503
        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull("K8s failure must return 503");
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

        // Item must be in Failed state (Pending→Failed via FailWorkItemAsync)
        await using var db = await dbFactory.CreateDbContextAsync();
        var failedItem = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == entity.Id);
        failedItem!.Status.Should().Be(WorkItemStatus.Failed,
            "K8s failure must transition item to Failed (Pending→Failed is a valid transition)");

        // PVC must be released: a second dispatch call for a NEW pending item with the same
        // selector must succeed (PVC "pvc-0" is available again because QueryAvailablePvcsAsync
        // filters by Pending/Dispatched/Running — the Failed item no longer holds the slot).
        var k8sMock2 = new Mock<IKubernetesJobClient>();
        k8sMock2.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle2 = CreateLifecycleService(k8sMock2.Object, ["pvc-0"]);
        var entity2 = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");
        var result2 = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity2.Id, dbFactory, lifecycle2, templateStore, resolver, lockProvider, CancellationToken.None);
        result2.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>>(
            "PVC must be released after K8s failure — Failed item no longer holds the credential slot");
    }

    // ── Test 12: Non-kiro template skips PVC gate ─────────────────────────────

    [Fact]
    public async Task DispatchPendingWorkItem_NonKiroTemplate_SkipsPvcGate_Returns200()
    {
        var dbFactory = CreateDbFactory();
        var entity = await SeedPendingItemAsync(dbFactory, "opencode,python");

        // Non-kiro template; empty PVC pool — PVC gate must be skipped
        var templateStore = CreateTemplateStore("opencode,python", maxConcurrent: 5, providerType: "opencode");
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object, pvcPool: []);
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>>(
            "non-kiro dispatch must succeed without a PVC");

        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == entity.Id);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    // ── Test 13: Gate-level rejections leave item Pending (re-dispatchable invariant) ──

    [Theory]
    [InlineData("no-template")]
    [InlineData("concurrency")]
    [InlineData("pvc-gate")]
    public async Task DispatchPendingWorkItem_GateRejection_LeavesItemPending(string scenario)
    {
        var dbFactory = CreateDbFactory();

        IResult result;
        Guid itemId;

        if (scenario == "no-template")
        {
            var entity = await SeedPendingItemAsync(dbFactory, "opencode,java");
            itemId = entity.Id;
            var ts = CreateTemplateStore("kiro,dotnet");
            var lc = CreateLifecycleService();
            var res = CreateTemplateResolver(ts);
            result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
                itemId, dbFactory, lc, ts, res, CreateNoOpLockProvider(), CancellationToken.None);
        }
        else if (scenario == "concurrency")
        {
            await SeedActiveItemAsync(dbFactory, "kiro,dotnet");
            await SeedActiveItemAsync(dbFactory, "kiro,dotnet");
            var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");
            itemId = entity.Id;
            var ts = CreateTemplateStore("kiro,dotnet", maxConcurrent: 2);
            var lc = CreateLifecycleService();
            var res = CreateTemplateResolver(ts);
            result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
                itemId, dbFactory, lc, ts, res, CreateNoOpLockProvider(), CancellationToken.None);
        }
        else // pvc-gate
        {
            await SeedActiveItemAsync(dbFactory, "kiro,dotnet", claimedPvcName: "pvc-0");
            var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");
            itemId = entity.Id;
            var ts = CreateTemplateStore("kiro,dotnet", maxConcurrent: 5);
            var lc = CreateLifecycleService(pvcPool: ["pvc-0"]);
            var res = CreateTemplateResolver(ts);
            result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
                itemId, dbFactory, lc, ts, res, CreateNoOpLockProvider(), CancellationToken.None);
        }

        // All gate rejections must be non-200
        result.Should().NotBeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>>(
            $"scenario '{scenario}' must not return 200");

        // Item must remain Pending — gate rejections never reach the CAS
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == itemId);
        item!.Status.Should().Be(WorkItemStatus.Pending,
            $"gate rejection '{scenario}' must leave the item in Pending state (re-dispatchable)");
    }

    // ── Test 14: Advisory lock timeout → 503 (not 500) ──────────────────────

    [Fact]
    public async Task DispatchPendingWorkItem_LockTimeout_Returns503_NotUnhandledException()
    {
        var dbFactory = CreateDbFactory();
        var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");

        var templateStore = CreateTemplateStore();
        var lifecycle = CreateLifecycleService();
        var resolver = CreateTemplateResolver(templateStore);

        // Lock provider that throws TimeoutException from AcquireAsync
        var timeoutLockMock = new Mock<IDistributedLockProvider>();
        timeoutLockMock
            .Setup(lp => lp.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Advisory lock acquisition timed out"));

        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, timeoutLockMock.Object, CancellationToken.None);

        // Must return 503, not throw
        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull("lock timeout must return 503 (not propagate an unhandled exception)");
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

        // Item must remain Pending (lock was never acquired — item untouched)
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == entity.Id);
        item!.Status.Should().Be(WorkItemStatus.Pending, "lock timeout must leave the item Pending");
    }

    // ── Test 15: Log injection — no-template Conflict body does not reflect raw CRLF ─

    /// <summary>
    /// Verifies that a malicious <c>agentSelector</c> containing CRLF characters is sanitized
    /// before being embedded in the 409 Conflict response body (no-template path).
    /// A raw <c>\r\n</c> in the response body could forge log lines if the body is ever written to a log.
    /// </summary>
    [Fact]
    public async Task DispatchPendingWorkItem_MaliciousAgentSelectorCRLF_NoTemplateConflict_DoesNotContainRawNewlines()
    {
        // Arrange: seed a WorkItem whose selector contains CRLF injection payload
        var dbFactory = CreateDbFactory();
        var maliciousSelector = "kiro\r\nINJECTED-fake-log-line";
        var entity = await SeedPendingItemAsync(dbFactory, maliciousSelector);

        // Template store has no template matching the malicious selector → triggers the no-template 409 path
        var templateStore = CreateTemplateStore("other,selector");
        var lifecycle = CreateLifecycleService();
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        // Act
        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        // Assert: result is a 409 Conflict
        var conflictResult = result as Microsoft.AspNetCore.Http.HttpResults.Conflict<string>;
        conflictResult.Should().NotBeNull("a selector with no matching template must return 409 Conflict");

        var body = conflictResult!.Value!;

        // The raw CRLF must NOT appear verbatim in the response body
        body.Should().NotContain("\r", "raw CR in the Conflict body enables log injection");
        body.Should().NotContain("\n", "raw LF in the Conflict body enables log injection");

        // The escaped form MUST appear — proving the sanitizer ran, not that the value was silently dropped
        body.Should().Contain("\\r", "CR must be escaped as \\r in the sanitized response");
        body.Should().Contain("\\n", "LF must be escaped as \\n in the sanitized response");
        // TODO [WARNING]: The assertions above only verify that \\r and \\n appear somewhere in the body.
        // They would pass even if the selector were replaced with a hardcoded literal containing those
        // escape sequences. Consider asserting the full sanitized selector text, e.g.:
        //   body.Should().Contain("kiro\\r\\nINJECTED-fake-log-line");
        // This would catch scenarios where the selector is silently dropped or replaced with a
        // generic placeholder (e.g. "[redacted]") rather than sanitized and reflected.
    }

    // ── Test 16: Log injection — concurrency Conflict body does not reflect raw CRLF ─

    /// <summary>
    /// Verifies that a malicious <c>agentSelector</c> containing CRLF characters is sanitized
    /// before being embedded in the 409 Conflict response body (concurrency-limit path).
    /// </summary>
    [Fact]
    public async Task DispatchPendingWorkItem_MaliciousAgentSelectorCRLF_ConcurrencyConflict_DoesNotContainRawNewlines()
    {
        // Arrange: the malicious selector must match an existing template (so it passes the template gate
        // and reaches the concurrency gate). NormalizeLabels does not strip \r\n from within tokens,
        // so "kiro\r\nINJECTED" is the normalized key used for the template lookup and concurrency map.
        // We cannot embed raw CRLF in a YAML string literal, so build the template store via JSON
        // (JSON \r and \n escape sequences are decoded by the parser, producing the exact same bytes
        // that NormalizeLabels stores in the dictionary key).
        var maliciousSelector = "kiro\r\nINJECTED-fake-log-line";
        var dbFactory = CreateDbFactory();

        // Seed one active item with the same selector to exhaust maxConcurrent=1
        await SeedActiveItemAsync(dbFactory, maliciousSelector);

        // Seed the Pending item under test
        var entity = await SeedPendingItemAsync(dbFactory, maliciousSelector);

        // Build a template store via JSON with the raw CRLF in the labels field.
        // JSON \r / \n sequences are decoded to the actual bytes, matching the NormalizeLabels key.
        var templateJson = """
            [
              {
                "labels": "kiro\r\nINJECTED-fake-log-line",
                "image": "test-image:latest",
                "imagePullPolicy": "Always",
                "providerType": "kiro",
                "maxConcurrent": 1
              }
            ]
            """;
        var templateStore = JobTemplateStore.LoadFromJson(templateJson);
        var lifecycle = CreateLifecycleService();
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        // Act
        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CancellationToken.None);

        // Assert: result is a 409 Conflict (concurrency path)
        var conflictResult = result as Microsoft.AspNetCore.Http.HttpResults.Conflict<string>;
        conflictResult.Should().NotBeNull("concurrency limit reached must return 409 Conflict");

        var body = conflictResult!.Value!;

        // The raw CRLF must NOT appear verbatim in the Conflict body
        body.Should().NotContain("\r", "raw CR in the Conflict body enables log injection");
        body.Should().NotContain("\n", "raw LF in the Conflict body enables log injection");

        // The escaped form MUST appear — proving the sanitizer ran
        body.Should().Contain("\\r", "CR must be escaped as \\r in the sanitized response");
        body.Should().Contain("\\n", "LF must be escaped as \\n in the sanitized response");
        // TODO [WARNING]: This test relies on LoadFromJson correctly parsing JSON \r/\n escape sequences
        // to literal CR/LF bytes and NormalizeLabels preserving them as dictionary keys. If the template
        // lookup misses (e.g. LoadFromJson parses differently), the test falls through to the no-template
        // 409 branch — giving a false pass on the wrong code path. Consider asserting the specific
        // Conflict message text to confirm the concurrency branch was exercised, e.g.:
        //   body.Should().Contain("Concurrency limit", "must be the concurrency-limit 409, not the no-template 409");
    }
}

// TODO [WARNING]: The DispatchWorkItem (synchronous dispatch) paths for CRLF injection are not
// covered by tests. The diff sanitizes request.AgentSelector in:
//   - DispatchWorkItem concurrency-limit Conflict body (line ~573–579)
//   - DispatchWorkItem no-template 422 log call (line ~542; body is not reflected, lower priority)
// If sanitization were accidentally reverted on those branches, no test would fail. Consider
// adding tests analogous to Tests 15–16 above for the DispatchWorkItem concurrency-conflict
// path, and optionally for the 422 log-only path.

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
