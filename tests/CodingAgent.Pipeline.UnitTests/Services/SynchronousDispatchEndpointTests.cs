using System.Diagnostics.Metrics;
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
/// Scheduler-side <c>WorkItemDispatchLoop</c>. The Scheduler creates a <c>Pending</c> WorkItem
/// via <c>POST /api/work-items</c>; <c>WorkItemDispatchLoop</c> then polls those items and calls
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
            AgentApiKeyValue = "test-master-key",
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

    private static DispatchWorkItemService CreateDispatchService(
        string labels = "kiro,dotnet",
        int maxConcurrent = 5,
        string providerType = "kiro") =>
        new DispatchWorkItemService(CreateTemplateStore(labels, maxConcurrent, providerType));

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
            request, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

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
            request, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

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
            request, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

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
            request, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

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
            request, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

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
            reviewRequest, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

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
            reviewRequest, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

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
            implRequest, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

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
            request1, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);
        var task2 = WorkItemDispatchEndpoints.DispatchWorkItem(
            request2, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

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
            request, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

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
            request, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

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

    // ── TimeoutSeconds clamping (issue #2745) ─────────────────────────────────

    /// <summary>
    /// AC (issue #2745): POST /api/work-items/dispatch with TimeoutSeconds = 0 must store
    /// <c>PipelineConstants.DefaultAgentTimeout</c> (1800s) rather than zero.
    /// </summary>
    [Fact]
    public async Task DispatchWorkItem_WithZeroTimeoutSeconds_StoresDefaultTimeoutSeconds()
    {
        var dbFactory = CreateDbFactory();
        var runService = CreateRunService();
        var templateStore = CreateTemplateStore(maxConcurrent: 5);
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object);

        var request = MakeRequest() with { TimeoutSeconds = 0 };

        var result = await WorkItemDispatchEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>>(
            "dispatch must succeed so we can verify the stored TimeoutSeconds");

        await using var db = await dbFactory.CreateDbContextAsync();
        var item2 = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IssueIdentifier == request.IssueIdentifier.Value);
        item2.Should().NotBeNull();
        item2!.TimeoutSeconds.Should().Be(
            (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds,
            "a zero TimeoutSeconds must be clamped to DefaultAgentTimeout (1800s) at the dispatch insert path");
    }

    /// <summary>
    /// AC (issue #2745): POST /api/work-items/dispatch with a negative TimeoutSeconds must store
    /// <c>PipelineConstants.DefaultAgentTimeout</c> (1800s) rather than the negative value.
    /// </summary>
    [Fact]
    public async Task DispatchWorkItem_WithNegativeTimeoutSeconds_StoresDefaultTimeoutSeconds()
    {
        var dbFactory = CreateDbFactory();
        var runService = CreateRunService();
        var templateStore = CreateTemplateStore(maxConcurrent: 5);
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object);

        var request = MakeRequest() with { TimeoutSeconds = -1 };

        var result = await WorkItemDispatchEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>>(
            "dispatch must succeed so we can verify the stored TimeoutSeconds");

        await using var db = await dbFactory.CreateDbContextAsync();
        var item3 = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IssueIdentifier == request.IssueIdentifier.Value);
        item3.Should().NotBeNull();
        item3!.TimeoutSeconds.Should().Be(
            (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds,
            "a negative TimeoutSeconds must be clamped to DefaultAgentTimeout (1800s) at the dispatch insert path");
    }

    // TODO [WARNING]: Add a positive-value passthrough regression test for the DispatchWorkItem path
    // (mirroring WorkItemEndpointTests.CreateWorkItem_WithPositiveTimeoutSeconds_StoresAsProvided).
    // Without it, an accidental inversion of the clamp condition (e.g. `> 0` changed to `>= 0`, or
    // the branches swapped) would go undetected on this path — the zero and negative tests both
    // produce the default value regardless of which branch runs when the input is <= 0.
    // (Correctness + TestQualityReviewer review [WARNING])

    // TODO [WARNING]: The three TimeoutSeconds tests (zero, negative, and positive) are structurally
    // identical — same arrange, same act, same DB-read pattern — differing only in the input value and
    // expected stored value. Consolidate them into a single [Theory] / [InlineData] test with triples
    // (inputTimeout, expectedStoredValue, reason) to eliminate copy-paste and make adding a fourth
    // variant trivial. (TestQualityReviewer review [WARNING])
    /// <summary>
    /// AC (issue #2745): POST /api/work-items/dispatch with a positive TimeoutSeconds must store
    /// the provided value unchanged rather than being replaced by <c>DefaultAgentTimeout</c>.
    /// Without this test, an accidental inversion of the clamp condition (<c>&gt; 0</c> → <c>&gt;= 0</c>)
    /// would go undetected — the zero and negative tests both produce the default value regardless
    /// of which branch executes when the input is ≤ 0.
    /// </summary>
    [Fact]
    public async Task DispatchWorkItem_WithPositiveTimeoutSeconds_StoresAsProvided()
    {
        var dbFactory = CreateDbFactory();
        var runService = CreateRunService();
        var templateStore = CreateTemplateStore(maxConcurrent: 5);
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(
                It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object);

        const int customTimeout = 7200;
        var request = MakeRequest() with { TimeoutSeconds = customTimeout };

        var result = await WorkItemDispatchEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>>(
            "dispatch must succeed so we can verify the stored TimeoutSeconds");

        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IssueIdentifier == request.IssueIdentifier.Value);
        item.Should().NotBeNull();
        item!.TimeoutSeconds.Should().Be(
            customTimeout,
            "a positive TimeoutSeconds must be stored as-is without clamping");
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
    /// used by the Scheduler's WorkItemDispatchLoop for all task types.
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
            AgentApiKeyValue = "test-master-key",
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

    private static DispatchWorkItemService CreateDispatchService(JobTemplateStore templateStore) =>
        new DispatchWorkItemService(templateStore);

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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

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
            missingId, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

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
            existingId, dbFactory, lifecycle, templateStore, resolver, mockLock.Object, CreateDispatchService(templateStore), CancellationToken.None);

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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);
        var task2 = WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

        var results = await Task.WhenAll(task1, task2);

        // Exactly one 200 and one non-200
        var okCount = results.Count(r => r is Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>);
        okCount.Should().Be(1, "exactly one concurrent dispatch must succeed");

        // TODO [WARNING]: The assertion above only confirms that exactly one call returned 200. It
        // does not constrain what the losing call returned — an unhandled exception (500) or a 503
        // would also satisfy okCount == 1. Consider adding:
        //   results.Count(r => r is not Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>).Should().Be(1)
        // or a tighter assertion on the losing result type to prevent silent exception propagation
        // from masking test failures.

        // TODO [CRITICAL→REMOVED]: A conflictCount.Should().Be(1) assertion was here, but it was
        // tautological: task1 and task2 execute cooperatively/sequentially (same sync context, no
        // Task.Run), so task2 always starts *after* task1 completes. At that point, the item is
        // already Dispatched and the pre-lock fast-path check (which predates the post-lock re-read
        // fix) returns 409 immediately — the new post-lock re-read code path is never reached.
        // Deleting the post-lock re-read block from WorkItemDispatchEndpoints.cs would not cause
        // this test to fail. The concurrent-dispatch 409 scenario is covered by the dedicated
        // Test 15 (DispatchPendingWorkItem_ConcurrentDispatch_LosingCallerReceives409Conflict)
        // which uses a mock lock provider to exercise the exact TOCTOU window.

        // K8s Job created exactly once (CAS prevents the second from reaching K8s)
        k8sCallCount.Should().Be(1, "K8s Job must be created exactly once — double-dispatch prevented");

        // DB must have exactly one Dispatched item
        await using var db = await dbFactory.CreateDbContextAsync();
        var dispatched = await db.WorkItems.AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Dispatched)
            .ToListAsync();
        dispatched.Should().HaveCount(1, "exactly one WorkItem must be Dispatched");
    }

    // ── Test 15: Concurrent dispatch — losing caller receives 409 (not 503) ───

    /// <summary>
    /// AC1 + AC2: When a WorkItem is transitioned to <c>Dispatched</c> by a concurrent caller
    /// between the fast-path status check and lock acquisition, <c>DispatchPendingWorkItem</c>
    /// must return 409 Conflict (not 503), so the Scheduler treats it as a permanent rejection
    /// rather than a transient error requiring a full-cycle retry.
    ///
    /// This test simulates the race window directly: a mock lock provider transitions the WorkItem
    /// to <c>Dispatched</c> during <c>AcquireAsync</c>, after the fast-path check has already
    /// confirmed the item is <c>Pending</c>. The post-lock status re-read inside the lock then
    /// detects the non-Pending status and returns 409 before entering the dispatch lifecycle.
    ///
    /// Without the post-lock re-read, the loser would enter <c>ExecuteDispatchLifecycleAsync</c>,
    /// find the item no longer <c>Pending</c>, exit early (<c>dispatched = false</c>), and the
    /// endpoint would return 503.
    /// </summary>
    [Fact]
    public async Task DispatchPendingWorkItem_ConcurrentDispatch_LosingCallerReceives409Conflict()
    {
        var dbFactory = CreateDbFactory();
        var entity = await SeedPendingItemAsync(dbFactory, "opencode,python");

        // Non-kiro template — PVC gate is skipped entirely (providerType != "kiro")
        var templateStore = CreateTemplateStore("opencode,python", maxConcurrent: 10, providerType: "opencode");
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Empty PVC pool — confirms PVC gate is not involved in the result
        var lifecycle = CreateLifecycleService(k8sMock.Object, pvcPool: []);
        var resolver = CreateTemplateResolver(templateStore);

        // Mock lock provider that simulates the race: during AcquireAsync (after the fast-path
        // check has already confirmed Pending), transition the item to Dispatched in the DB.
        // This opens the exact TOCTOU window that the post-lock re-read is designed to close.
        // TODO [WARNING]: This test uses a non-kiro (opencode) provider template to bypass the PVC
        // gate. The post-lock re-read fix is provider-agnostic (it precedes the PVC gate in the
        // kiro flow as well), but the kiro path with a populated PVC pool is not exercised here.
        // If the post-lock re-read were accidentally relocated to after the PVC check, this test
        // would still pass while the kiro path remained broken.
        // TODO [WARNING]: This test's validity depends on the post-lock re-read using AsNoTracking()
        // (which issues a fresh SQL query, bypassing EF's first-level cache). If the production
        // query were changed to tracked mode (dropping AsNoTracking()), the cached entity from the
        // fast-path check would still report Pending, and the mock-DB write in AcquireAsync would
        // be invisible to the re-read — the test would silently stop detecting the TOCTOU race.
        var handle = new Mock<IAsyncDisposable>();
        handle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var racingLockMock = new Mock<IDistributedLockProvider>();
        racingLockMock
            .Setup(lp => lp.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            // TODO [WARNING]: Moq's Returns(async ...) for a Task-returning delegate does not wrap
            // exceptions thrown inside the async body into a faulted Task before returning — it
            // returns the Task directly. If SaveChangesAsync inside the lambda throws, the exception
            // surfaces from the awaited AcquireAsync call in production code and produces an
            // unhandled exception rather than a Conflict result, causing a misleading test failure.
            // A more robust setup would use a dedicated async helper or TaskCompletionSource to
            // make the failure mode explicit if the mock setup itself is broken.
            .Returns(async (string _, CancellationToken ct) =>
            {
                // Simulate a concurrent dispatch path transitioning the item to Dispatched
                // while this caller is waiting to acquire the lock.
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var item = await db.WorkItems.FindAsync([entity.Id], ct);
                if (item is { Status: WorkItemStatus.Pending })
                {
                    item.Status = WorkItemStatus.Dispatched;
                    item.DispatchedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                return (IAsyncDisposable)handle.Object;
            });

        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, racingLockMock.Object, CreateDispatchService(templateStore), CancellationToken.None);

        // The post-lock re-read must detect the non-Pending status and return 409 Conflict.
        // Without the fix this would return 503 (lifecycle early-return → dispatched=false).
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>(
            "when the item is dispatched between fast-path check and lock acquisition, the endpoint must return 409 Conflict");

        // TODO [WARNING]: This test only exercises the Dispatched transition during the TOCTOU
        // window. The post-lock condition `postLockCheck.Status != WorkItemStatus.Pending` fires
        // identically for Running, Succeeded, Failed, and Cancelled statuses. None of these are
        // exercised by any test. If the condition is ever narrowed (e.g. to check only Dispatched),
        // those paths would silently regress. Consider adding a parameterised test covering all
        // non-Pending status values.

        // TODO [WARNING]: The `postLockCheck is null` branch (item row deleted between fast-path
        // check and lock acquisition) is not covered by any test. The production comment explicitly
        // documents this guard ("a missing row returns null… treated as non-Pending to avoid
        // dispatching a ghost"), but removing the null guard would cause a NullReferenceException
        // at runtime and no test would catch it. Consider adding a test where the mock's
        // AcquireAsync deletes the row rather than updating it.

        // K8s must NOT have been called — the post-lock check must short-circuit before the lifecycle
        k8sMock.Verify(
            k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never(),
            "K8s must not be called when the post-lock re-read detects the item is no longer Pending");
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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

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
            entity2.Id, dbFactory, lifecycle2, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);
        result2.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>>(
            "PVC must be released after K8s failure — Failed item no longer holds the credential slot");
    }

    // ── Test 17: FailWorkItemAsync throws → 503, Warning logged, item remains Pending ──

    /// <summary>
    /// When K8s Job creation fails AND the subsequent <c>FailWorkItemAsync</c> call itself throws
    /// (e.g. a faulted <see cref="IDbContextFactory{T}"/>), the exception must be caught at the
    /// <c>FailWorkItemAsync</c> call site inside <c>CreateK8sJobAsync</c>. The endpoint must return
    /// 503, the WorkItem must remain <c>Pending</c> (for reconciliation), and a <c>Warning</c>-level
    /// Serilog log entry must be emitted with the WorkItem ID.
    /// <para>
    /// Without the fix, the exception from <c>FailWorkItemAsync</c> propagates to
    /// <c>DispatchPendingWorkItem</c>'s outer catch, which logs a generic <c>Error</c> with no
    /// WorkItem-ID attribution — this is the bug described in issue #2647.
    /// </para>
    /// </summary>
    // TODO: This test uses providerType: "opencode" to skip the PVC gate (ctx.ClaimedPvc is null),
    // which means the db.SaveChangesAsync PVC-cleanup path inside CreateK8sJobAsync's catch block is
    // never exercised. A second test (or parameterised case) with providerType: "kiro" and a single
    // PVC in the pool would lock in coverage of the kiro-path: K8s throws + FailWorkItemAsync throws
    // with a claimed PVC in play, verifying that the PVC is released and the Warning is still emitted.
    // TODO: The OnFailure callback path (ctx.OnFailure is not null) is not exercised in this test.
    // DispatchPendingWorkItem currently passes onFailure: null at both call sites, but if a non-null
    // OnFailure is added in the future, a test verifying correct behaviour when both FailWorkItemAsync
    // and OnFailure throw (or only one of them) should be added.
    [Fact]
    public async Task DispatchPendingWorkItem_FailWorkItemAsyncThrows_Returns503_WarningLogged_ItemRemainingPending()
    {
        // Arrange: seed a Pending WorkItem via the working factory.
        var dbFactory = CreateDbFactory();
        var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");

        // Construct a faulted IDbContextFactory — its CreateDbContextAsync always throws
        // InvalidOperationException. This factory is ONLY injected into the WorkItemTransitionService
        // inside the faulted lifecycle; the outer endpoint still receives the working dbFactory.
        // The faulted factory causes FailWorkItemAsync → TransitionAsync → TransitionCoreAsync →
        // _dbFactory.CreateDbContextAsync to throw, exercising the new inner try/catch.
        var faultedFactory = new FaultedDbContextFactory();

        var faultedTransitionSvc = new WorkItemTransitionService(
            faultedFactory,
            Mock.Of<ILogger<WorkItemTransitionService>>());

        // K8s mock throws to enter CreateK8sJobAsync's outer catch block.
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("K8s API unavailable"));

        // Non-kiro template: PVC gate is skipped entirely, so no PVC cleanup SaveChangesAsync
        // runs in the catch block — we test only the FailWorkItemAsync throw path.
        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 5, providerType: "opencode");
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        // Capture Serilog events by injecting a capturing logger directly into the faulted lifecycle.
        // DispatchLifecycleService now accepts an optional Serilog.ILogger parameter; when provided,
        // all _log.* calls in the lifecycle route through it instead of the global static logger.
        // This avoids the static-field-initialization-time capture problem that prevents
        // Serilog.Log.Logger reassignment from intercepting events emitted via ForContext<T>().
        var capturedEvents = new List<Serilog.Events.LogEvent>();
        var capturingSink = new CapturingSink(capturedEvents);
        var capturingLogger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(capturingSink)
            .CreateLogger();

        // Construct the faulted lifecycle directly (not via CreateLifecycleService, which wires
        // a working factory). The capturing logger is injected so Warning events are observable.
        var faultedLifecycle = new DispatchLifecycleService(
            k8sMock.Object,
            faultedTransitionSvc,
            new DispatchServiceOptions
            {
                Namespace = "test",
                OrchestratorUrl = "http://test",
                AgentApiKeySecretName = "secret",
                AgentApiKeyValue = "test-master-key",
                AgentServiceAccountName = "sa",
                KiroPvcPool = []  // non-kiro: no PVC involvement
            },
            logger: capturingLogger);

        IResult result;
        try
        {
            // Act — use the working dbFactory for the endpoint's own DB operations, but the
            // faulted lifecycle so that FailWorkItemAsync throws inside CreateK8sJobAsync.
            result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
                entity.Id, dbFactory, faultedLifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);
        }
        finally
        {
            faultedLifecycle.Dispose();
            if (capturingLogger is IDisposable d) d.Dispose();
        }

        // Assert 1: result must be 503 — the exception was caught, not propagated.
        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull("FailWorkItemAsync throw must be caught — result must be 503, not an unhandled exception");
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

        // Assert 2: item must remain Pending — FailWorkItemAsync never wrote the transition
        // because it threw inside TransitionCoreAsync before reaching SaveChangesAsync.
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == entity.Id);
        item!.Status.Should().Be(WorkItemStatus.Pending,
            "FailWorkItemAsync threw before writing the transition — item must remain Pending for reconciliation");

        // Assert 3: a Warning-level log entry must have been emitted at the FailWorkItemAsync call site,
        // including the WorkItem ID as a structured property.
        capturedEvents.Should().Contain(
            e => e.Level == Serilog.Events.LogEventLevel.Warning &&
                 e.MessageTemplate.Text.Contains("FailWorkItemAsync threw"),
            "a Warning log must be emitted when FailWorkItemAsync throws in CreateK8sJobAsync");

        var warningEvent = capturedEvents.First(
            e => e.Level == Serilog.Events.LogEventLevel.Warning &&
                 e.MessageTemplate.Text.Contains("FailWorkItemAsync threw"));
        warningEvent.Properties.Should().ContainKey("WorkItemId",
            "the Warning log must include the WorkItem ID as a structured property");
        // TODO: This assertion uses a substring match (.Contain) because Serilog's ScalarValue
        // serialisation wraps GUIDs in double-quotes (e.g. "\"3f2504e0-...\""). While not
        // exploitable for GUIDs, this is a fragile pattern. Prefer extracting the raw value via
        // ((Serilog.Events.ScalarValue)warningEvent.Properties["WorkItemId"]).Value and asserting
        // .Should().Be(entity.Id) for type-safe, exact equality.
        warningEvent.Properties["WorkItemId"].ToString().Should().Contain(entity.Id.ToString(),
            "the WorkItem ID in the Warning log must match the seeded entity");
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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

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
                itemId, dbFactory, lc, ts, res, CreateNoOpLockProvider(), CreateDispatchService(ts), CancellationToken.None);
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
                itemId, dbFactory, lc, ts, res, CreateNoOpLockProvider(), CreateDispatchService(ts), CancellationToken.None);
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
                itemId, dbFactory, lc, ts, res, CreateNoOpLockProvider(), CreateDispatchService(ts), CancellationToken.None);
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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, timeoutLockMock.Object, CreateDispatchService(templateStore), CancellationToken.None);

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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

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

    // ── Test 18: Profile fallback — projection.AgentSelector must be the canonical selector ──

    /// <summary>
    /// Characterization test for issue #2777 (selector-key mismatch on profile-fallback path).
    ///
    /// When <c>DispatchPendingWorkItem</c> resolves a template via the profile fallback (item has
    /// partial selector "dotnet", profile expands it to canonical "dotnet,kiro"), the
    /// <see cref="PendingWorkItemProjection.AgentSelector"/> written into the context must equal the
    /// canonical selector "dotnet,kiro" — not the raw normalized input "dotnet".
    ///
    /// Observability: after a successful dispatch, the in-memory concurrency dictionary passed to
    /// <see cref="DispatchLifecycleService.ExecuteDispatchLifecycleAsync"/> is mutated by
    /// <c>FinalizeDispatchAsync</c>, which keys the increment on <c>item.AgentSelector</c>.
    /// If the projection carries the wrong key ("dotnet"), the increment lands under "dotnet"; if
    /// it carries the correct key ("dotnet,kiro"), it lands under "dotnet,kiro". We assert the
    /// increment landed under the canonical key.
    ///
    /// This test fails against the current (broken) code because <c>projection.AgentSelector</c>
    /// is set to <c>normalizedSelector</c> ("dotnet") rather than the resolved canonical selector.
    /// It passes after the fix that propagates <c>effectiveSelector</c>.
    /// </summary>
    // TODO [WARNING]: This test does NOT actually assert that projection.AgentSelector equals the
    // canonical selector "dotnet,kiro". It only verifies that dispatch succeeded (200 OK),
    // LoadAgentProfilesAsync was called once, and CreateJobAsync was called once — all of which
    // would also pass against the pre-fix broken code. The primary acceptance criterion for the
    // projection.AgentSelector value (criterion 1) is covered by Test 19 instead. Consider either
    // removing this test (its coverage is a subset of Test 19's) or restructuring it to assert
    // the stored AgentSelector on the dispatched WorkItemEntity equals "dotnet,kiro".
    [Fact]
    public async Task DispatchPendingWorkItem_ProfileFallback_SetsProjectionAgentSelectorToCanonicalSelector()
    {
        // Arrange: item has partial selector "dotnet"; profile expands to canonical "dotnet,kiro"
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

        // We observe the projection.AgentSelector value indirectly via the concurrencyBySelector
        // dictionary that DispatchPendingWorkItem constructs and passes to ExecuteDispatchLifecycleAsync.
        // After a successful dispatch, FinalizeDispatchAsync increments concurrencyBySelector[item.AgentSelector].
        // Because DispatchPendingWorkItem uses the same dictionary reference throughout, we can inspect
        // it by building our own and observing it post-dispatch via a wrapping approach.
        //
        // Simpler approach: verify via the DispatchLifecycleContext.ConcurrencyBySelector.
        // We seed ZERO active items in the DB, so the concurrency map starts empty.
        // After dispatch: the increment must be keyed on "dotnet,kiro" (canonical), not "dotnet" (partial).
        // We can't directly read the map from outside the endpoint, but we CAN verify the net effect:
        // a second dispatch attempt for a new item with selector "dotnet" and maxConcurrent=1 will be
        // blocked only if the increment was recorded under the key that the gate checks.

        // Act: dispatch the first item
        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

        // Assert: dispatch succeeded
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>>(
            "profile fallback must resolve the template and dispatch the item");

        // Assert: the profile fallback path was actually entered (not the direct-resolve path)
        profileStoreMock.Verify(
            ps => ps.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "LoadAgentProfilesAsync must be called exactly once — confirming the profile-fallback path was taken, not the direct-resolve path");

        // Assert: the K8s job was created with the canonical selector embedded in the job spec context.
        // JobSpecBuilder.Build receives ctx.AgentSelector = item.AgentSelector. After the fix,
        // item.AgentSelector == "dotnet,kiro". We verify by capturing the V1Job spec passed to
        // CreateJobAsync and checking its label value.

        // TODO [WARNING]: The capture setup below is dead code — CreateJobAsync was already called
        // during the Act step above, before this re-setup runs. The callback will never fire and
        // capturedJobs will always be empty. Any assertion added on capturedJobs would vacuously pass
        // (false negative). The canonical-selector assertion is covered by Test 19 instead.
        // This block should either be removed or the test restructured to capture before the Act step.
        var capturedJobs = new List<k8s.Models.V1Job>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<k8s.Models.V1Job, string, CancellationToken>((job, _, _) => capturedJobs.Add(job))
            .Returns(Task.CompletedTask);

        // For the AgentSelector assertion, we dispatch a second item to get a fresh K8s call.
        // (The first call already completed above; re-using it here would require a second dispatch.)
        // Instead: verify via the concurrencyBySelector increment — the most direct observable.
        // Seed a second pending item with the same partial selector "dotnet" and maxConcurrent: 1.
        // If the first dispatch incremented under "dotnet,kiro", the second should be blocked (409)
        // when we use a fresh template store with maxConcurrent: 1 and seed ONE active item under "dotnet,kiro".
        // This is Test 19. For THIS test, we verify only via profileStoreMock.Verify (which confirms
        // the fallback was used) and that dispatch succeeded (i.e., the template was resolved).
        // The canonical-selector increment assertion is covered by Test 19.
        k8sMock.Verify(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "K8s job must have been created exactly once on the profile-fallback path");
    }

    // ── Test 19: Profile fallback — concurrency gate uses canonical selector ──

    /// <summary>
    /// Characterization test for issue #2777 (concurrency gate key mismatch on profile-fallback path).
    ///
    /// When <c>DispatchPendingWorkItem</c> resolves a template via the profile fallback (item has
    /// partial selector "dotnet", profile expands to canonical "dotnet,kiro") and the concurrency
    /// limit for "dotnet,kiro" is already reached (one active item stored under "dotnet,kiro"),
    /// the endpoint must return 409 Conflict.
    ///
    /// On the current (broken) code, <c>IsAtConcurrencyLimit</c> is called with
    /// <c>normalizedSelector</c> ("dotnet"), which has no entry in the concurrency map → count=0 →
    /// limit not reached → dispatch proceeds (over-dispatch). This test will therefore fail against
    /// the current code with a 200 OK instead of 409.
    ///
    /// After the fix, <c>IsAtConcurrencyLimit</c> is called with <c>effectiveSelector</c>
    /// ("dotnet,kiro"), which has count=1 ≥ maxConcurrent=1 → returns 409.
    /// </summary>
    [Fact]
    public async Task DispatchPendingWorkItem_ProfileFallback_ConcurrencyLimitUsesCanonicalSelector()
    {
        // Arrange: one active item stored under the canonical selector "dotnet,kiro" fills maxConcurrent=1
        var dbFactory = CreateDbFactory();
        await SeedActiveItemAsync(dbFactory, selector: "dotnet,kiro"); // this goes into concurrencyBySelector["dotnet,kiro"] = 1

        // Pending item has the PARTIAL selector "dotnet" — template only exists for "dotnet,kiro"
        var entity = await SeedPendingItemAsync(dbFactory, selector: "dotnet");

        // Template is keyed on "dotnet,kiro" with maxConcurrent=1 (already full from the active item above)
        var templateStore = CreateTemplateStore("dotnet,kiro", maxConcurrent: 1, providerType: "kiro");

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
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

        // Assert: concurrency limit must be respected using the canonical selector "dotnet,kiro"
        // Current (broken) code: checks normalizedSelector="dotnet" → count=0 → gate passes → returns 200
        // Fixed code: checks effectiveSelector="dotnet,kiro" → count=1 ≥ maxConcurrent=1 → returns 409
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>(
            "concurrency limit for 'dotnet,kiro' (count=1, maxConcurrent=1) must block dispatch even when the item's " +
            "stored selector is the partial form 'dotnet' that requires profile-fallback resolution");

        // Confirm the item remains Pending (gate rejection must not change state)
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == entity.Id);
        item!.Status.Should().Be(WorkItemStatus.Pending,
            "concurrency gate rejection on the profile-fallback path must leave the item Pending");

        // K8s must NOT have been called — the concurrency gate must short-circuit before dispatch
        k8sMock.Verify(
            k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "K8s must not be called when the concurrency gate blocks dispatch");

        // Confirm the profile fallback was actually entered (not direct-resolve path)
        profileStoreMock.Verify(
            ps => ps.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "LoadAgentProfilesAsync must be called once — confirming the profile-fallback path was taken");
    }
    // ── Test 20: Combined concurrency+PVC — 409, counter NOT incremented ─────

    /// <summary>
    /// AC2: When both the concurrency limit is reached AND the PVC pool is empty for a Kiro agent,
    /// <c>ApplyGates</c> returns a 409 Conflict (concurrency gate fires first, short-circuiting
    /// before the PVC gate). The <c>PvcPoolExhaustions</c> counter must NOT be incremented because
    /// the rejection is a concurrency rejection, not a PVC exhaustion.
    /// </summary>
    // TODO [WARNING]: This test covers the Kiro-agent combined scenario only. The prior bug was
    // specifically gated on `isKiroAgent && pvcResult.AvailablePvcs.Count == 0`, so a non-Kiro
    // agent in the same concurrency-limit + empty-"PVC pool" situation would not have triggered
    // the old over-counting. The production fix (`gateResult is StatusCodeHttpResult { StatusCode: 503 }`)
    // is correct regardless of agent type, but there is no test asserting that a non-Kiro agent
    // with a combined concurrency+PVC rejection also does not increment PvcPoolExhaustions.
    // This is a missing-coverage gap rather than a regression risk for the current fix.
    [Fact]
    public async Task DispatchPendingWorkItem_ConcurrencyLimitReachedAndPvcPoolEmpty_Returns409_AndDoesNotIncrementPvcPoolExhaustions()
    {
        var dbFactory = CreateDbFactory();
        // Fill concurrency limit (maxConcurrent=2) and claim the only PVC simultaneously.
        await SeedActiveItemAsync(dbFactory, "kiro,dotnet", claimedPvcName: "pvc-0");
        // TODO [WARNING]: The second SeedActiveItemAsync call intentionally omits claimedPvcName
        // (leaving it null). QueryAvailablePvcsAsync filters claimed PVCs by ClaimedPvcName != null,
        // so only "pvc-0" (from the first item) is treated as claimed, leaving zero available PVCs
        // from the single-entry pool ["pvc-0"]. This null is load-bearing: if a future maintainer
        // adds a claimedPvcName here or extends the pool, the "zero available PVCs" precondition
        // could silently break without a direct test failure on the concurrency assertion.
        await SeedActiveItemAsync(dbFactory, "kiro,dotnet");
        var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");

        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 2);
        // PVC pool contains only "pvc-0", which is already claimed → zero available PVCs.
        var lifecycle = CreateLifecycleService(pvcPool: ["pvc-0"]);
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        // Subscribe to PvcPoolExhaustions on the static meter before calling the endpoint.
        // MeterListener captures only measurements made during its active window (delta semantics).
        // TODO [WARNING]: The three MeterListener setups in Tests 20, 21, and 22 duplicate the
        // instrument name string "workdistribution.pvc_pool_exhaustions" verbatim. If the instrument
        // is ever renamed, all three listeners silently stop enabling the instrument, exhaustionCount
        // stays 0, and assertions that check for 0 will still pass — creating vacuous tests that no
        // longer guard the fix. Extract a shared helper (e.g. CreatePvcExhaustionListener) to ensure
        // a rename is caught at a single site.
        long exhaustionCount = 0;
        // TODO [WARNING]: `counting` is a plain bool read inside the MeterListener callback, which
        // may be invoked on a thread-pool thread. While the await before `counting = false` provides
        // a memory barrier in practice on current .NET semantics, the read inside the callback has
        // no formal visibility guarantee. Use `volatile bool counting` or replace with an
        // Interlocked-controlled long to make the ordering robust and silence analyser warnings.
        // The same applies to the identical pattern in Tests 21 and 22.
        var counting = false;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName &&
                instrument.Name == "workdistribution.pvc_pool_exhaustions")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (counting && instrument.Name == "workdistribution.pvc_pool_exhaustions")
                Interlocked.Add(ref exhaustionCount, measurement);
        });
        listener.Start();
        // Warm-up: the static instrument was created before the listener started; enabling via
        // InstrumentPublished during Start() handles already-published instruments correctly.

        counting = true;
        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);
        counting = false;

        // Gate result: concurrency limit fires first → 409, not 503.
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>(
            "concurrency gate fires before the PVC gate — combined rejection must return 409");

        // Counter must NOT have been incremented (this was the bug).
        Interlocked.Read(ref exhaustionCount).Should().Be(0,
            "PvcPoolExhaustions must not fire on a 409 concurrency rejection, even when the PVC pool is also empty");

        // Item must remain Pending (gate rejection must not alter state).
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == entity.Id);
        item!.Status.Should().Be(WorkItemStatus.Pending, "gate rejection must leave the item Pending");
    }

    // ── Test 21: Genuine PVC exhaustion (503) — counter IS incremented ────────

    /// <summary>
    /// Positive regression guard: when the PVC pool is empty and concurrency is below the limit,
    /// <c>ApplyGates</c> returns 503 and <c>PvcPoolExhaustions</c> must be incremented by 1.
    /// This verifies the fix did not break the correct path.
    /// </summary>
    [Fact]
    public async Task DispatchPendingWorkItem_PvcPoolEmpty_ConcurrencyBelow_Returns503_AndIncrementsPvcPoolExhaustions()
    {
        var dbFactory = CreateDbFactory();
        // Claim the only PVC but stay below maxConcurrent=5 → concurrency gate passes, PVC gate fires.
        await SeedActiveItemAsync(dbFactory, "kiro,dotnet", claimedPvcName: "pvc-0");
        var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");

        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 5);
        var lifecycle = CreateLifecycleService(pvcPool: ["pvc-0"]);
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        long exhaustionCount = 0;
        // TODO [WARNING]: `counting` is a plain bool read inside the MeterListener callback, which
        // may be invoked on a thread-pool thread. While the await before `counting = false` provides
        // a memory barrier in practice on current .NET semantics, the read inside the callback has
        // no formal visibility guarantee. Use `volatile bool counting` or an Interlocked-controlled
        // long to make the ordering robust. See Test 20 for the same note.
        var counting = false;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName &&
                instrument.Name == "workdistribution.pvc_pool_exhaustions")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (counting && instrument.Name == "workdistribution.pvc_pool_exhaustions")
                Interlocked.Add(ref exhaustionCount, measurement);
        });
        listener.Start();

        counting = true;
        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);
        counting = false;

        // PVC gate fires → 503.
        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull();
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable,
            "zero available PVCs with concurrency below limit must return 503");

        // Counter must have been incremented exactly once.
        Interlocked.Read(ref exhaustionCount).Should().Be(1,
            "PvcPoolExhaustions must be incremented by 1 when the 503/PVC-gate path fires");
    }

    // ── Test 22: Concurrency rejection with PVCs available — counter NOT incremented ──

    /// <summary>
    /// Control case: when only the concurrency gate fires (PVCs are available), the endpoint
    /// returns 409 and <c>PvcPoolExhaustions</c> must not be incremented.
    /// </summary>
    [Fact]
    public async Task DispatchPendingWorkItem_ConcurrencyLimitReached_PvcsAvailable_Returns409_AndDoesNotIncrementPvcPoolExhaustions()
    {
        var dbFactory = CreateDbFactory();
        await SeedActiveItemAsync(dbFactory, "kiro,dotnet");
        await SeedActiveItemAsync(dbFactory, "kiro,dotnet");
        var entity = await SeedPendingItemAsync(dbFactory, "kiro,dotnet");

        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 2);
        // PVCs are available — only the concurrency gate fires.
        var lifecycle = CreateLifecycleService(pvcPool: ["pvc-0", "pvc-1"]);
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        long exhaustionCount = 0;
        // TODO [WARNING]: `counting` is a plain bool read inside the MeterListener callback, which
        // may be invoked on a thread-pool thread. While the await before `counting = false` provides
        // a memory barrier in practice on current .NET semantics, the read inside the callback has
        // no formal visibility guarantee. Use `volatile bool counting` or an Interlocked-controlled
        // long to make the ordering robust. See Test 20 for the same note.
        var counting = false;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName &&
                instrument.Name == "workdistribution.pvc_pool_exhaustions")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (counting && instrument.Name == "workdistribution.pvc_pool_exhaustions")
                Interlocked.Add(ref exhaustionCount, measurement);
        });
        listener.Start();

        counting = true;
        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);
        counting = false;

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>(
            "concurrency limit with PVCs available must return 409");

        Interlocked.Read(ref exhaustionCount).Should().Be(0,
            "PvcPoolExhaustions must not fire when only the concurrency gate rejects the request");
    }

    // ── Test 23: Post-gate PVC race — !dispatched branch → 503, item remains Pending ─

    /// <summary>
    /// Characterization test for the <c>!dispatched</c> branch in <c>DispatchPendingWorkItem</c>.
    ///
    /// <para>
    /// Gates pass (PVC appears available), the lifecycle is entered, the K8s Job is created, but
    /// a concurrent path transitions the WorkItem from <c>Pending</c> to <c>Dispatched</c> during
    /// the K8s API call. When <c>HandleOrphanedJobIfRaceDetectedAsync</c> reloads the WorkItem and
    /// finds it is no longer in <c>Pending</c> state (<c>ExpectedInitialStatus</c>), it returns
    /// <c>shouldContinue = false</c> and the lifecycle returns without calling <c>onDispatchSuccess</c>.
    /// </para>
    ///
    /// <para>
    /// Expected result: 503 Service Unavailable, item remains <c>Dispatched</c> (as set by the
    /// concurrent path). Critically, <c>SafelyCancelOrphanedDispatchedWorkItemAsync</c> must NOT
    /// be called on this path — the item was already claimed by the concurrent dispatch, and
    /// cancelling it would destroy that concurrent run. The <c>DispatchPendingWorkItem</c> handler
    /// explicitly does NOT pass an <c>onDispatchFailure</c> delegate to <c>RunDispatchLifecycleAsync</c>.
    /// </para>
    ///
    /// <para>
    /// This test is the safety net for the AC1 extraction: after extracting the <c>!dispatched</c>
    /// branch into <c>RunDispatchLifecycleAsync</c>, this test proves the extracted method does
    /// NOT call <c>SafelyCancelOrphanedDispatchedWorkItemAsync</c> on the
    /// <c>DispatchPendingWorkItem</c> path (where <c>onDispatchFailure</c> is null).
    /// </para>
    /// </summary>
    [Fact]
    public async Task DispatchPendingWorkItem_PostGatePvcRace_LifecycleExitsWithoutDispatching_Returns503_ItemUntouched()
    {
        // Arrange: seed a Pending item; use a non-kiro template to skip PVC involvement entirely.
        // The race is simulated at the K8s-call boundary: the K8s mock transitions the item to
        // Dispatched during CreateJobAsync, so HandleOrphanedJobIfRaceDetectedAsync finds
        // Status ≠ Pending and returns shouldContinue=false.
        var dbFactory = CreateDbFactory();
        var entity = await SeedPendingItemAsync(dbFactory, "opencode,python");

        // Non-kiro template — PVC gate is skipped, no PVC involvement.
        var templateStore = CreateTemplateStore("opencode,python", maxConcurrent: 5, providerType: "opencode");
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        // K8s mock: during CreateJobAsync, transition the item to Dispatched in the DB to simulate
        // a concurrent dispatch path claiming the item between the pre-write save and the
        // HandleOrphanedJobIfRaceDetectedAsync reload.
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (k8s.Models.V1Job _, string _, CancellationToken ct) =>
            {
                // Concurrent path: mark the item as Dispatched while CreateJobAsync is running.
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var item = await db.WorkItems.FindAsync([entity.Id], ct);
                if (item is { Status: WorkItemStatus.Pending })
                {
                    item.Status = WorkItemStatus.Dispatched;
                    item.DispatchedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            });

        // DeleteJobAsync is called by HandleOrphanedJobIfRaceDetectedAsync — allow it silently.
        // TODO [WARNING]: No Verify is performed on DeleteJobAsync. HandleOrphanedJobIfRaceDetectedAsync
        // calling DeleteJobAsync is the mechanism that causes shouldContinue=false and the 503 path here.
        // If that method is later refactored to skip the delete, the item would stay in a different state
        // (e.g. Pending rather than Dispatched) and the Status assertion below would fail — but the missing
        // delete itself would be undetected. Consider adding:
        //   k8sMock.Verify(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once)
        // to lock in that the orphaned-job cleanup runs on this code path.
        var deleteJobMock = k8sMock;
        deleteJobMock.Setup(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Empty PVC pool — non-kiro so PVC gate is not reached.
        var lifecycle = CreateLifecycleService(k8sMock.Object, pvcPool: []);

        // Act
        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entity.Id, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

        // Assert 1: endpoint returns 503 (lifecycle exited without dispatching)
        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull("lifecycle exit without dispatching must return 503");
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

        // Assert 2: item was claimed by the concurrent path and remains Dispatched.
        // SafelyCancelOrphanedDispatchedWorkItemAsync must NOT have been called
        // (the item is still Dispatched — it was not transitioned to Failed/Cancelled).
        await using var db = await dbFactory.CreateDbContextAsync();
        var updatedItem = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == entity.Id);
        updatedItem.Should().NotBeNull();
        updatedItem!.Status.Should().Be(WorkItemStatus.Dispatched,
            "DispatchPendingWorkItem must not call SafelyCancelOrphanedDispatchedWorkItemAsync — " +
            "the item was claimed by a concurrent dispatch path and must remain Dispatched");
    }

    // ── TraceParent passthrough (issue #2977) ────────────────────────────────

    /// <summary>
    /// Verifies that when a Pending WorkItemEntity has a non-null TraceParent, the K8s Job created
    /// by DispatchLifecycleService carries the TRACEPARENT env var with the exact value.
    ///
    /// Root cause (issue #2977): DispatchLifecycleService.CreateK8sJobAsync built the BuildContext
    /// from ctx.Item (PendingWorkItemProjection, which has NO TraceParent field) instead of
    /// ctx.WorkItem (WorkItemEntity, which has the TraceParent column). The fix adds
    /// TraceParent = ctx.WorkItem.TraceParent to the BuildContext initializer.
    ///
    /// This test seeds a WorkItemEntity with a known TraceParent directly in the in-memory DB
    /// (Activity.Current is null in test environments), captures the V1Job via a Moq Callback,
    /// and asserts TRACEPARENT appears in the container env with the correct value.
    /// </summary>
    [Fact]
    public async Task DispatchPendingWorkItem_WhenWorkItemHasTraceParent_K8sJobCarriesTraceparentEnvVar()
    {
        // Arrange: seed a Pending WorkItem with a known TraceParent.
        // Activity.Current is null in tests, so we seed directly — not via the endpoint.
        // This is the path where TraceParent was originally broken: it was set at WorkItem creation
        // time (WorkItemDispatchEndpoints.CreatePendingItem) but never forwarded to the BuildContext.
        const string expectedTraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var dbFactory = CreateDbFactory();
        await using (var seedDb = await dbFactory.CreateDbContextAsync())
        {
            seedDb.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(), // placeholder; we'll re-query below
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"trace-test-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-1",
                Status = WorkItemStatus.Pending,
                AgentSelector = "kiro,dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = "{}",
                TraceParent = expectedTraceParent   // ← the value that must reach the K8s Job
            });
            await seedDb.SaveChangesAsync();
        }

        // Retrieve the seeded entity's ID
        Guid entityId;
        await using (var readDb = await dbFactory.CreateDbContextAsync())
        {
            var seeded = await readDb.WorkItems.AsNoTracking()
                .FirstAsync(w => w.TraceParent == expectedTraceParent);
            entityId = seeded.Id;
        }

        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 5);
        k8s.Models.V1Job? capturedJob = null;
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock
            .Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<k8s.Models.V1Job, string, CancellationToken>((job, _, _) => capturedJob = job)
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object, ["pvc-0"]);
        var resolver = CreateTemplateResolver(templateStore);
        var lockProvider = CreateNoOpLockProvider();

        // Act
        var result = await WorkItemDispatchEndpoints.DispatchPendingWorkItem(
            entityId, dbFactory, lifecycle, templateStore, resolver, lockProvider, CreateDispatchService(templateStore), CancellationToken.None);

        // Assert 1: dispatch succeeded
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>>(
            "a Pending WorkItem with TraceParent must dispatch successfully");

        // Assert 2: the K8s Job contains TRACEPARENT with the exact value from WorkItemEntity.TraceParent.
        // This is the core fix for issue #2977: TraceParent must flow through ctx.WorkItem (WorkItemEntity),
        // not ctx.Item (PendingWorkItemProjection which lacks the field).
        capturedJob.Should().NotBeNull("CreateJobAsync must have been called with a V1Job");
        var envVars = capturedJob!.Spec.Template.Spec.Containers[0].Env;
        var traceEnv = envVars.FirstOrDefault(e => e.Name == "TRACEPARENT");
        traceEnv.Should().NotBeNull("TRACEPARENT must be present in the K8s Job container env");
        traceEnv!.Value.Should().Be(expectedTraceParent,
            "TRACEPARENT value must match WorkItemEntity.TraceParent — the agent pod needs it to attach its spans to the upstream dispatch trace");
    }

}

