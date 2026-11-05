using System.Text.Json;
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
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.UnitTests.Helpers;
using CodingAgent.Web.TestUtilities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="DispatchWorkItemService"/> — the shared helper class extracted
/// from <see cref="WorkItemDispatchEndpoints"/> by issue #2743.
///
/// <para>
/// These tests target the service methods directly. The handler-level behaviour is covered
/// by the existing tests in <see cref="CodingAgent.Pipeline.UnitTests.Services.DispatchPendingWorkItemEndpointTests"/>,
/// which continue to exercise the full dispatch pipeline (including the new service).
/// </para>
/// </summary>
[Collection("Metrics")]
public sealed class DispatchWorkItemServiceTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DispatchWorkItemService CreateService(
        string labels = "kiro,dotnet",
        int maxConcurrent = 5,
        string providerType = "kiro",
        IAgentStartGate? gate = null) =>
        new DispatchWorkItemService(CreateTemplateStore(labels, maxConcurrent, providerType), gate ?? AgentStartGateFakes.AllowAll());

    /// <summary>
    /// Spec 057: the real <see cref="AgentStartGate"/> over <paramref name="locks"/>, a Job client that lists no Jobs
    /// and the UI limits of <paramref name="dbFactory"/>'s database — so the lock-handling tests below still observe
    /// the handle the lock provider returns.
    /// </summary>
    private static AgentStartGate CreateGate(IDistributedLockProvider locks, IDbContextFactory<PipelineDbContext> dbFactory) =>
        new AgentStartGate(
            locks,
            Mock.Of<IKubernetesJobClient>(),
            CreateTemplateStore(),
            new DispatchServiceOptions { Namespace = "test" },
            new AgentCapacityStore(dbFactory),
            Serilog.Log.Logger);

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

    private static IDbContextFactory<PipelineDbContext> CreateDbFactory(string? dbName = null)
    {
        var name = dbName ?? $"dispatch-svc-test-{Guid.NewGuid():N}";
        var opts = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SimpleInMemoryDbContextFactory(opts);
    }

    /// <summary>
    /// Creates raw <see cref="DbContextOptions{TContext}"/> so both the seeding context and the
    /// <see cref="ThrowingOnSaveDbContext"/> can be constructed from the exact same in-memory
    /// database instance. This is required for Test 1: if the throwing context is opened from
    /// a different options object it would see an empty store, <c>FindAsync</c> would return
    /// null, and <c>ExecuteDispatchLifecycleAsync</c> would exit via the <c>workItem is null</c>
    /// guard (exercising the <c>!dispatched</c> 503 branch, not the exception-catch branch).
    /// </summary>
    private static DbContextOptions<PipelineDbContext> CreateDbOptions(string? dbName = null)
    {
        var name = dbName ?? $"dispatch-svc-test-{Guid.NewGuid():N}";
        return new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    private static PvcAvailabilityResult MakePvcResult(int available, int claimed = 0) =>
        new PvcAvailabilityResult(
            Enumerable.Range(0, available).Select(i => $"pvc-{i}").ToList(),
            claimed);

    private static JobTemplate ResolveTemplate(JobTemplateStore store, string selector = "kiro,dotnet")
        => store.Resolve(JobTemplateStore.NormalizeLabels(selector))!;

    private static async Task SeedWorkItemAsync(
        IDbContextFactory<PipelineDbContext> dbFactory,
        WorkItemStatus status,
        string selector)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"issue-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = status,
            AgentSelector = selector,
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
    }

    // TODO [WARNING]: CreateLifecycleService constructs its own independent CreateDbFactory(),
    // so the lifecycle service's internal WorkItemTransitionService queries a completely
    // separate in-memory database from the one used in test bodies. The PVC availability
    // query (QueryAvailablePvcsAsync) uses the caller-supplied db argument directly, so
    // the test currently passes correctly — but the isolation between the two factories
    // creates a latent confusion that could mask a regression if the plumbing changes.
    // The lifecycle service's private dbFactory is dead weight here.
    private DispatchLifecycleService CreateLifecycleService(IReadOnlyList<string>? pvcPool = null, IKubernetesJobClient? kubeClient = null)
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
            kubeClient ?? Mock.Of<IKubernetesJobClient>(),
            transitionSvc,
            opts);
    }

    /// <summary>
    /// Creates a <see cref="RolloutRouting"/> over the supplied <paramref name="dbFactory"/>
    /// WITHOUT seeding a routing table. Per <see cref="TestWorkItemRouter"/> docs: without a
    /// seeded routing table, every route request is refused with <c>no_route</c>.
    /// </summary>
    private static RolloutRouting CreateRolloutRouting(IDbContextFactory<PipelineDbContext> dbFactory, JobTemplateStore templateStore)
    {
        var router = TestWorkItemRouter.Create(dbFactory, templateStore);
        var transitions = new WorkItemStatusTransitionService(
            new WorkItemTransitionService(dbFactory, NullLogger<WorkItemTransitionService>.Instance),
            Mock.Of<IRunLifecycleManager>(),
            NullLogger<WorkItemStatusTransitionService>.Instance,
            dbFactory);
        return new RolloutRouting(dbFactory, router, transitions);
    }

    private static async Task<Guid> SeedPendingAsync(IDbContextFactory<PipelineDbContext> dbFactory, string selector)
    {
        var id = Guid.NewGuid();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"issue-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = WorkItemStatus.Pending,
            AgentSelector = selector,
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ── ApplyGates (PVC gate only since spec 057) ─────────────────────────────

    [Fact]
    public void ApplyGates_KiroAgentNoPvc_Returns503()
    {
        var result = CreateService().ApplyGates("kiro,dotnet", MakePvcResult(available: 0), isKiroAgent: true, callerName: "Test");

        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull("PVC exhaustion for a kiro agent must fire the gate");
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public void ApplyGates_KiroAgentWithAPvc_ReturnsNull()
    {
        CreateService().ApplyGates("kiro,dotnet", MakePvcResult(available: 1), isKiroAgent: true, callerName: "Test")
            .Should().BeNull("a free PVC passes the gate");
    }

    [Fact]
    public void ApplyGates_NonKiroAgentNoPvc_ReturnsNull()
    {
        CreateService(providerType: "opencode").ApplyGates("kiro,dotnet", MakePvcResult(available: 0), isKiroAgent: false, callerName: "Test")
            .Should().BeNull("non-kiro agents skip the PVC gate — zero PVCs must not block dispatch");
    }

    // ── QueryDispatchablePvcsAsync (spec 057 DD1: held PVCs from the gate's Job list) ──

    [Fact]
    public async Task QueryDispatchablePvcsAsync_LeavesOutClaimedAndHeldPvcs()
    {
        var dbFactory = CreateDbFactory();
        await using (var seedDb = await dbFactory.CreateDbContextAsync())
        {
            seedDb.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"pvc-holder-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-1",
                Status = WorkItemStatus.Dispatched,
                AgentSelector = "kiro,dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = "{}",
                ClaimedPvcName = "pvc-0"
            });
            await seedDb.SaveChangesAsync();
        }
        var lifecycle = CreateLifecycleService(pvcPool: ["pvc-0", "pvc-1", "pvc-2"]);
        await using var db = await dbFactory.CreateDbContextAsync();

        var pvcResult = await lifecycle.QueryDispatchablePvcsAsync(db, new HashSet<string> { "pvc-1" }, CancellationToken.None);

        pvcResult.AvailablePvcs.Should().Equal(["pvc-2"], "pvc-0 is claimed by a work item and pvc-1 mounted by a running Job (e.g. a chat)");
        pvcResult.ClaimedCount.Should().Be(1);
    }

    // ── DispatchPendingWorkItemAsync with the agent start gate (spec 057) ─────

    // req 3.1, 3.2
    [Theory]
    [InlineData(LimitScope.Global, "global_concurrency_limit")]
    [InlineData(LimitScope.Template, "concurrency_limit")]
    public async Task DispatchPendingWorkItemAsync_WhenALimitIsReached_HoldsTheItemWithTheReason_AndCreatesNoJob(
        LimitScope scope, string reason)
    {
        var dbFactory = CreateDbFactory();
        var id = await SeedPendingAsync(dbFactory, "kiro,dotnet");
        var kube = new Mock<IKubernetesJobClient>();
        var gate = AgentStartGateFakes.Deny(new AgentStartDecision(false, scope, LimitSource.Ui, 3, 3));
        var svc = CreateService(gate: gate);

        var result = await svc.DispatchPendingWorkItemAsync(
            id, dbFactory, CreateLifecycleService(kubeClient: kube.Object), CreateRolloutRouting(dbFactory, CreateTemplateStore()), CancellationToken.None);

        var ok = result as Microsoft.AspNetCore.Http.HttpResults.Ok<DispatchPendingResponse>;
        ok.Should().NotBeNull();
        ok!.Value.Should().Be(new DispatchPendingResponse(false, reason));
        kube.Verify(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        await using var db = await dbFactory.CreateDbContextAsync();
        (await db.WorkItems.SingleAsync(w => w.Id == id)).Status.Should().Be(WorkItemStatus.Pending, "a held item stays Pending");
        gate.Disposed.Should().Be(gate.Entered, "the start lock is released on the hold path");
    }

    // req 2.4
    [Fact]
    public async Task DispatchPendingWorkItemAsync_WhenRunningAgentsCannotBeCounted_Returns503_AndCreatesNoJob()
    {
        var dbFactory = CreateDbFactory();
        var id = await SeedPendingAsync(dbFactory, "kiro,dotnet");
        var kube = new Mock<IKubernetesJobClient>();
        var svc = CreateService(gate: AgentStartGateFakes.Unavailable());

        var result = await svc.DispatchPendingWorkItemAsync(
            id, dbFactory, CreateLifecycleService(kubeClient: kube.Object), CreateRolloutRouting(dbFactory, CreateTemplateStore()), CancellationToken.None);

        (result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        kube.Verify(k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task DispatchPendingWorkItemAsync_WhenTheLockTimesOut_Returns503()
    {
        var dbFactory = CreateDbFactory();
        var id = await SeedPendingAsync(dbFactory, "kiro,dotnet");
        var svc = CreateService(gate: AgentStartGateFakes.LockTimeout());

        var result = await svc.DispatchPendingWorkItemAsync(
            id, dbFactory, CreateLifecycleService(), CreateRolloutRouting(dbFactory, CreateTemplateStore()), CancellationToken.None);

        (result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    // req 3.1: a missing template is answered before the limit check (and the lease is released).
    [Fact]
    public async Task DispatchPendingWorkItemAsync_NoTemplate_IsAnsweredBeforeTheLimitCheck()
    {
        var dbFactory = CreateDbFactory();
        var id = await SeedPendingAsync(dbFactory, "python,unknown");
        var gate = AgentStartGateFakes.Deny(new AgentStartDecision(false, LimitScope.Global, LimitSource.Helm, 1, 1));
        var svc = CreateService(gate: gate);

        var result = await svc.DispatchPendingWorkItemAsync(
            id, dbFactory, CreateLifecycleService(), CreateRolloutRouting(dbFactory, CreateTemplateStore()), CancellationToken.None);

        (result as Microsoft.AspNetCore.Http.HttpResults.Ok<DispatchPendingResponse>)!.Value!.Reason.Should().Be("no_template");
        gate.Disposed.Should().Be(gate.Entered);
    }

    // DD1: the PVC a running chat Job mounts is not offered to the work item.
    [Fact]
    public async Task DispatchPendingWorkItemAsync_PvcGate_UsesTheLeasesHeldPvcs()
    {
        var dbFactory = CreateDbFactory();
        var id = await SeedPendingAsync(dbFactory, "kiro,dotnet");
        var chatJob = new k8s.Models.V1Job
        {
            Metadata = new k8s.Models.V1ObjectMeta { Name = "caa-chat-1a2b3c4d", Labels = new Dictionary<string, string>() },
            Spec = new k8s.Models.V1JobSpec
            {
                Template = new k8s.Models.V1PodTemplateSpec
                {
                    Spec = new k8s.Models.V1PodSpec
                    {
                        Containers = [],
                        Volumes = [new k8s.Models.V1Volume { Name = "kiro-cli-data", PersistentVolumeClaim = new k8s.Models.V1PersistentVolumeClaimVolumeSource { ClaimName = "pvc-0" } }]
                    }
                }
            },
            Status = new k8s.Models.V1JobStatus()
        };
        var listing = new Mock<IKubernetesJobClient>();
        listing.Setup(k => k.ListJobsAsync("test", AgentJobPvcs.AgentJobSelector, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new k8s.Models.V1JobList { Items = [chatJob] });
        var svc = CreateService(gate: AgentStartGateFakes.AllowAll(listing.Object, "test"));

        // The pool has only pvc-0, which the chat mounts → no PVC for the kiro item → 503 pvc_unavailable.
        var result = await svc.DispatchPendingWorkItemAsync(
            id, dbFactory, CreateLifecycleService(pvcPool: ["pvc-0"]), CreateRolloutRouting(dbFactory, CreateTemplateStore()), CancellationToken.None);

        (result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult)!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    // ── HandleUniqueViolationFallback ─────────────────────────────────────────

    [Fact]
    public void HandleUniqueViolationFallback_ReturnsConflictWithExpectedMessage()
    {
        var result = DispatchWorkItemService.HandleUniqueViolationFallback();

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>(
            "the fallback must produce a 409 Conflict");

        var conflict = (Microsoft.AspNetCore.Http.HttpResults.Conflict<string>)result;
        conflict.Value.Should().Be("A live work item already exists for this issue.",
            "the exact literal must match what callers previously inlined");
    }

    // ── BuildProjectionFromQuickCheck ─────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DispatchWorkItemService.BuildProjectionFromQuickCheck"/> maps the 9 projection
    /// fields (8 from the quick-check row, AgentSelector from normalizedSelector) to the correct <see cref="PendingWorkItemProjection"/> fields.
    /// </summary>
    [Fact]
    public void BuildProjectionFromQuickCheck_MapsAllFieldsCorrectly()
    {
        // Arrange: distinctive values for each of the 9 projection fields
        var id = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2025, 3, 10, 8, 0, 0, TimeSpan.Zero);
        const string normalizedSelector = "dotnet,kiro";
        const int timeoutSeconds = 3600;
        const WorkItemTaskType taskType = WorkItemTaskType.Implementation;
        const string issueIdentifier = "GH-99";
        const string issueProviderConfigId = "prov-xyz";
        const int priorityWeight = 50;

        // Act
        var quickCheck = new DispatchQuickCheck
        {
            Id = id,
            Status = WorkItemStatus.Pending,
            AgentSelector = "dotnet",
            CreatedAt = createdAt,
            TimeoutSeconds = timeoutSeconds,
            TaskType = taskType,
            ProjectId = projectId,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderConfigId,
            PriorityWeight = priorityWeight
        };
        var projection = DispatchWorkItemService.BuildProjectionFromQuickCheck(quickCheck, normalizedSelector);

        // Assert: all 9 fields mapped correctly
        projection.Id.Should().Be(id);
        projection.AgentSelector.Should().Be(normalizedSelector, "AgentSelector must come from normalizedSelector parameter");
        projection.CreatedAt.Should().Be(createdAt);
        projection.TimeoutSeconds.Should().Be(timeoutSeconds);
        projection.TaskType.Should().Be(taskType);
        projection.ProjectId.Should().Be(projectId);
        projection.IssueIdentifier.Should().Be(issueIdentifier);
        projection.IssueProviderConfigId.Should().Be(issueProviderConfigId);
        projection.PriorityWeight.Should().Be(priorityWeight);
    }

    // ── TryEnterDispatchAsync ─────────────────────────────────────────

    /// <summary>
    /// AC4 (issue #3795) — Regression test for the double-dispose risk on the non-Pending branch.
    /// When the item is NOT Pending (so <c>acquiredLock.DisposeAsync()</c> is called inside the
    /// try block) AND that disposal throws, the catch block must NOT call <c>DisposeAsync()</c>
    /// a second time; it must re-throw the original exception.
    ///
    /// <para>
    /// Without the <c>lockTransferred</c> flag fix the catch block calls
    /// <c>acquiredLock.DisposeAsync()</c> unconditionally, so this test would record
    /// <c>Times.Exactly(2)</c> and the <c>Times.Once()</c> assertion would fail.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TryEnterDispatchAsync_NonPendingBranchDisposeThrows_CatchDoesNotDisposeAgainAndRethrows()
    {
        // Arrange — lock handle whose DisposeAsync() throws (simulates a DB connection drop
        // at the moment of advisory-lock release on the non-Pending early-return path).
        var lockHandle = new Mock<IAsyncDisposable>();
        lockHandle.Setup(h => h.DisposeAsync())
            .ThrowsAsync(new InvalidOperationException("simulated disposal failure"));

        var lockProvider = new Mock<IDistributedLockProvider>();
        lockProvider
            .Setup(p => p.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lockHandle.Object);

        // Seed a non-Pending work item so the if-branch is entered.
        var dbFactory = CreateDbFactory();
        var workItemId = Guid.NewGuid();
        await using (var seedCtx = await dbFactory.CreateDbContextAsync())
        {
            seedCtx.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                Status = WorkItemStatus.Dispatched,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"issue-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-1",
                AgentSelector = "dotnet,kiro",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = "{}"
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var db = await dbFactory.CreateDbContextAsync();

        // Act & Assert — original exception from the in-try DisposeAsync() propagates unchanged.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DispatchWorkItemService.TryEnterDispatchAsync(
                CreateGate(lockProvider.Object, dbFactory), db, workItemId, CancellationToken.None));
        ex.Message.Should().Be("simulated disposal failure",
            "the original exception must propagate via 'throw;', not be replaced by a new throw");

        // The critical assertion: DisposeAsync was called exactly once (the in-try call).
        // Without the lockTransferred fix this would be Times.Exactly(2).
        lockHandle.Verify(h => h.DisposeAsync(), Times.Once(),
            "catch block must not call DisposeAsync() a second time when the in-try disposal threw");
    }

    /// <summary>
    /// AC1 — Regression test for issue #3701.
    /// When the post-lock DB query throws after AcquireAsync succeeds, the acquired lock
    /// must be disposed exactly once before the exception propagates.
    /// This test fails on the pre-fix code (no try/catch around the post-lock body) and passes
    /// after the fix is applied.
    ///
    /// <para>
    /// The post-lock query is made to throw by passing a pre-cancelled CancellationToken.
    /// The AcquireAsync mock is set up to succeed regardless (returns a completed task),
    /// so the lock IS acquired. The in-memory provider then checks <c>ct.ThrowIfCancellationRequested()</c>
    /// at the start of query execution and throws <c>OperationCanceledException</c>.
    /// The bare <c>catch</c> block in the fix handles all exception types, so this correctly
    /// tests the lock-disposal behavior. No native SQLite library is used, avoiding
    /// process-crash interactions with Coverlet's coverage finalization.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TryEnterDispatchAsync_PostLockQueryThrows_DisposesLockExactlyOnce()
    {
        // Arrange — trackable lock handle
        var lockHandle = new Mock<IAsyncDisposable>();
        lockHandle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        // AcquireAsync mock returns a completed task, so it succeeds even for a cancelled token.
        var lockProvider = new Mock<IDistributedLockProvider>();
        lockProvider
            .Setup(p => p.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lockHandle.Object);

        // Use EF in-memory provider — no native SQLite library required.
        var dbFactory = CreateDbFactory();
        await using var db = await dbFactory.CreateDbContextAsync();

        // Pre-cancel the token. AcquireAsync completes (mock ignores the token);
        // the post-lock FirstOrDefaultAsync then calls ThrowIfCancellationRequested()
        // and throws OperationCanceledException.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DispatchWorkItemService.TryEnterDispatchAsync(
                CreateGate(lockProvider.Object, dbFactory), db, Guid.NewGuid(), cts.Token));

        lockHandle.Verify(h => h.DisposeAsync(), Times.Once(),
            "lock must be disposed exactly once on the exception path");
    }

    /// <summary>
    /// AC2 — Caller-ownership path.
    /// When the item is Pending, TryEnterDispatchAsync must return (lockHandle, null)
    /// without calling DisposeAsync inside the method — the caller holds dispose responsibility.
    /// </summary>
    [Fact]
    public async Task TryEnterDispatchAsync_ItemIsPending_ReturnsLockWithoutDisposing()
    {
        // Arrange
        var lockHandle = new Mock<IAsyncDisposable>();
        lockHandle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var lockProvider = new Mock<IDistributedLockProvider>();
        lockProvider
            .Setup(p => p.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lockHandle.Object);

        var dbFactory = CreateDbFactory();
        var workItemId = Guid.NewGuid();
        await using (var seedCtx = await dbFactory.CreateDbContextAsync())
        {
            seedCtx.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                Status = WorkItemStatus.Pending,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"issue-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-1",
                AgentSelector = "dotnet,kiro",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = "{}"
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var db = await dbFactory.CreateDbContextAsync();

        // Act
        var (returnedLock, earlyReturn) = await DispatchWorkItemService.TryEnterDispatchAsync(
            CreateGate(lockProvider.Object, dbFactory), db, workItemId, CancellationToken.None);

        // Assert — caller receives the lock handle; DisposeAsync is NOT called inside the method
        returnedLock.Should().NotBeNull(
            "the lock handle must be returned to the caller, not kept internally");
        earlyReturn.Should().BeNull(
            "a Pending item has no early-return reason — the caller proceeds to dispatch");
        lockHandle.Verify(h => h.DisposeAsync(), Times.Never(),
            "caller owns the lock on the Pending success path — DisposeAsync must not be called inside TryEnterDispatchAsync");
    }

    /// <summary>
    /// Happy-path non-Pending: when the post-lock re-read finds a work item that is no longer
    /// <c>Pending</c> (e.g. already <c>Dispatched</c> by a concurrent caller) and
    /// <c>DisposeAsync()</c> succeeds, <see cref="DispatchWorkItemService.TryEnterDispatchAsync"/>
    /// must dispose the acquired lock and return <c>(null, 200/deferred)</c>.
    ///
    /// <para>
    /// This is the normal-disposal complement to
    /// <see cref="TryEnterDispatchAsync_NonPendingBranchDisposeThrows_CatchDoesNotDisposeAgainAndRethrows"/>:
    /// both tests enter the non-Pending if-block, but here <c>DisposeAsync()</c> returns
    /// <c>ValueTask.CompletedTask</c> so execution reaches the
    /// <c>return (null, TypedResults.Ok(...))</c> statement — the lines that are unreachable
    /// when disposal throws.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TryEnterDispatchAsync_NonPendingAfterLockAcquisition_ReturnsDeferredResultAndDisposesLock()
    {
        // Arrange — lock handle whose DisposeAsync() succeeds (no throw on the normal path).
        var lockHandle = new Mock<IAsyncDisposable>();
        lockHandle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var lockProvider = new Mock<IDistributedLockProvider>();
        lockProvider
            .Setup(p => p.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lockHandle.Object);

        // Seed a non-Pending (Dispatched) work item so the post-lock re-check enters the
        // non-Pending branch. The item is seeded as Dispatched to simulate the race where
        // a concurrent caller won the dispatch between the fast-path check and lock acquisition.
        var dbFactory = CreateDbFactory();
        var workItemId = Guid.NewGuid();
        await using (var seedCtx = await dbFactory.CreateDbContextAsync())
        {
            seedCtx.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                Status = WorkItemStatus.Dispatched,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"issue-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-1",
                AgentSelector = "dotnet,kiro",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = "{}"
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var db = await dbFactory.CreateDbContextAsync();

        // Act
        var (returnedLock, earlyReturn) = await DispatchWorkItemService.TryEnterDispatchAsync(
            CreateGate(lockProvider.Object, dbFactory), db, workItemId, CancellationToken.None);

        // Assert — the lock must be disposed inside the method (not transferred to the caller).
        returnedLock.Should().BeNull(
            "the lock must be disposed inside the method and not transferred to the caller when the item is no longer Pending");
        earlyReturn.Should().NotBeNull(
            "a non-Pending item after lock acquisition must produce a non-null early-return result");
        var ok = earlyReturn as Microsoft.AspNetCore.Http.HttpResults.Ok<DispatchPendingResponse>;
        ok.Should().NotBeNull(
            "the non-Pending early-return must be a 200 Ok<DispatchPendingResponse>");
        ok!.Value.Should().NotBeNull();
        ok.Value!.Dispatched.Should().BeFalse(
            "a deferred result must indicate that dispatch did not occur");
        ok.Value.Reason.Should().Be("not_pending",
            "the reason must match NotPendingReason in DispatchWorkItemService");
        // DisposeAsync must have been called exactly once (the in-try disposal call).
        // If lockTransferred = true is removed before DisposeAsync(), the catch block would
        // call it a second time — this assertion catches that double-dispose regression (#3795).
        lockHandle.Verify(h => h.DisposeAsync(), Times.Once(),
            "the acquired lock must be disposed exactly once inside the method on the non-Pending path");
    }

    // ── PrepareDispatchVariantAsync ───────────────────────────────────────────

    /// <summary>
    /// Regression test for issue #3758 (AC3).
    /// When the work item has a non-null <see cref="WorkItemEntity.ProjectId"/>, and the project
    /// row exists in the database with a <c>Secrets</c> entry in its Settings JSON,
    /// <see cref="WorkItemDispatchEndpoints.PrepareDispatchVariantAsync"/> must return
    /// <c>shouldContinue = true</c> and a non-null <c>projectSecrets</c> dictionary populated
    /// with the project's secrets.
    /// </summary>
    [Fact]
    public async Task PrepareDispatchVariantAsync_WithNonNullProjectId_PopulatesProjectSecretsFromDb()
    {
        // Arrange — seed a ProjectEntity with a Secrets entry in its Settings JSON.
        var projectId = Guid.NewGuid();
        var dbFactory = CreateDbFactory();

        await using (var seedCtx = await dbFactory.CreateDbContextAsync())
        {
            seedCtx.Projects.Add(new ProjectEntity
            {
                Id = projectId,
                Name = "Test Project",
                Enabled = true,
                Settings = "{\"Secrets\":{\"MY_KEY\":\"my_value\"}}"
            });
            await seedCtx.SaveChangesAsync();
        }

        // WorkItemEntity is constructed in memory — PrepareDispatchVariantAsync only reads
        // workItem.ProjectId and calls DispatchLifecycleService.LoadProjectSecretsAsync(db, ...)
        // which queries db.Projects. It never queries db.WorkItems, so no DB insert is needed.
        var workItem = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"secrets-test-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = WorkItemStatus.Pending,
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}",
            ProjectId = projectId
        };

        await using var db = await dbFactory.CreateDbContextAsync();

        // Act
        var (shouldContinue, projectSecrets) = await WorkItemDispatchEndpoints
            .PrepareDispatchVariantAsync(db, workItem, CancellationToken.None);

        // Assert
        shouldContinue.Should().BeTrue("the variant preparation must proceed when ProjectId is set");
        projectSecrets.Should().NotBeNull("a project with a Secrets entry must produce a non-null dictionary");
        projectSecrets!.Should().ContainKey("MY_KEY", "the key from the project's Secrets JSON must be present");
        projectSecrets["MY_KEY"].Should().Be("my_value", "the value must match the project's Secrets JSON");
    }

    /// <summary>
    /// Complementary test: when <see cref="WorkItemEntity.ProjectId"/> is null,
    /// <see cref="WorkItemDispatchEndpoints.PrepareDispatchVariantAsync"/> must return
    /// <c>shouldContinue = true</c> and a null <c>projectSecrets</c> — the project-secrets
    /// loading branch is not entered.
    /// </summary>
    [Fact]
    public async Task PrepareDispatchVariantAsync_WithNullProjectId_ReturnsNullSecrets()
    {
        // Arrange — work item with no project association.
        var workItem = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"no-project-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = WorkItemStatus.Pending,
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}",
            ProjectId = null
        };

        var dbFactory = CreateDbFactory();
        await using var db = await dbFactory.CreateDbContextAsync();

        // Act
        var (shouldContinue, projectSecrets) = await WorkItemDispatchEndpoints
            .PrepareDispatchVariantAsync(db, workItem, CancellationToken.None);

        // Assert
        shouldContinue.Should().BeTrue("null ProjectId is not a failure — dispatch proceeds normally");
        projectSecrets.Should().BeNull("no project is associated — projectSecrets must remain null");
    }

    /// <summary>
    /// Complementary test: when <see cref="WorkItemEntity.ProjectId"/> is non-null but the
    /// referenced project row does not exist in the database, <c>LoadProjectSecretsAsync</c>
    /// returns null via the <c>FirstOrDefaultAsync</c> → <c>settingsJson is null</c> exit path,
    /// and <see cref="WorkItemDispatchEndpoints.PrepareDispatchVariantAsync"/> must therefore
    /// return <c>shouldContinue = true</c> and a null <c>projectSecrets</c>.
    /// A regression that returns an empty dictionary or throws instead of null would be caught here.
    /// </summary>
    [Fact]
    public async Task PrepareDispatchVariantAsync_WithNonNullProjectId_ProjectNotInDb_ReturnsNullSecrets()
    {
        // Arrange — ProjectId points to a GUID that has no corresponding row in the DB.
        var missingProjectId = Guid.NewGuid();
        var workItem = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"missing-project-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = WorkItemStatus.Pending,
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}",
            ProjectId = missingProjectId
        };

        // Empty DB — no ProjectEntity for missingProjectId.
        var dbFactory = CreateDbFactory();
        await using var db = await dbFactory.CreateDbContextAsync();

        // Act
        var (shouldContinue, projectSecrets) = await WorkItemDispatchEndpoints
            .PrepareDispatchVariantAsync(db, workItem, CancellationToken.None);

        // Assert
        shouldContinue.Should().BeTrue("a missing project row is not a hard failure — dispatch proceeds");
        projectSecrets.Should().BeNull(
            "LoadProjectSecretsAsync returns null when FirstOrDefaultAsync finds no project row");
    }

    /// <summary>
    /// AC3 — TimeoutException path unchanged.
    /// When AcquireAsync throws TimeoutException the method must return (null, 503) without
    /// any IAsyncDisposable having been acquired, and the new try/catch must not absorb or
    /// wrap the TimeoutException.
    /// </summary>
    [Fact]
    public async Task TryEnterDispatchAsync_LockAcquisitionTimesOut_Returns503WithoutDispose()
    {
        // Arrange
        var lockProvider = new Mock<IDistributedLockProvider>();
        lockProvider
            .Setup(p => p.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("lock timed out"));

        var dbFactory = CreateDbFactory();
        await using var db = await dbFactory.CreateDbContextAsync();

        // Act
        var (lockHandleResult, earlyReturn) = await DispatchWorkItemService.TryEnterDispatchAsync(
            CreateGate(lockProvider.Object, dbFactory), db, Guid.NewGuid(), CancellationToken.None);

        // Assert
        lockHandleResult.Should().BeNull("no lock was acquired — nothing to dispose");
        earlyReturn.Should().NotBeNull("a lock timeout must produce a non-null early-return result");
        var statusResult = earlyReturn as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull("lock timeout must return a StatusCodeHttpResult");
        statusResult!.StatusCode.Should().Be(503,
            "TimeoutException on lock acquisition maps to 503 Service Unavailable");
    }

    // ── RunDispatchLifecycleAsync ─────────────────────────────────────────────

    /// <summary>
    /// AC1 (issue #3939): When <c>ExecuteDispatchLifecycleAsync</c> throws a non-<see cref="OperationCanceledException"/>
    /// exception, <see cref="DispatchWorkItemService.RunDispatchLifecycleAsync"/> must catch it and
    /// return 503 Service Unavailable — not propagate a 500.
    ///
    /// <para>
    /// Trigger: a <see cref="ThrowingOnSaveDbContext"/> is passed as <c>ctx.Db</c>. Its
    /// <c>SaveChangesAsync</c> throws <see cref="InvalidOperationException"/> on the K8sJobName
    /// pre-write inside <c>ExecuteDispatchLifecycleAsync</c>. The bare <c>catch { throw; }</c>
    /// block in <c>ExecuteDispatchLifecycleAsync</c> re-throws, and
    /// <c>RunDispatchLifecycleAsync</c>'s <c>catch (Exception ex) when (ex is not OperationCanceledException)</c>
    /// catches it and returns 503.
    /// </para>
    ///
    /// <para>
    /// <see cref="IsKiro"/> is set to <c>false</c> (non-kiro template) to skip
    /// <c>SelectPvcAsync</c> and the PVC guard entirely — the exception still propagates from
    /// the <c>SaveChangesAsync</c> pre-write. This avoids PVC-pool and semaphore setup.
    /// </para>
    ///
    /// <para>
    /// The <see cref="ThrowingOnSaveDbContext"/> is opened using the same
    /// <see cref="DbContextOptions{TContext}"/> that the Pending item was seeded into.
    /// Using a fresh options object would cause <c>FindAsync</c> to return <c>null</c>,
    /// which silently exercises the <c>!dispatched</c> 503 branch instead of the exception-catch branch —
    /// both return 503 but only one satisfies the acceptance criterion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunDispatchLifecycleAsync_LifecycleThrowsNonOce_Returns503()
    {
        // Arrange: create raw options so we can share the same in-memory DB between the
        // seeding context and the ThrowingOnSaveDbContext.
        var opts = CreateDbOptions();

        // Seed a Pending item so FindAsync inside ExecuteDispatchLifecycleAsync finds it.
        var workItemId = Guid.NewGuid();
        await using (var seedCtx = new TestPipelineDbContext(opts))
        {
            seedCtx.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"lifecycle-throw-test-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-1",
                Status = WorkItemStatus.Pending,
                AgentSelector = "opencode,python",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = "{}"
            });
            await seedCtx.SaveChangesAsync();
        }

        // Open the throwing context from the SAME options — FindAsync sees the seeded item,
        // then SaveChangesAsync for the K8sJobName pre-write throws.
        await using var throwingCtx = new ThrowingOnSaveDbContext(opts);

        // Non-kiro template — IsKiro = false skips SelectPvcAsync and the PVC null-guard.
        var templateStore = CreateTemplateStore("opencode,python", maxConcurrent: 5, providerType: "opencode");
        var template = ResolveTemplate(templateStore, "opencode,python");

        var projection = new PendingWorkItemProjection
        {
            Id = workItemId,
            AgentSelector = "opencode,python",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"lifecycle-throw-test",
            IssueProviderConfigId = "prov-1",
        };

        var ctx = new DispatchLifecycleContext(
            throwingCtx,
            projection,
            template,
            IsKiro: false,
            AvailablePvcs: [],
            LogPrefix: "test-");

        var lifecycle = CreateLifecycleService();
        var svc = CreateService("opencode,python", providerType: "opencode");

        // Act
        var result = await svc.RunDispatchLifecycleAsync(
            ctx,
            lifecycle,
            onSuccess: _ => TypedResults.Ok(),
            workItemId: workItemId,
            callerName: "TestCaller",
            ct: CancellationToken.None);

        // Assert: the exception-catch path returns 503
        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull("non-OCE exception from the lifecycle must produce a StatusCodeHttpResult");
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable,
            "RunDispatchLifecycleAsync must catch non-OCE exceptions and return 503, not propagate them as 500");

        // Discriminator: confirm the exception-catch branch ran, not the !dispatched branch.
        // Both branches return 503 with an identical StatusCodeHttpResult, so the status code alone
        // cannot distinguish them. WasThrowCalled is true only when SaveChangesAsync was actually
        // called and threw on the ThrowingOnSaveDbContext. If ExecuteDispatchLifecycleAsync were
        // refactored to avoid ctx.Db.SaveChangesAsync (e.g. opening its own context from a factory),
        // WasThrowCalled would be false, dispatched would remain false, and the !dispatched 503 path
        // would be taken — this assertion would fail and signal the regression.
        throwingCtx.WasThrowCalled.Should().BeTrue(
            "the exception-catch path requires SaveChangesAsync to have been invoked and thrown; " +
            "if false, the !dispatched 503 branch ran instead and AC1 is not covered");
    }

    // ── DispatchPendingWorkItemAsync ──────────────────────────────────────────

    /// <summary>
    /// AC2 (issue #3939): When <c>rollout.RouteAsync</c> returns a non-null early result,
    /// <see cref="DispatchWorkItemService.DispatchPendingWorkItemAsync"/> must return that
    /// result directly without dispatching the work item.
    ///
    /// <para>
    /// Setup: a Pending work item is seeded with <c>AgentSelector = ""</c> (no selector)
    /// and a fully serialized <see cref="JobDistributionRequest"/> payload (required to pass
    /// <c>WorkItemPayload.TryDeserialize</c> inside <c>RolloutRouting.RouteAsync</c>).
    /// <c>RolloutRouting</c> is constructed without a seeded routing table; per
    /// <see cref="TestWorkItemRouter"/> docs, every route request is therefore refused with
    /// <c>no_route</c>, making <c>RouteAsync</c> return
    /// <c>(null, Ok(DispatchPendingResponse(false, "no_route")))</c>.
    /// </para>
    ///
    /// <para>
    /// <strong>Why <c>Payload = "{}"</c> is not used:</strong> <c>JobDistributionRequest</c>
    /// has six <c>required</c> C# properties; <c>System.Text.Json</c> throws on missing required
    /// members, so <c>TryDeserialize("{}")</c> returns <c>false</c> and <c>RouteAsync</c> exits
    /// via <c>return ("", null)</c> — not an early result. The test seeds a properly serialized
    /// payload with all required fields and <c>Route = null</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DispatchPendingWorkItemAsync_RouteAsyncReturnsEarlyResult_EarlyResultReturnedWithoutDispatching()
    {
        // Arrange
        var dbFactory = CreateDbFactory();
        var workItemId = Guid.NewGuid();

        // Seed a Pending item with empty AgentSelector and a routable payload.
        // Route = null so RolloutRouting.RouteAsync proceeds to router.RouteAsync.
        // The router refuses (no routing table seeded) → RouteAsync returns the early result.
        await using (var seedDb = await dbFactory.CreateDbContextAsync())
        {
            seedDb.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"rollout-{workItemId:N}",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Pending,
                AgentSelector = "",   // empty → triggers the rollout routing block
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.Serialize(new JobDistributionRequest
                {
                    IssueIdentifier = new IssueIdentifier($"rollout-{workItemId:N}"),
                    IssueProviderConfigId = "ip-1",
                    RepoProviderConfigId = "repo-1",
                    InitiatedBy = "test",
                    TaskType = WorkItemTaskType.Implementation,
                    TimeoutSeconds = 3600,
                    PayloadSchemaVersion = 1,
                    // Route deliberately left null so RouteAsync routes (then refuses)
                }, PipelineJsonOptions.Default)
            });
            await seedDb.SaveChangesAsync();
        }

        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 5);

        // RolloutRouting with NO routing seeded → router refuses all requests → returns early result
        var rollout = CreateRolloutRouting(dbFactory, templateStore);

        var k8sMock = new Mock<IKubernetesJobClient>();
        var lifecycle = CreateLifecycleService(kubeClient: k8sMock.Object);
        var svc = CreateService("kiro,dotnet");

        // Act
        var result = await svc.DispatchPendingWorkItemAsync(
            workItemId, dbFactory, lifecycle, rollout, CancellationToken.None);

        // Assert 1: the early result from RouteAsync is returned directly
        var okResult = result as Microsoft.AspNetCore.Http.HttpResults.Ok<DispatchPendingResponse>;
        okResult.Should().NotBeNull("the early result from RouteAsync must be returned as-is");
        okResult!.Value.Should().NotBeNull();
        okResult.Value!.Dispatched.Should().BeFalse(
            "a refused route must not dispatch the work item");
        okResult.Value.Reason.Should().Be("no_route",
            "RouteAsync refusal produces the 'no_route' reason");

        // Assert 2: work item was transitioned to Failed (RolloutRouting calls transitions.TransitionAsync)
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        item.Should().NotBeNull();
        item!.Status.Should().Be(WorkItemStatus.Failed,
            "RolloutRouting.RouteAsync transitions the item to Failed on route refusal");

        // Assert 3: K8s was never called — dispatch did not reach the lifecycle
        k8sMock.Verify(
            k => k.CreateJobAsync(It.IsAny<k8s.Models.V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never(),
            "the K8s Job must not be created when the early result is returned before dispatch");
    }

    /// <summary>
    /// When a Pending work item has an empty <c>AgentSelector</c> and its payload already
    /// contains a non-null <see cref="AgentRouteSnapshot"/> (<c>Route != null</c>),
    /// <see cref="RolloutRouting.RouteAsync"/> returns <c>("", null)</c> — routing succeeds with
    /// an empty selector and no early-return result.
    ///
    /// <para>
    /// The assignment <c>agentSelector = routedSelector!</c> (and its closing brace) then
    /// execute. That assignment is the empty-selector rollout-routing success branch and was
    /// previously unreachable because all other tests that trigger the rollout path end with
    /// <c>RouteAsync</c> returning a non-null early result (refusal/error) before reaching it.
    /// </para>
    ///
    /// <para>
    /// After the assignment, template resolution finds no template for the empty selector, and
    /// the method returns 200 <c>DispatchPendingResponse(false, "no_template")</c>.
    /// </para>
    /// </summary>
    // TODO [WARNING]: The `agentSelector = routedSelector!` assignment is a no-op in this
    // test: both `agentSelector` (initial "") and `routedSelector` ("", from RolloutRouting's
    // `request.Route is not null` short-circuit) are the same empty string. The test therefore
    // covers the *line* for SonarQube purposes (AC#4) but cannot detect removal of that
    // assignment — omitting the line would leave `agentSelector` as "" and the test would still
    // pass with the same `no_template` outcome. A stronger test would seed a real routing table
    // and use `Route = null` in the payload so `RouteAsync` returns a non-empty selector; the
    // `agentSelector = routedSelector!` assignment would then be observable through the
    // resolved template or the dispatched selector. TestWorkItemRouter.Create requires a seeded
    // routing table (KeyValueStore row), which is more involved setup.
    [Fact]
    public async Task DispatchPendingWorkItemAsync_RolloutRoutingSucceedsWithNonNullRouteInPayload_Returns200NoTemplate()
    {
        // Arrange: seed a Pending item with an empty AgentSelector and a payload that has
        // Route != null. RolloutRouting.RouteAsync checks `request.Route is not null` and,
        // when true, returns ("", null) — routing "succeeds" (no early result) with an empty
        // selector. This causes the `agentSelector = routedSelector!` assignment to execute.
        var dbFactory = CreateDbFactory();
        var workItemId = Guid.NewGuid();
        var issueId = $"rollout-notemplate-{workItemId:N}";

        await using (var seedDb = await dbFactory.CreateDbContextAsync())
        {
            seedDb.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = issueId,
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Pending,
                AgentSelector = "",   // empty → triggers rollout routing
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.Serialize(new JobDistributionRequest
                {
                    IssueIdentifier = new IssueIdentifier(issueId),
                    IssueProviderConfigId = "ip-1",
                    RepoProviderConfigId = "repo-1",
                    InitiatedBy = "test",
                    TaskType = WorkItemTaskType.Implementation,
                    TimeoutSeconds = 3600,
                    PayloadSchemaVersion = 1,
                    // Route is non-null: RolloutRouting takes the `request.Route is not null`
                    // branch and returns ("", null) — the routing-success path with empty selector.
                    Route = new AgentRouteSnapshot("prof-1", "Profile 1", "kiro", "gpt-4o", "medium", AgentRouteSource.Dispatch),
                }, PipelineJsonOptions.Default)
            });
            await seedDb.SaveChangesAsync();
        }

        // Template store has no template for empty selector ("").
        // Template resolution returns null → the method short-circuits to no_template.
        var templateStore = CreateTemplateStore("kiro,dotnet", maxConcurrent: 5);
        var rollout = CreateRolloutRouting(dbFactory, templateStore);
        var lifecycle = CreateLifecycleService();
        var svc = CreateService("kiro,dotnet");

        // Act
        var result = await svc.DispatchPendingWorkItemAsync(
            workItemId, dbFactory, lifecycle, rollout, CancellationToken.None);

        // Assert: no template for the empty selector → 200 no_template deferred response.
        var ok = result as Microsoft.AspNetCore.Http.HttpResults.Ok<DispatchPendingResponse>;
        ok.Should().NotBeNull(
            "when routing returns an empty selector and no template matches, the response must be 200 Ok<DispatchPendingResponse>");
        ok!.Value!.Dispatched.Should().BeFalse(
            "no template found for the empty selector — dispatch must not occur");
        ok.Value.Reason.Should().Be("no_template",
            "RolloutRouting.RouteAsync returned an empty selector, template resolution found null");
    }
}

