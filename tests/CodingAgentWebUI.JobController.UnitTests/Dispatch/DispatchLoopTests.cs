using AwesomeAssertions;
using CodingAgentWebUI.JobController.Dispatch;
using k8s.Models;
using System.Collections.Concurrent;

namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;

/// <summary>
/// Unit tests for DispatchLoop — the core poll-claim-create logic.
/// Tests are written before the implementation (TDD: Task 12a).
/// </summary>
public sealed class DispatchLoopTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _workItemClient = new();
    private readonly Mock<IPipelineApiConfigClient> _configClient = new();
    private readonly Mock<IKubernetesJobClient> _k8sClient = new();
    private readonly JobTemplateStore _templateStore;
    private readonly DispatchServiceOptions _options;

    // Shared process-wide PVC selection lock — same singleton that would be injected by DI in production.
    private readonly PvcSelectLock _pvcSelectLock = new();

    private static readonly Guid ItemId = Guid.NewGuid();

    public DispatchLoopTests()
    {
        _options = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            PollIntervalSeconds = 1,
            RateLimitPerSecond = 100,
            AgentJobTimeoutSeconds = 7200,
            ChatPodConnectTimeoutSeconds = 120
        };

        // Single template with no concurrency limit and opencode providerType (no PVC needed)
        const string yaml = """
            - labels: dotnet10,opencode
              image: chemsorly/coding-agent:opencode-dotnet10
              providerType: opencode
              maxConcurrent: 0
              resources:
                requests:
                  cpu: 100m
                  memory: 256Mi
            """;
        _templateStore = JobTemplateStore.LoadFromYaml(yaml);

        // Default: list no active jobs (no concurrency pressure)
        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });
    }

    private static PendingWorkItemDto MakePending(string agentSelector = "dotnet10,opencode", int timeoutSeconds = 0) =>
        new()
        {
            Id = ItemId,
            IssueIdentifier = "owner/repo#1",
            IssueProviderConfigId = "gh-1",
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = agentSelector,
            RetryCount = 0,
            TimeoutSeconds = timeoutSeconds
        };

    private static WorkItemClaimResponse MakeClaimed() =>
        new()
        {
            WorkItemId = ItemId,
            RunId = "run-1",
            PayloadJson = "{}",
            OrchestratorUrl = "http://orchestrator:5000"
        };

    private DispatchLoop CreateLoop() =>
        new(_workItemClient.Object, _configClient.Object, _k8sClient.Object,
            _templateStore, _options, _pvcSelectLock);

    // ─── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenClaimSucceeds_ShouldCallCreateJobAsync()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── 409 on claim ────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenClaimReturns409_ShouldNotCallCreateJobAsync()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        // null return = 409 (already claimed by another instance)
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemClaimResponse?)null);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── K8s creation failure ─────────────────────────────────────────────────

    [Fact]
    public async Task WhenK8sCreateFails_ShouldCallRequeueAsync()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());
        _k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("k8s unavailable"));

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.RequeueAsync(ItemId, It.IsAny<CancellationToken>()), Times.Once);
        // Must not create more than one K8s Job (the one that failed)
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── PVC pool exhausted ───────────────────────────────────────────────────

    // TODO [WARNING]: No tests cover the PVC-release paths when TryClaimWorkItemAsync fails for a kiro
    // agent in DispatchLoop. Two cases need coverage:
    //   1. ClaimAsync throws WorkItemNotFoundException (404) — _pvcPool.Release(pvcName) must be called
    //   2. ClaimAsync returns null (409 contention)          — _pvcPool.Release(pvcName) must be called
    // Without these tests, a future regression that drops either Release call would cause a permanent
    // PVC leak on every 404/409 event and would not be caught by the test suite. ConsolidationDispatchLoopTests
    // has an equivalent TODO comment; DispatchLoopTests should have the same coverage.

    /// <summary>
    /// PVC starvation must NOT call RequeueAsync — the item is already Pending and should
    /// be held there silently until a PVC becomes available. Calling RequeueAsync would
    /// increment RetryCount on every 10s poll cycle, corrupting the field (issue #2129).
    /// AC (b): all PVCs claimed → item held, no requeue.
    /// </summary>
    [Fact]
    public async Task WhenAllPvcsClaimed_ShouldHoldItemInPending_NoRequeue()
    {
        // Template with kiro providerType so PVC is required
        const string kiroYaml = """
            - labels: dotnet10,kiro
              image: chemsorly/coding-agent:kiro-dotnet10
              providerType: kiro
              maxConcurrent: 0
            """;
        var kiroStore = JobTemplateStore.LoadFromYaml(kiroYaml);
        var twoVcOptions = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            RateLimitPerSecond = 100,
            KiroPvcPool = ["kiro-pvc-0", "kiro-pvc-1"]
        };

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);

        // Both PVCs are claimed by live Jobs
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList
            {
                Items =
                [
                    MakeJobWithPvc("job-0", "kiro-pvc-0"),
                    MakeJobWithPvc("job-1", "kiro-pvc-1")
                ]
            });

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            kiroStore, twoVcOptions, _pvcSelectLock);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // PVC unavailability must NOT mutate the item's RetryCount — no requeue call
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // PVC check must happen BEFORE ClaimAsync — if ClaimAsync fires, the item transitions
        // Pending→Dispatched server-side with no K8s Job, stranding it indefinitely (issue #2129).
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// AC (a): PVC available → job created with correct PVC name.
    /// When one of two configured PVCs is already claimed by a live Job, the loop
    /// must create the K8s Job using the remaining free PVC name.
    /// </summary>
    [Fact]
    public async Task WhenPvcAvailable_ShouldCreateJobWithCorrectPvcName()
    {
        const string kiroYaml = """
            - labels: dotnet10,kiro
              image: chemsorly/coding-agent:kiro-dotnet10
              providerType: kiro
              maxConcurrent: 0
            """;
        var kiroStore = JobTemplateStore.LoadFromYaml(kiroYaml);
        var twoVcOptions = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            RateLimitPerSecond = 100,
            AgentJobTimeoutSeconds = 7200,
            ChatPodConnectTimeoutSeconds = 120,
            KiroPvcPool = ["kiro-pvc-0", "kiro-pvc-1"]
        };

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        // Only kiro-pvc-0 is claimed; kiro-pvc-1 is free
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [MakeJobWithPvc("job-0", "kiro-pvc-0")] });

        V1Job? capturedJob = null;
        _k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((job, _, _) => capturedJob = job)
            .Returns(Task.CompletedTask);

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            kiroStore, twoVcOptions, _pvcSelectLock);

        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        capturedJob.Should().NotBeNull();
        // The Job must mount kiro-pvc-1 (the free one) — not kiro-pvc-0 (already claimed)
        var mountedPvc = capturedJob!.Spec.Template.Spec.Volumes
            .FirstOrDefault(v => v.PersistentVolumeClaim?.ClaimName is not null)
            ?.PersistentVolumeClaim?.ClaimName;
        mountedPvc.Should().Be("kiro-pvc-1");
    }

    /// <summary>
    /// AC (c): Intra-cycle sequential dispatch picks distinct PVCs.
    /// A single loop instance processing two pending kiro items in one cycle must assign
    /// distinct PVCs to each item. The SemaphoreSlim(1,1) wraps SelectAvailablePvcAsync +
    /// CreateJobAsync so item2's availability query runs after item1's Job is already created
    /// — item2 therefore sees kiro-pvc-0 as taken and selects kiro-pvc-1.
    ///
    /// This is the exact production scenario: DispatchService calls RunOneCycleAsync once per
    /// poll interval and the foreach processes items one by one on the same thread. Each item
    /// acquires the semaphore, queries K8s, creates the Job, then releases — giving the next
    /// item an accurate view of which PVCs are now in use.
    /// </summary>
    [Fact]
    public async Task ConcurrentDispatch_PicksDistinctPvcs()
    {
        const string kiroYaml = """
            - labels: dotnet10,kiro
              image: chemsorly/coding-agent:kiro-dotnet10
              providerType: kiro
              maxConcurrent: 0
            """;
        var kiroStore = JobTemplateStore.LoadFromYaml(kiroYaml);
        var twoVcOptions = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            RateLimitPerSecond = 100,
            AgentJobTimeoutSeconds = 7200,
            ChatPodConnectTimeoutSeconds = 120,
            KiroPvcPool = ["kiro-pvc-0", "kiro-pvc-1"]
        };

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        // Single loop instance — two items in the same cycle (the foreach processes them sequentially)
        var workItemClient = new Mock<IPipelineApiWorkItemClient>();
        var configClient = new Mock<IPipelineApiConfigClient>();
        var k8sClient = new Mock<IKubernetesJobClient>();

        var item1 = new PendingWorkItemDto { Id = id1, IssueIdentifier = "o/r#1", IssueProviderConfigId = "gh-1", TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "dotnet10,kiro", RetryCount = 0, TimeoutSeconds = 0 };
        var item2 = new PendingWorkItemDto { Id = id2, IssueIdentifier = "o/r#2", IssueProviderConfigId = "gh-1", TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "dotnet10,kiro", RetryCount = 0, TimeoutSeconds = 0 };

        // Return both items in the same cycle
        workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item1, item2]);
        workItemClient.Setup(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        // Stateful K8s mock: CreateJobAsync populates mountedPvcs; ListJobsAsync returns current state.
        // Because the foreach is sequential (no concurrency within a cycle), item1's CreateJobAsync
        // completes before item2's SelectAvailablePvcAsync queries — so item2 always sees pvc-0 taken.
        var mountedPvcs = new List<string>();
        k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, CancellationToken _) =>
            {
                var items = mountedPvcs.Select(pvc => MakeJobWithPvc($"job-{pvc}", pvc)).ToList();
                return Task.FromResult(new V1JobList { Items = items });
            });

        var capturedPvcs = new List<string>();
        k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((job, _, _) =>
            {
                var pvc = job.Spec.Template.Spec.Volumes
                    .FirstOrDefault(v => v.PersistentVolumeClaim?.ClaimName is not null)
                    ?.PersistentVolumeClaim?.ClaimName;
                if (pvc is not null)
                {
                    mountedPvcs.Add(pvc);  // Update state so next item's ListJobsAsync sees this PVC as taken
                    capturedPvcs.Add(pvc);
                }
            })
            .Returns(Task.CompletedTask);

        var loop = new DispatchLoop(workItemClient.Object, configClient.Object, k8sClient.Object,
            kiroStore, twoVcOptions, _pvcSelectLock);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Both items must be dispatched with distinct PVCs.
        // item1 sees empty K8s → picks kiro-pvc-0.
        // item2 sees kiro-pvc-0 mounted → picks kiro-pvc-1.
        capturedPvcs.Should().HaveCount(2, "both items in the cycle must create a Job");
        capturedPvcs.Distinct().Should().HaveCount(2, "each item must be assigned a distinct PVC");
        capturedPvcs.Should().Contain("kiro-pvc-0").And.Contain("kiro-pvc-1");
    }

    /// <summary>
    /// Intra-instance TOCTOU: two concurrent RunOneCycleAsync calls on the same loop
    /// instance must not both select kiro-pvc-0. The SemaphoreSlim(1,1) ensures that
    /// cycle 2's SelectAvailablePvcAsync + CreateJobAsync block runs only after cycle 1's
    /// completes, so cycle 2 sees kiro-pvc-0 as already mounted and selects kiro-pvc-1.
    ///
    /// Setup: A single DispatchLoop processes one item per cycle (GetPendingAsync returns
    /// different items on first and second call). CreateJobAsync is blocked via a
    /// TaskCompletionSource while cycle 1 holds the semaphore — cycle 2 must wait at
    /// WaitAsync. After the TCS is signalled (cycle 1 finishes CreateJobAsync and releases
    /// the lock), cycle 2 proceeds, queries K8s (which now shows kiro-pvc-0 as mounted),
    /// and selects kiro-pvc-1.
    /// </summary>
    [Fact]
    public async Task SameLoop_ConcurrentCycles_SemaphoreSerializesSelection()
    {
        const string kiroYaml = """
            - labels: dotnet10,kiro
              image: chemsorly/coding-agent:kiro-dotnet10
              providerType: kiro
              maxConcurrent: 0
            """;
        var kiroStore = JobTemplateStore.LoadFromYaml(kiroYaml);
        var twoVcOptions = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            RateLimitPerSecond = 100,
            AgentJobTimeoutSeconds = 7200,
            ChatPodConnectTimeoutSeconds = 120,
            KiroPvcPool = ["kiro-pvc-0", "kiro-pvc-1"]
        };

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        // Single loop instance — two concurrent RunOneCycleAsync calls, each sees one item.
        // GetPendingAsync returns item1 on first call, item2 on second call.
        var workItemClient = new Mock<IPipelineApiWorkItemClient>();
        var configClient = new Mock<IPipelineApiConfigClient>();
        var k8sClient = new Mock<IKubernetesJobClient>();

        workItemClient.SetupSequence(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PendingWorkItemDto { Id = id1, IssueIdentifier = "o/r#1", IssueProviderConfigId = "gh-1", TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "dotnet10,kiro", RetryCount = 0, TimeoutSeconds = 0 }])
            .ReturnsAsync([new PendingWorkItemDto { Id = id2, IssueIdentifier = "o/r#2", IssueProviderConfigId = "gh-1", TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "dotnet10,kiro", RetryCount = 0, TimeoutSeconds = 0 }]);
        workItemClient.Setup(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        // Stateful K8s mock: CreateJobAsync adds pvc to mountedPvcs; ListJobsAsync returns current state.
        var mountedPvcs = new ConcurrentBag<string>();
        var capturedPvcs = new ConcurrentBag<string>();

        // TCS used to hold cycle 1's CreateJobAsync inside the semaphore until we release it.
        // This guarantees cycle 2 is blocked at _pvcSelectLock.WaitAsync while cycle 1 holds the lock.
        var firstJobCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Signal when cycle 2 has started waiting for the semaphore (optional — used to prove ordering)
        var cycle2ReachedLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, CancellationToken _) =>
            {
                var items = mountedPvcs.Select(pvc => MakeJobWithPvc($"job-{pvc}", pvc)).ToList();
                return Task.FromResult(new V1JobList { Items = items });
            });

        k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (V1Job job, string _, CancellationToken _) =>
            {
                var pvc = job.Spec.Template.Spec.Volumes
                    .FirstOrDefault(v => v.PersistentVolumeClaim?.ClaimName is not null)
                    ?.PersistentVolumeClaim?.ClaimName;
                if (pvc is not null)
                {
                    // If this is the first job: hold the semaphore (by delaying CreateJobAsync) until
                    // we signal release. This lets us confirm cycle 2 is blocked before we proceed.
                    if (mountedPvcs.IsEmpty)
                    {
                        // Signal that cycle 2 should now try to acquire the lock concurrently.
                        // Wait until the test confirms cycle 2 is pending before releasing.
                        cycle2ReachedLock.TrySetResult();
                        await firstJobCreated.Task; // hold the semaphore here
                    }
                    mountedPvcs.Add(pvc);
                    capturedPvcs.Add(pvc);
                }
            });

        var loop = new DispatchLoop(workItemClient.Object, configClient.Object, k8sClient.Object,
            kiroStore, twoVcOptions, _pvcSelectLock);

        // Start cycle 1 — it will enter the semaphore and block at CreateJobAsync
        var cycle1 = loop.RunOneCycleAsync(CancellationToken.None);

        // Wait until cycle 1 is inside CreateJobAsync (holding the semaphore), then start cycle 2
        await cycle2ReachedLock.Task;
        var cycle2 = loop.RunOneCycleAsync(CancellationToken.None);

        // Give cycle 2 a moment to reach _pvcSelectLock.WaitAsync (it must block there)
        await Task.Delay(50);

        // Release cycle 1's CreateJobAsync — it will add pvc-0 to mountedPvcs and release the semaphore
        firstJobCreated.SetResult();

        await Task.WhenAll(cycle1, cycle2);

        // cycle 1 must have created a Job with kiro-pvc-0 (first available PVC).
        // cycle 2, after acquiring the semaphore, queries K8s and sees kiro-pvc-0 mounted → picks kiro-pvc-1.
        capturedPvcs.Should().HaveCount(2, "both cycles must create exactly one Job each");
        capturedPvcs.Distinct().Should().HaveCount(2, "the semaphore must prevent both cycles from selecting the same PVC");
        capturedPvcs.Should().Contain("kiro-pvc-0").And.Contain("kiro-pvc-1");
    }

    /// <summary>
    /// Cross-loop TOCTOU: a DispatchLoop and a ConsolidationDispatchLoop processing kiro items
    /// concurrently must assign distinct PVCs because they share the same <see cref="PvcSelectLock"/>
    /// singleton. Without the shared lock, both loops could observe the same free PVC and issue
    /// CreateJobAsync calls with the same PVC, causing a credential conflict at runtime.
    ///
    /// The test uses the same blocking pattern as SameLoop_ConcurrentCycles_SemaphoreSerializesSelection:
    /// DispatchLoop holds the lock inside CreateJobAsync; ConsolidationDispatchLoop blocks at
    /// _pvcSelectLock.WaitAsync. After the first Job is created, the consolidation loop acquires the
    /// lock, queries K8s (sees kiro-pvc-0 mounted), and selects kiro-pvc-1.
    /// </summary>
    [Fact]
    public async Task CrossLoop_DispatchAndConsolidation_SharedLock_PicksDistinctPvcs()
    {
        const string kiroYaml = """
            - labels: dotnet10,kiro
              image: chemsorly/coding-agent:kiro-dotnet10
              providerType: kiro
              maxConcurrent: 0
            """;
        var kiroStore = JobTemplateStore.LoadFromYaml(kiroYaml);
        var twoVcOptions = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            RateLimitPerSecond = 100,
            AgentJobTimeoutSeconds = 7200,
            ChatPodConnectTimeoutSeconds = 120,
            KiroPvcPool = ["kiro-pvc-0", "kiro-pvc-1"]
        };

        var dispatchItemId = Guid.NewGuid();
        var consolidationItemId = Guid.NewGuid();

        var workItemClient = new Mock<IPipelineApiWorkItemClient>();
        var configClient = new Mock<IPipelineApiConfigClient>();
        var consolidationClient = new Mock<IPipelineApiConsolidationWorkItemClient>();
        var k8sClient = new Mock<IKubernetesJobClient>();

        workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PendingWorkItemDto { Id = dispatchItemId, IssueIdentifier = "o/r#1", IssueProviderConfigId = "gh-1", TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "dotnet10,kiro", RetryCount = 0, TimeoutSeconds = 0 }]);
        workItemClient.Setup(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        consolidationClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PendingWorkItemDto { Id = consolidationItemId, IssueIdentifier = "consolidation-run-1", IssueProviderConfigId = "gh-1", TaskType = WorkItemTaskType.Consolidation, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "dotnet10,kiro", RetryCount = 0, TimeoutSeconds = 0 }]);
        consolidationClient.Setup(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationWorkItemClaimResponse { WorkItemId = consolidationItemId, RunId = "run-1", EnrichedPayloadJson = "{}", OrchestratorUrl = "http://orchestrator:5000" });

        // Stateful K8s mock shared by both loops
        var mountedPvcs = new ConcurrentBag<string>();
        var capturedPvcs = new ConcurrentBag<string>();

        var dispatchJobCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchHoldsLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, CancellationToken _) =>
            {
                var items = mountedPvcs.Select(pvc => MakeJobWithPvc($"job-{pvc}", pvc)).ToList();
                return Task.FromResult(new V1JobList { Items = items });
            });

        k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (V1Job job, string _, CancellationToken _) =>
            {
                var pvc = job.Spec.Template.Spec.Volumes
                    .FirstOrDefault(v => v.PersistentVolumeClaim?.ClaimName is not null)
                    ?.PersistentVolumeClaim?.ClaimName;
                if (pvc is not null)
                {
                    if (mountedPvcs.IsEmpty)
                    {
                        // DispatchLoop holds the shared lock here — signal consolidation loop can try
                        dispatchHoldsLock.TrySetResult();
                        await dispatchJobCreated.Task; // block inside the critical section
                    }
                    mountedPvcs.Add(pvc);
                    capturedPvcs.Add(pvc);
                }
            });

        // Both loops share the SAME PvcSelectLock — this is the fix under test.
        var sharedLock = new PvcSelectLock();
        var dispatchLoop = new DispatchLoop(workItemClient.Object, configClient.Object, k8sClient.Object,
            kiroStore, twoVcOptions, sharedLock);
        var consolidationLoop = new ConsolidationDispatchLoop(consolidationClient.Object, k8sClient.Object,
            kiroStore, twoVcOptions, sharedLock);

        // Start DispatchLoop — it enters the lock and blocks at CreateJobAsync
        var dispatchCycle = dispatchLoop.RunOneCycleAsync(CancellationToken.None);

        // Wait until DispatchLoop holds the lock, then start ConsolidationDispatchLoop
        await dispatchHoldsLock.Task;
        var consolidationCycle = consolidationLoop.RunOneCycleAsync(CancellationToken.None);

        // Give ConsolidationDispatchLoop a moment to reach _pvcSelectLock.WaitAsync (must block)
        await Task.Delay(50);

        // Release DispatchLoop's CreateJobAsync — it mounts kiro-pvc-0 and releases the lock
        dispatchJobCreated.SetResult();

        await Task.WhenAll(dispatchCycle, consolidationCycle);

        capturedPvcs.Should().HaveCount(2, "both loops must each create exactly one Job");
        capturedPvcs.Distinct().Should().HaveCount(2, "the shared lock must prevent both loops from selecting the same PVC");
        capturedPvcs.Should().Contain("kiro-pvc-0").And.Contain("kiro-pvc-1");
    }

    // ─── Concurrency limit reached ────────────────────────────────────────────

    [Fact]
    public async Task WhenConcurrencyLimitReached_ShouldSkipItem_NoClaim()
    {
        const string yaml = """
            - labels: dotnet10,opencode
              image: chemsorly/coding-agent:opencode-dotnet10
              providerType: opencode
              maxConcurrent: 1
            """;
        var limitedStore = JobTemplateStore.LoadFromYaml(yaml);

        // Return 1 active job with matching agent selector label
        var activeJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string>
                {
                    ["caa/agent-selector"] = "dotnet10.opencode" // normalized (dots not commas)
                }
            }
        };
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [activeJob] });

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,opencode")]);

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            limitedStore, _options, _pvcSelectLock);

        await loop.RunOneCycleAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Label swap failure — non-fatal ──────────────────────────────────────

    [Fact]
    public async Task WhenLabelSwapFails_ShouldNotCallRequeueAsync()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());
        _workItemClient.Setup(c => c.PostLabelSwapAsync(ItemId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("label swap failed"));

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        // Job was already created — must NOT requeue
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        // Job must have been created
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Startup validation ───────────────────────────────────────────────────

    [Fact]
    public async Task OnStartup_ShouldCallGetAgentProfilesAsync()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _configClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        // First poll cycle triggers startup validation
        await loop.RunOneCycleAsync(CancellationToken.None);

        _configClient.Verify(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Empty pending — no work ──────────────────────────────────────────────

    [Fact]
    public async Task WhenNoPendingItems_ShouldNotClaimOrCreate()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Key delivery ────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatch must create the Job and nothing else — specifically, no per-Job derived-key Secret.
    ///
    /// Spec 043 Req 8a.3 originally had the dispatcher derive HMAC(master, jobName), write it to a
    /// Job-owned Secret, and mount that into the pod. That was removed because the agent derives
    /// the same key itself at runtime — <c>HubConnectionManager.DeriveKey</c> and
    /// <c>WorkItemHttpClient</c> both call HMAC(AGENT_API_KEY, AGENT_ID) — so a pre-derived key
    /// arriving in the pod got derived a second time and failed auth. Work-item pods now receive
    /// the master key via the file mount instead (<c>DerivedKeySecretName</c> stays null in the
    /// build context).
    ///
    /// This is the guard against re-introducing that double-derivation: three tests used to assert
    /// the Secret was written, and they are what caught the removal.
    /// </summary>
    [Fact]
    public async Task WhenClaimSucceeds_ShouldCreateJob_ButNoDerivedKeySecret()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(c => c.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // The Job read-back existed only to get a UID for the Secret's ownerReference. With no
        // Secret to own, dispatch should not be paying for that call either.
        _k8sClient.Verify(c => c.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Job is running, so nothing goes back on the queue.
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── No Job Template for selector ────────────────────────────────────────

    [Fact]
    public async Task WhenNoTemplateForSelector_ShouldNotClaimOrCreate()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("unknown,selector")]);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── GetPendingAsync failure ──────────────────────────────────────────────

    [Fact]
    public async Task WhenGetPendingThrows_ShouldReturnWithoutClaiming()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unreachable"));

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── WorkItemNotFoundException (404 race) ─────────────────────────────────

    [Fact]
    public async Task WhenClaimThrowsNotFound_ShouldSkipWithoutRequeue()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WorkItemNotFoundException(ItemId));

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Startup validation: profile with no matching template ───────────────

    [Fact]
    public async Task OnStartup_ProfileWithNoMatchingTemplate_ShouldLogWarningAndContinue()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _configClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AgentProfile { Id = "p-1", DisplayName = "Orphan", AgentProviderConfigId = "ap-1", MatchLabels = ["kiro", "orphan"] }
            ]);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _configClient.Verify(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Startup validation throws ────────────────────────────────────────────

    [Fact]
    public async Task OnStartup_WhenGetAgentProfilesThrows_ShouldContinue()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _configClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unreachable"));

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);
    }

    // ─── Label swap failure (non-fatal) ──────────────────────────────────────

    [Fact]
    public async Task WhenLabelSwapFails_ShouldNotRequeue_JobAlreadyRunning()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());
        _workItemClient.Setup(c => c.PostLabelSwapAsync(ItemId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("label swap failed"));

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── SafeRequeueAsync failure ─────────────────────────────────────────────

    [Fact]
    public async Task WhenK8sCreateFailsAndRequeueFails_ShouldNotThrow()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());
        _k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), _options.Namespace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("K8s unavailable"));
        _workItemClient.Setup(c => c.RequeueAsync(ItemId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("requeue also failed"));

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);
    }

    /// <summary>
    /// When K8s fails and the real client receives a 409 from /requeue, it now returns
    /// normally (no throw). This test verifies that SafeRequeueAsync's catch block —
    /// which contains Log.Error — is structurally unreachable for that path, because
    /// RequeueAsync completes without throwing.
    ///
    /// Together with RequeueAsync_OnConflict_DoesNotThrow in Pipeline.UnitTests, this forms
    /// the complete proof: client silences 409 → SafeRequeueAsync catch never fires → Log.Error
    /// is never called for a 409 response.
    /// </summary>
    [Fact]
    public async Task WhenK8sCreateFailsAndRequeueReturns409Silently_ShouldNotThrow()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());
        _k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("K8s unavailable"));
        // RequeueAsync returns without throwing (real impl now handles 409 as no-op)
        _workItemClient.Setup(c => c.RequeueAsync(ItemId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var loop = CreateLoop();

        // Must not throw. Because RequeueAsync doesn't throw, SafeRequeueAsync's catch block
        // (which contains Log.Error) is never entered.
        await loop.RunOneCycleAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.RequeueAsync(ItemId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Math.Max timeout selection ───────────────────────────────────────────

    [Fact]
    public async Task WhenItemTimeoutExceedsGlobal_K8sJob_ActiveDeadlineSeconds_UsesItemTimeout()
    {
        // item timeout (28800s) > global timeout (7200s) → activeDeadlineSeconds == 28860
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending(timeoutSeconds: 28800)]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        V1Job? capturedJob = null;
        _k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((job, _, _) => capturedJob = job)
            .Returns(Task.CompletedTask);

        var loop = CreateLoop(); // _options.AgentJobTimeoutSeconds == 7200
        await loop.RunOneCycleAsync(CancellationToken.None);

        capturedJob.Should().NotBeNull();
        capturedJob!.Spec.ActiveDeadlineSeconds.Should().Be(28860L); // 28800 + 60
    }

    [Fact]
    public async Task WhenGlobalTimeoutExceedsItem_K8sJob_ActiveDeadlineSeconds_UsesGlobalTimeout()
    {
        // item timeout (3600s) < global timeout (7200s) → activeDeadlineSeconds == 7260
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending(timeoutSeconds: 3600)]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        V1Job? capturedJob = null;
        _k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((job, _, _) => capturedJob = job)
            .Returns(Task.CompletedTask);

        var loop = CreateLoop(); // _options.AgentJobTimeoutSeconds == 7200
        await loop.RunOneCycleAsync(CancellationToken.None);

        capturedJob.Should().NotBeNull();
        capturedJob!.Spec.ActiveDeadlineSeconds.Should().Be(7260L); // 7200 + 60
    }

    // TODO: Add equal-values edge case test — WhenItemTimeoutEqualsGlobal_K8sJob_ActiveDeadlineSeconds_UsesSharedTimeout
    // where item.TimeoutSeconds == agentJobTimeoutSeconds (e.g. both 7200s) → activeDeadlineSeconds == 7260.
    // This boundary condition confirms that floor and ceiling converge cleanly at the same value.

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static V1Job MakeJobWithPvc(string jobName, string pvcName) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = jobName },
            Spec = new V1JobSpec
            {
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        Volumes =
                        [
                            new V1Volume
                            {
                                Name = "kiro-data",
                                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = pvcName }
                            }
                        ]
                    }
                }
            },
            Status = new V1JobStatus { Active = 1 }
        };
}