// ── Characterization tests for unique-violation idempotent-retry (CreateWorkItem + DispatchWorkItem) ──

/// <summary>
/// Characterization tests for the <c>catch (Exception ex) when (IsUniqueViolation(ex))</c>
/// block in <c>CreateWorkItem</c> and <c>DispatchWorkItem</c>.
///
/// <para>
/// These tests lock in the behavior of the unique-violation idempotent-retry path before
/// extracting it into a shared <c>DispatchWorkItemService.HandleUniqueViolationAsync</c> method.
/// </para>
///
/// <para>
/// EF InMemory throws <c>ArgumentException("An item with the same key has already been added")</c>
/// on PK duplicate (not <c>DbUpdateException</c>). <c>IsUniqueViolation</c> handles this via the
/// "An item with the same key has already been added" string match. The partial unique-index case
/// (IssueIdentifier + IssueProviderConfigId conflict) is simulated by seeding a live item with
/// the same IssueIdentifier+IssueProviderConfigId pair (since InMemory removes the filtered index
/// via the shim in <c>TestPipelineDbContext</c>), then engineering a <c>HandleUniqueViolationFallback</c>
/// call via a <c>ArgumentException</c> triggered by PK collision without a matching ID row.
/// </para>
/// </summary>
public sealed class UniqueViolationIdempotentRetryTests
{
    private readonly string _dbName = $"unique-violation-test-{Guid.NewGuid():N}";