// ── Test infrastructure ──────────────────────────────────────────────────────

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
/// A <see cref="PipelineDbContext"/> that throws <see cref="InvalidOperationException"/> on every
/// <c>SaveChangesAsync</c> call. Used by
/// <see cref="DispatchWorkItemServiceTests.RunDispatchLifecycleAsync_LifecycleThrowsNonOce_Returns503"/>
/// to trigger the <c>catch (Exception ex) when (ex is not OperationCanceledException)</c> path in
/// <see cref="DispatchWorkItemService.RunDispatchLifecycleAsync"/>.
///
/// <para>
/// Both <c>SaveChangesAsync(CancellationToken)</c> and
/// <c>SaveChangesAsync(bool, CancellationToken)</c> must be overridden: EF Core's default
/// <c>SaveChangesAsync(CancellationToken)</c> delegates to the two-argument overload, so omitting
/// either lets EF bypass the throw via the non-overridden path.
/// </para>
///
/// <para>
/// Inherits from <see cref="PipelineDbContext"/> directly (not <c>TestPipelineDbContext</c>, which is
/// <c>file sealed</c> and therefore cannot be subclassed). The same <c>OnModelCreating</c> overrides
/// (disable RowVersion concurrency token, remove filtered indexes) are replicated here.
/// </para>
/// </summary>
file sealed class ThrowingOnSaveDbContext : PipelineDbContext
{
    public ThrowingOnSaveDbContext(DbContextOptions<PipelineDbContext> opts) : base(opts) { }

    /// <summary>
    /// Set to <c>true</c> the first time any <c>SaveChangesAsync</c> or <c>SaveChanges</c> override
    /// is entered. Asserted by <c>RunDispatchLifecycleAsync_LifecycleThrowsNonOce_Returns503</c>
    /// to confirm the exception-catch path was taken rather than the <c>!dispatched</c> early-exit
    /// path (both return 503 but only the exception-catch path calls <c>SaveChangesAsync</c>).
    /// </summary>
    public bool WasThrowCalled { get; private set; }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        WasThrowCalled = true;
        return Task.FromException<int>(new InvalidOperationException("simulated lifecycle failure"));
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        WasThrowCalled = true;
        return Task.FromException<int>(new InvalidOperationException("simulated lifecycle failure"));
    }

    public override int SaveChanges()
    {
        WasThrowCalled = true;
        throw new InvalidOperationException("simulated lifecycle failure");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        WasThrowCalled = true;
        throw new InvalidOperationException("simulated lifecycle failure");
    }

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
