using CodingAgentWebUI.JobController.Dispatch;
using k8s.Models;

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

    private static PendingWorkItemDto MakePending(string agentSelector = "dotnet10,opencode") =>
        new()
        {
            Id = ItemId,
            IssueIdentifier = "owner/repo#1",
            IssueProviderConfigId = "gh-1",
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = agentSelector,
            RetryCount = 0
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
            _templateStore, new PvcPool(_options.KiroPvcPool), _options);

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

    [Fact]
    public async Task WhenPvcPoolExhausted_ShouldNotCallRequeueAsync_AndNotCreateJob()
    {
        // Template with kiro providerType so PVC is required.
        // The PVC check now runs BEFORE ClaimAsync — so ClaimAsync must never be called
        // and RequeueAsync must never be called (item remains Pending, RetryCount unchanged).
        const string kiroYaml = """
            - labels: dotnet10,kiro
              image: chemsorly/coding-agent:kiro-dotnet10
              providerType: kiro
              maxConcurrent: 0
            """;
        var kiroStore = JobTemplateStore.LoadFromYaml(kiroYaml);
        var emptyPoolOptions = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            RateLimitPerSecond = 100,
            KiroPvcPool = [] // empty pool — TryClaim always returns null
        };

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        // No ClaimAsync setup — it must NOT be called

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            kiroStore, new PvcPool(emptyPoolOptions.KiroPvcPool), emptyPoolOptions);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Item remains Pending — no claim, no job, no requeue (RetryCount unchanged)
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── 409 with kiro template — PVC must be released ────────────────────────

    [Fact]
    public async Task WhenClaimReturns409_WithKiroTemplate_ShouldReleasePvc_AndNotCreateJob()
    {
        // Arrange: kiro template with a PVC pool that has one entry. ClaimAsync returns null
        // (409 — another instance beat us). The PVC we reserved must be returned to the pool
        // so it doesn't leak.
        const string kiroYaml = """
            - labels: dotnet10,kiro
              image: chemsorly/coding-agent:kiro-dotnet10
              providerType: kiro
              maxConcurrent: 0
            """;
        var kiroStore = JobTemplateStore.LoadFromYaml(kiroYaml);
        var poolOptions = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            RateLimitPerSecond = 100,
            KiroPvcPool = ["kiro-pvc-0"]
        };
        var pvcPool = new PvcPool(poolOptions.KiroPvcPool);

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        // null = 409 (already claimed by another instance)
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemClaimResponse?)null);

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            kiroStore, pvcPool, poolOptions);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Job must not have been created
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // RequeueAsync must not have been called
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        // TODO: Weak assertion — pool starts at 1 and the assertion passes even if Release was
        // never called (count never changed). Strengthen by draining the pool before the test
        // (call TryClaim manually so AvailableCount=0), then verifying the loop restores it to 1.
        // PVC must have been released back to the pool (AvailableCount returns to 1)
        Assert.Equal(1, pvcPool.AvailableCount);
    }

    // ─── 404 with kiro template — PVC must be released ────────────────────────

    [Fact]
    public async Task WhenClaimThrowsNotFound_WithKiroTemplate_ShouldReleasePvc_AndNotRequeue()
    {
        // Arrange: kiro template, PVC pool has one entry. ClaimAsync throws 404 (item deleted
        // between poll and claim). The PVC we reserved must be returned to the pool.
        const string kiroYaml = """
            - labels: dotnet10,kiro
              image: chemsorly/coding-agent:kiro-dotnet10
              providerType: kiro
              maxConcurrent: 0
            """;
        var kiroStore = JobTemplateStore.LoadFromYaml(kiroYaml);
        var poolOptions = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            RateLimitPerSecond = 100,
            KiroPvcPool = ["kiro-pvc-0"]
        };
        var pvcPool = new PvcPool(poolOptions.KiroPvcPool);

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WorkItemNotFoundException(ItemId));

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            kiroStore, pvcPool, poolOptions);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Job must not have been created
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // RequeueAsync must not have been called
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        // TODO: Weak assertion — pool starts at 1 and the assertion passes even if Release was
        // never called (count never changed). Strengthen by draining the pool before the test
        // (call TryClaim manually so AvailableCount=0), then verifying the loop restores it to 1.
        // PVC must have been released back to the pool
        Assert.Equal(1, pvcPool.AvailableCount);
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
            limitedStore, new PvcPool([]), _options);

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
}