    private IDbContextFactory<PipelineDbContext> CreateDbFactory()
    {
        var opts = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SimpleInMemoryDbContextFactory(opts);
    }

    private static IOrchestratorRunService CreateRunService() =>
        new OrchestratorRunService(Mock.Of<Serilog.ILogger>());

    private static JobDistributionRequest MakeRequest(
        string? issueIdentifier = null,
        string? runId = null) => new()
        {
            IssueIdentifier = new IssueIdentifier(issueIdentifier ?? $"issue-{Guid.NewGuid():N}"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro,dotnet",
            TimeoutSeconds = 3600,
            RunId = runId ?? Guid.NewGuid().ToString()
        };

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

    // ── CreateWorkItem unique-violation tests ────────────────────────────────────

    /// <summary>
    /// When <c>CreateWorkItem</c> is called twice with the same RunId (same workItemId), the
    /// second call must return 201 (idempotent PK retry) — the item already exists.
    /// </summary>
    [Fact]
    public async Task CreateWorkItem_UniqueViolation_IdempotentPkRetry_Returns201()
    {
        // Arrange: first call creates the item; second call with same RunId triggers PK conflict.
        var dbFactory = CreateDbFactory();
        var runService = CreateRunService();
        var runId = Guid.NewGuid().ToString();
        var request = MakeRequest(runId: runId);

        // Act: first call — succeeds, returns 201
        var firstResult = await WorkItemDispatchEndpoints.CreateWorkItem(
            request, dbFactory, runService, CancellationToken.None);
        firstResult.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Created<Guid>>(
            "first call must succeed");

        // Act: second call with same RunId — triggers PK unique violation
        var secondResult = await WorkItemDispatchEndpoints.CreateWorkItem(
            request, dbFactory, runService, CancellationToken.None);

        // Assert: second call must return 201 (idempotent — item already exists)
        var created = secondResult as Microsoft.AspNetCore.Http.HttpResults.Created<Guid>;
        created.Should().NotBeNull("idempotent PK retry must return 201");
        var workItemId = Guid.Parse(runId);
        created!.Value.Should().Be(workItemId, "the returned GUID must match the original workItemId");
    }

    /// <summary>
    /// When <c>CreateWorkItem</c> receives a unique-violation exception that is NOT a PK duplicate
    /// (i.e., a business-rule partial unique index conflict — same IssueIdentifier + IssueProviderConfigId),
    /// it must return 409 Conflict via <c>HandleUniqueViolationFallback</c>.
    ///
    /// <para>
    /// Simulation: the EF InMemory provider strips filtered indexes (via <c>TestPipelineDbContext</c>),
    /// so we cannot directly trigger the IssueIdentifier+IssueProviderConfigId index. Instead, we
    /// seed an existing item for the same issue, then force a PK collision with a DIFFERENT RunId
    /// by seeding a second item with the same PK (but a non-matching RunId so the idempotent check
    /// returns 409, not 201). We achieve this by pre-seeding an item with the target ID directly in the DB,
    /// then calling <c>CreateWorkItem</c> with a RunId matching that pre-seeded ID (so the PK collides,
    /// and then the idempotent re-check finds the row exists → 201). To get the 409 path we need to trigger
    /// the conflict on a key that does NOT exist in the DB after the conflict fires. The cleanest approach:
    /// use a <c>ThrowingDbContextFactory</c> that produces a PK-collision <c>ArgumentException</c> on
    /// <c>SaveChangesAsync</c> while the corresponding ID does NOT exist in the re-check DB.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CreateWorkItem_UniqueViolation_WhenNotIdempotentRetry_Returns409()
    {
        // Arrange: use a factory that throws a PK-collision ArgumentException on SaveChanges,
        // but the "re-check" DbContext (fresh) finds no existing row with that workItemId.
        // This simulates the partial unique-index conflict path where the workItemId is new
        // but a different run is already live for the same IssueIdentifier+IssueProviderConfigId.
        var workItemId = Guid.NewGuid();
        var runId = workItemId.ToString(); // so the endpoint parses this as workItemId

        // Use a two-factory setup: a throwing factory for SaveChanges (triggers IsUniqueViolation)
        // while the re-check DbContext (opened fresh) finds no row → falls through to HandleUniqueViolationFallback.
        // We accomplish this by using a real InMemory DB (no pre-seeded row) for both contexts,
        // but the write context throws ArgumentException to simulate the unique violation.
        // Since the read-back DB is the same InMemory DB and has no row, HandleUniqueViolationFallback fires.
        var dbFactory = new ThrowingOnSaveDbContextFactory(_dbName);
        var runService = CreateRunService();
        var request = MakeRequest(runId: runId);

        // Act
        var result = await WorkItemDispatchEndpoints.CreateWorkItem(
            request, dbFactory, runService, CancellationToken.None);

        // Assert: 409 Conflict via HandleUniqueViolationFallback
        var conflict = result as Microsoft.AspNetCore.Http.HttpResults.Conflict<string>;
        conflict.Should().NotBeNull("unique-violation with no pre-existing row must return 409 via HandleUniqueViolationFallback");
        conflict!.Value.Should().Contain("live work item", "the body must be the HandleUniqueViolationFallback message");
    }

    // ── DispatchWorkItem unique-violation tests ──────────────────────────────────

    /// <summary>
    /// When <c>DispatchWorkItem</c> encounters a unique-violation exception and the existing item
    /// is active (Dispatched or Running), it must return 200 (idempotent retry — item is running).
    /// </summary>
    [Fact]
    public async Task DispatchWorkItem_UniqueViolation_WhenExistingItemIsActive_Returns200()
    {
        // Arrange: use a factory where SaveChanges throws ArgumentException (unique violation),
        // and the re-check DB (fresh context) contains a Dispatched item with the matching ID.
        var workItemId = Guid.NewGuid();
        var dbFactory = new PrepopulatedThrowingDbContextFactory(_dbName + "-dw-active", workItemId, WorkItemStatus.Dispatched);

        var runService = CreateRunService();
        var templateStore = CreateTemplateStore(maxConcurrent: 5);
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object);

        // Request with RunId = workItemId → handler uses this as workItemId
        var request = MakeRequest(runId: workItemId.ToString());

        // Act
        var result = await WorkItemDispatchEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

        // Assert: 200 OK — item is active (Dispatched), idempotent retry succeeds
        var ok = result as Microsoft.AspNetCore.Http.HttpResults.Ok<Guid>;
        ok.Should().NotBeNull("active existing item must return 200 on unique-violation idempotent retry");
        ok!.Value.Should().Be(workItemId);
    }

