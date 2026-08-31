using AwesomeAssertions;
using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.JobController.Reconciliation;
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
    private readonly Mock<IReconciliationTrigger> _reconciliationTrigger = new();
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
            _templateStore, new PvcPool(_options.KiroPvcPool), _options, _reconciliationTrigger.Object);

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
    /// </summary>
    [Fact]
    public async Task WhenPvcPoolExhausted_ShouldNotCallRequeueAsync_AndShouldNotCreateJob()
    {
        // Template with kiro providerType so PVC is required
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
            KiroPvcPool = [] // empty pool — TryClaim returns null
        };

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        // No ClaimAsync setup — PVC check runs before claim, so Moq will throw MockException
        // on any unexpected ClaimAsync call, giving an early and explicit failure signal.

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            kiroStore, new PvcPool(emptyPoolOptions.KiroPvcPool), emptyPoolOptions,
            _reconciliationTrigger.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // PVC unavailability must NOT mutate the item's RetryCount — no requeue call
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // PVC check must happen BEFORE ClaimAsync — if ClaimAsync fires, the item transitions
        // Pending→Dispatched server-side with no K8s Job, stranding it indefinitely (issue #2129).
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenPvcPoolExhausted_ShouldCallRequestImmediateCycle()
    {
        // Template with kiro providerType so PVC is required
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
            KiroPvcPool = [] // empty pool — TryClaim returns null
        };

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());
        // TODO [WARNING]: The ClaimAsync setup above is now dead — the PVC check (TryClaimPvcForKiroAgent)
        // returns early before ClaimAsync is ever reached. A reader would incorrectly conclude ClaimAsync
        // is expected to be called in this path. Remove this setup and add a ClaimAsync: Times.Never
        // verification (as the sibling test WhenPvcPoolExhausted_ShouldNotCallRequeueAsync does) to
        // explicitly document the PVC-before-claim ordering.

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            kiroStore, new PvcPool(emptyPoolOptions.KiroPvcPool), emptyPoolOptions,
            _reconciliationTrigger.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Trigger must fire exactly once — not zero (no-op) and not more than once per cycle
        _reconciliationTrigger.Verify(t => t.RequestImmediateCycle(), Times.Once);
        // No K8s Job should be created
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
            limitedStore, new PvcPool([]), _options, _reconciliationTrigger.Object);

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

    // ─── Concurrency map: completed jobs within retention window ─────────────

    /// <summary>
    /// Regression test for issue #2176.
    /// A completed K8s Job within the 600s log-retention window must NOT consume a concurrency
    /// slot. Only running (non-terminal) jobs count toward the limit.
    ///
    /// Scenario: maxConcurrent=2, one active job + one completed job for the same selector.
    /// Expected: concurrency count = 1 (only the active job, completed job excluded), dispatch proceeds.
    /// The limit is set to 2 so that count=1 leaves headroom, confirming the completed job was
    /// not counted. (If it were counted, count=2 would equal the limit and dispatch would be blocked.)
    /// </summary>
    // TODO: The test proves count=1 indirectly (dispatch proceeds with maxConcurrent=2, implying
    // active count < 2). It does not directly assert count=1 as a numeric value. A complementary
    // test using maxConcurrent=1 with the same two jobs would more directly verify the acceptance
    // criterion: with count=1 == limit=1, ClaimAsync must NOT be called.
    [Fact]
    public async Task WhenOneRunningAndOneCompletedJobForSameSelector_ConcurrencyCountIsOne()
    {
        const string yaml = """
            - labels: dotnet10,opencode
              image: chemsorly/coding-agent:opencode-dotnet10
              providerType: opencode
              maxConcurrent: 2
            """;
        var store = JobTemplateStore.LoadFromYaml(yaml);

        // Running job — no conditions, no succeeded/failed counters
        // TODO: This running job does not set Active = 1, which would reflect the real K8s shape of
        // an active job. If IsJobTerminal were later refactored to also check Active == 0 as a terminal
        // signal, this test would not catch the regression because Active is already null (equivalent to 0).
        // Consider setting Status = new V1JobStatus { Active = 1, Succeeded = 0, Failed = 0 }.
        var runningJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string>
                {
                    ["caa/agent-selector"] = "dotnet10.opencode"
                }
            },
            Status = new V1JobStatus
            {
                Succeeded = 0,
                Failed = 0
            }
        };

        // Completed job — has a "Complete" condition with Status "True"
        var completedJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string>
                {
                    ["caa/agent-selector"] = "dotnet10.opencode"
                }
            },
            Status = new V1JobStatus
            {
                Conditions =
                [
                    new V1JobCondition { Type = "Complete", Status = "True" }
                ],
                Succeeded = 1
            }
        };

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [runningJob, completedJob] });

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,opencode")]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            store, new PvcPool([]), _options, _reconciliationTrigger.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // With maxConcurrent=2 and only 1 truly active job, the item should be claimed and dispatched
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression test for issue #2176 — boundary at the concurrency limit.
    /// When maxConcurrent=1 and the only K8s Job in the list is a completed one (within the
    /// retention window), the concurrency count must be 0 and dispatch must proceed.
    /// </summary>
    [Fact]
    public async Task WhenOnlyJobIsCompleted_ConcurrencyCountIsZero_AndDispatchProceeds()
    {
        const string yaml = """
            - labels: dotnet10,opencode
              image: chemsorly/coding-agent:opencode-dotnet10
              providerType: opencode
              maxConcurrent: 1
            """;
        var store = JobTemplateStore.LoadFromYaml(yaml);

        // Completed job — has a "Complete" condition (like the production scenario from issue #2176)
        var completedJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string>
                {
                    ["caa/agent-selector"] = "dotnet10.opencode"
                }
            },
            Status = new V1JobStatus
            {
                Conditions =
                [
                    new V1JobCondition { Type = "Complete", Status = "True" }
                ],
                Succeeded = 1
            }
        };

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [completedJob] });

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,opencode")]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            store, new PvcPool([]), _options, _reconciliationTrigger.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Completed job must not block dispatch — item should be claimed
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression test for issue #2176 — failed job variant.
    /// A failed K8s Job within the retention window must also not consume a concurrency slot.
    /// </summary>
    [Fact]
    public async Task WhenOnlyJobIsFailed_ConcurrencyCountIsZero_AndDispatchProceeds()
    {
        const string yaml = """
            - labels: dotnet10,opencode
              image: chemsorly/coding-agent:opencode-dotnet10
              providerType: opencode
              maxConcurrent: 1
            """;
        var store = JobTemplateStore.LoadFromYaml(yaml);

        // Failed job — has a "Failed" condition with Status "True"
        var failedJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string>
                {
                    ["caa/agent-selector"] = "dotnet10.opencode"
                }
            },
            Status = new V1JobStatus
            {
                Conditions =
                [
                    new V1JobCondition { Type = "Failed", Status = "True" }
                ],
                Failed = 1
            }
        };

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [failedJob] });

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,opencode")]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            store, new PvcPool([]), _options, _reconciliationTrigger.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Failed job within retention window must not block dispatch
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TODO: Add test for the counter-only fallback path where Status.Failed=1, Status.Active=1,
    // Status.Conditions=null — this is the retrying-job scenario from review finding #3 (issue #2176).
    // IsJobTerminal must return false (job is not terminal), so it should still be counted toward
    // concurrency. A test with maxConcurrent=1 and one such retrying job should assert that
    // ClaimAsync is NOT called (dispatch blocked by the in-flight retry pod).
    // Example: Status = new V1JobStatus { Failed = 1, Active = 1, Conditions = null }

    // TODO: Add test asserting that a truly running job at the concurrency limit blocks dispatch
    // (i.e. ClaimAsync is NOT called). Currently all three #2176 tests only verify terminal jobs
    // do NOT block dispatch; there is no complementary test proving a non-terminal job DOES block.
    // This gap means a regression where IsJobTerminal incorrectly returns true for all jobs would
    // not be caught. Use maxConcurrent=1 with one running job (no conditions, Succeeded=0, Failed=0).
}