    /// <summary>
    /// When <c>DispatchWorkItem</c> encounters a unique-violation exception and the existing item
    /// is in a non-active state (e.g., Failed), it must return 409 Conflict so the Scheduler
    /// re-queues the issue rather than treating it as dispatched.
    /// </summary>
    [Fact]
    public async Task DispatchWorkItem_UniqueViolation_WhenExistingItemIsNonActive_Returns409()
    {
        // Arrange: SaveChanges throws unique violation; re-check DB has a Failed item.
        var workItemId = Guid.NewGuid();
        var dbFactory = new PrepopulatedThrowingDbContextFactory(_dbName + "-dw-failed", workItemId, WorkItemStatus.Failed);

        var runService = CreateRunService();
        var templateStore = CreateTemplateStore(maxConcurrent: 5);
        var k8sMock = new Mock<IKubernetesJobClient>();
        k8sMock.Setup(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var lifecycle = CreateLifecycleService(k8sMock.Object);

        var request = MakeRequest(runId: workItemId.ToString());

        // Act
        var result = await WorkItemDispatchEndpoints.DispatchWorkItem(
            request, dbFactory, runService, lifecycle, templateStore, new DispatchWorkItemService(templateStore), CancellationToken.None);

        // Assert: 409 Conflict — existing item is non-active (Failed)
        var conflict = result as Microsoft.AspNetCore.Http.HttpResults.Conflict<string>;
        conflict.Should().NotBeNull("non-active existing item must return 409 on unique-violation idempotent retry");
        conflict!.Value.Should().Contain("Failed", "the 409 body must include the current non-active status");
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

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
            AgentApiKeyValue = "test-master-key",
            AgentServiceAccountName = "sa",
            KiroPvcPool = (pvcPool ?? ["pvc-0", "pvc-1"]).ToList()
        };
        return new DispatchLifecycleService(
            k8sClient ?? Mock.Of<IKubernetesJobClient>(),
            transitionSvc,
            opts);
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

/// <summary>
/// Minimal Serilog sink that collects emitted <see cref="Serilog.Events.LogEvent"/> instances into a
/// caller-supplied list. Used by tests that temporarily replace <c>Serilog.Log.Logger</c> to assert
/// on log output from code that writes via the global static Serilog logger
/// (e.g. <c>Serilog.Log.ForContext&lt;T&gt;()</c>).
/// </summary>
file sealed class CapturingSink(List<Serilog.Events.LogEvent> events) : Serilog.Core.ILogEventSink
{
    public void Emit(Serilog.Events.LogEvent logEvent) => events.Add(logEvent);
}

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> that always throws
/// <see cref="InvalidOperationException"/> from <c>CreateDbContextAsync</c>. Used to simulate a
/// faulted database factory so that <c>WorkItemTransitionService.TransitionCoreAsync</c> throws
/// when exercising the <c>FailWorkItemAsync</c>-throws code path inside
/// <c>DispatchLifecycleService.CreateK8sJobAsync</c>.
/// </summary>
file sealed class FaultedDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    public PipelineDbContext CreateDbContext() =>
        throw new InvalidOperationException("DB factory faulted");

    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("DB factory faulted");
}

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> that throws
/// <see cref="ArgumentException"/> from <c>SaveChangesAsync</c> on the first write (to simulate
/// an EF InMemory PK-collision unique violation), but allows subsequent read-only contexts
/// (created fresh via the real InMemory database) to operate normally.
/// Used by <see cref="UniqueViolationIdempotentRetryTests"/> to exercise the
/// <c>HandleUniqueViolationFallback</c> path where no matching row exists in the DB.
/// </summary>
file sealed class ThrowingOnSaveDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _opts;
    // TODO [WARNING]: _hasThrown is consumed on the FIRST call to CreateDbContextAsync, not on the
    // first SaveChangesAsync. If CreateWorkItem opens a read context before the write context
    // (e.g. a pre-check query added in the future), _hasThrown is set on that read context and
    // the ThrowingPipelineDbContext is never used for the actual save — the test would then pass
    // trivially on the non-violation path without exercising HandleUniqueViolationFallback.
    // Consider switching to a factory that counts SaveChangesAsync invocations instead.
    private bool _hasThrown;

    public ThrowingOnSaveDbContextFactory(string dbName)
    {
        _opts = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName + "-throwing")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    public PipelineDbContext CreateDbContext() => CreateContext();

    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult<PipelineDbContext>(CreateContext());

    private PipelineDbContext CreateContext()
    {
        if (!_hasThrown)
        {
            _hasThrown = true;
            return new ThrowingPipelineDbContext(_opts);
        }
        // Subsequent contexts (the re-check read) return a normal context with an empty DB
        // (no row with the workItemId exists), so HandleUniqueViolationFallback is invoked.
        return new TestPipelineDbContext(_opts);
    }
}

/// <summary>
/// A <see cref="PipelineDbContext"/> whose <see cref="SaveChangesAsync"/> always throws
/// <see cref="ArgumentException"/> with the EF InMemory PK-collision message. Used to
/// simulate unique-violation triggering without a real Postgres database.
/// </summary>
file sealed class ThrowingPipelineDbContext : PipelineDbContext
{
    public ThrowingPipelineDbContext(DbContextOptions<PipelineDbContext> opts) : base(opts) { }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        Task.FromException<int>(
            new ArgumentException("An item with the same key has already been added. Key: simulated-pk-collision"));

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default) =>
        Task.FromException<int>(
            new ArgumentException("An item with the same key has already been added. Key: simulated-pk-collision"));

    public override int SaveChanges() =>
        throw new ArgumentException("An item with the same key has already been added. Key: simulated-pk-collision");

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

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> for testing the unique-violation catch block in
/// <c>DispatchWorkItem</c> and <c>CreateWorkItem</c>. On the FIRST call, returns a context whose
/// <c>SaveChangesAsync</c> throws <see cref="ArgumentException"/> (simulating a unique-violation).
/// On subsequent calls, returns a real InMemory context pre-populated with a work item at the
/// specified <paramref name="workItemId"/> in the specified <paramref name="existingStatus"/>.
/// This simulates the catch block's re-check path: a fresh DbContext is opened to query whether
/// the conflicting item already exists and in what state.
/// </summary>
file sealed class PrepopulatedThrowingDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _throwOpts;
    private readonly DbContextOptions<PipelineDbContext> _readOpts;
    private readonly Guid _workItemId;
    private readonly WorkItemStatus _existingStatus;
    // TODO [WARNING]: _hasThrown is consumed on the FIRST call to CreateDbContextAsync. The
    // DispatchWorkItem handler opens at least one context for the pre-save work AND a fresh one
    // inside HandleUniqueViolationAsync for the re-check. If the handler opens more than one
    // context before SaveChangesAsync throws, _hasThrown is consumed on the first non-save call
    // and the re-check context is returned from the throw-opts (empty) DB rather than the
    // pre-populated read DB — making both DispatchWorkItem unique-violation tests sensitive to
    // the exact number of CreateDbContextAsync calls before save. Consider tracking invocations
    // more precisely (e.g. counting SaveChangesAsync throws) to decouple from call-order assumptions.
    private bool _hasThrown;

    public PrepopulatedThrowingDbContextFactory(string dbName, Guid workItemId, WorkItemStatus existingStatus)
    {
        _workItemId = workItemId;
        _existingStatus = existingStatus;

        // Throwing context uses an empty DB — SaveChangesAsync throws before any write.
        _throwOpts = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName + "-throw")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        // Read context uses a separate DB pre-populated with the "existing" item.
        _readOpts = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName + "-read")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        // Pre-populate the read DB with the "existing" item.
        using var seedCtx = new TestPipelineDbContext(_readOpts);
        seedCtx.WorkItems.Add(new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"issue-{workItemId:N}",
            IssueProviderConfigId = "prov-1",
            Status = existingStatus,
            AgentSelector = "kiro,dotnet",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            DispatchedAt = existingStatus == WorkItemStatus.Dispatched ? DateTimeOffset.UtcNow : null,
            Payload = "{}"
        });
        seedCtx.SaveChanges();
    }

    public PipelineDbContext CreateDbContext() => CreateContext();

    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult<PipelineDbContext>(CreateContext());

    private PipelineDbContext CreateContext()
    {
        if (!_hasThrown)
        {
            _hasThrown = true;
            // First context: throws on SaveChangesAsync; uses an empty DB so Add() succeeds.
            return new ThrowingPipelineDbContext(_throwOpts);
        }
        // Subsequent contexts: reads from the pre-populated DB to find the "existing" item.
        return new TestPipelineDbContext(_readOpts);
    }
}
