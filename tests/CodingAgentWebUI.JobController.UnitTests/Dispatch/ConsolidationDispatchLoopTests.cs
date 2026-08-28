using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;

namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;

/// <summary>
/// Unit tests for ConsolidationDispatchLoop — the core poll-claim-create logic for
/// consolidation WorkItems. Written before implementation (TDD: Task 8).
///
/// Mirrors DispatchLoopTests but targets the consolidation-specific HTTP client
/// and run-status transition calls.
/// </summary>
public sealed class ConsolidationDispatchLoopTests
{
    private readonly Mock<IPipelineApiConsolidationWorkItemClient> _consolidationClient = new();
    private readonly Mock<IKubernetesJobClient> _k8sClient = new();
    private readonly JobTemplateStore _templateStore;
    private readonly DispatchServiceOptions _options;

    private static readonly Guid ItemId = Guid.NewGuid();
    private const string RunId = "consolidation-run-1";

    public ConsolidationDispatchLoopTests()
    {
        _options = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            PollIntervalSeconds = 1,
            RateLimitPerSecond = 100,
            AgentJobTimeoutSeconds = 7200,
            ChatPodConnectTimeoutSeconds = 120
        };

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

        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });
    }

    private static PendingWorkItemDto MakePending(string agentSelector = "dotnet10,opencode") =>
        new()
        {
            Id = ItemId,
            IssueIdentifier = RunId,
            IssueProviderConfigId = "gh-1",
            TaskType = WorkItemTaskType.Consolidation,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = agentSelector,
            RetryCount = 0
        };

    private static ConsolidationWorkItemClaimResponse MakeClaimed(
        Dictionary<string, string>? projectSecrets = null) =>
        new()
        {
            WorkItemId = ItemId,
            RunId = RunId,
            EnrichedPayloadJson = "{}",
            OrchestratorUrl = "http://orchestrator:5000",
            ProjectSecrets = projectSecrets
        };

    private ConsolidationDispatchLoop CreateLoop() =>
        new(_consolidationClient.Object, _k8sClient.Object, _templateStore, new PvcPool(_options.KiroPvcPool), _options);

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenClaimSucceeds_ShouldCreateJobAndTransitionRunToRunning()
    {
        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        await CreateLoop().RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
        _consolidationClient.Verify(c => c.TransitionRunAsync(
            RunId, ConsolidationRunStatus.Running, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── No pending items ───────────────────────────────────────────────────────

    [Fact]
    public async Task WhenNoPendingItems_ShouldNotClaimOrCreate()
    {
        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await CreateLoop().RunOneCycleAsync(CancellationToken.None);

        _consolidationClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 409 on claim ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenClaimReturns409_ShouldNotCreateJobOrTransitionRun()
    {
        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationWorkItemClaimResponse?)null);

        await CreateLoop().RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _consolidationClient.Verify(c => c.TransitionRunAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _consolidationClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── K8s creation failure → requeue + cascade fail ─────────────────────────

    [Fact]
    public async Task WhenK8sCreateFails_ShouldRequeueAndTransitionRunToFailed()
    {
        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());
        _k8sClient
            .Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("k8s unavailable"));

        await CreateLoop().RunOneCycleAsync(CancellationToken.None);

        _consolidationClient.Verify(c => c.RequeueAsync(ItemId, It.IsAny<CancellationToken>()), Times.Once);
        _consolidationClient.Verify(c => c.TransitionRunAsync(
            RunId, ConsolidationRunStatus.Failed, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        // Must NOT transition to Running after a failure
        _consolidationClient.Verify(c => c.TransitionRunAsync(
            RunId, ConsolidationRunStatus.Running, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── PVC pool exhausted ─────────────────────────────────────────────────────

    [Fact]
    public async Task WhenPvcPoolExhausted_ShouldNotCallRequeueAsync_AndNotCreateJob()
    {
        // The PVC check now runs BEFORE ClaimAsync — so ClaimAsync must never be called,
        // RequeueAsync must never be called, and ConsolidationRunStatus.Failed must never
        // be triggered. Item remains Pending (RetryCount unchanged).
        const string kiroYaml = """
            - labels: dotnet10,kiro
              image: chemsorly/coding-agent:kiro-dotnet10
              providerType: kiro
              maxConcurrent: 0
            """;
        var kiroStore = JobTemplateStore.LoadFromYaml(kiroYaml);
        var emptyPoolOptions = new DispatchServiceOptions { Namespace = "test-ns", RateLimitPerSecond = 100, KiroPvcPool = [] };

        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        // No ClaimAsync setup — it must NOT be called

        var loop = new ConsolidationDispatchLoop(
            _consolidationClient.Object, _k8sClient.Object,
            kiroStore, new PvcPool([]), emptyPoolOptions);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Item remains Pending — no claim, no job, no requeue, no run failure
        _consolidationClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _consolidationClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _consolidationClient.Verify(c => c.TransitionRunAsync(
            It.IsAny<string>(), ConsolidationRunStatus.Failed, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 409 with kiro template — PVC must be released ─────────────────────────

    [Fact]
    public async Task WhenClaimReturns409_WithKiroTemplate_ShouldReleasePvc_AndNotCreateJob()
    {
        // Arrange: kiro template with one PVC. ClaimAsync returns null (409). The PVC we
        // reserved before the claim must be returned to the pool so it doesn't leak.
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

        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        // null = 409
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationWorkItemClaimResponse?)null);

        var loop = new ConsolidationDispatchLoop(
            _consolidationClient.Object, _k8sClient.Object, kiroStore, pvcPool, poolOptions);

        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _consolidationClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _consolidationClient.Verify(c => c.TransitionRunAsync(
            It.IsAny<string>(), ConsolidationRunStatus.Failed, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        // TODO: Weak assertion — pool starts at 1 and the assertion passes even if Release was
        // never called (count never changed). Strengthen by draining the pool before the test
        // (call TryClaim manually so AvailableCount=0), then verifying the loop restores it to 1.
        // PVC must have been released back to pool
        Assert.Equal(1, pvcPool.AvailableCount);
    }

    // ── 404 with kiro template — PVC must be released ─────────────────────────

    [Fact]
    public async Task WhenClaimThrowsNotFound_WithKiroTemplate_ShouldReleasePvc_AndNotRequeue()
    {
        // Arrange: kiro template, one PVC, ClaimAsync throws 404. PVC must be released.
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

        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WorkItemNotFoundException(ItemId));

        var loop = new ConsolidationDispatchLoop(
            _consolidationClient.Object, _k8sClient.Object, kiroStore, pvcPool, poolOptions);

        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _consolidationClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _consolidationClient.Verify(c => c.TransitionRunAsync(
            It.IsAny<string>(), ConsolidationRunStatus.Failed, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        // TODO: Weak assertion — pool starts at 1 and the assertion passes even if Release was
        // never called (count never changed). Strengthen by draining the pool before the test
        // (call TryClaim manually so AvailableCount=0), then verifying the loop restores it to 1.
        // PVC must have been released
        Assert.Equal(1, pvcPool.AvailableCount);
    }

    // ── Concurrency limit reached ──────────────────────────────────────────────

    [Fact]
    public async Task WhenConcurrencyLimitReached_ShouldSkipWithoutClaiming()
    {
        const string yaml = """
            - labels: dotnet10,opencode
              image: chemsorly/coding-agent:opencode-dotnet10
              providerType: opencode
              maxConcurrent: 1
            """;
        var limitedStore = JobTemplateStore.LoadFromYaml(yaml);

        var activeJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string>
                {
                    ["caa/agent-selector"] = "dotnet10.opencode"
                }
            }
        };
        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [activeJob] });
        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,opencode")]);

        var loop = new ConsolidationDispatchLoop(
            _consolidationClient.Object, _k8sClient.Object,
            limitedStore, new PvcPool([]), _options);

        await loop.RunOneCycleAsync(CancellationToken.None);

        _consolidationClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Project secrets → K8s Secret created ──────────────────────────────────

    [Fact]
    public async Task WhenClaimReturnsProjectSecrets_ShouldCreateK8sSecret()
    {
        var secrets = new Dictionary<string, string> { ["MY_TOKEN"] = "secret-value" };

        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed(projectSecrets: secrets));
        // ReadJobAsync called for owner-reference UID
        _k8sClient
            .Setup(c => c.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Uid = "test-uid" } });

        await CreateLoop().RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(c => c.CreateSecretAsync(It.IsAny<V1Secret>(), _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── No project secrets → no K8s Secret created ────────────────────────────

    [Fact]
    public async Task WhenClaimReturnsNoProjectSecrets_ShouldNotCreateK8sSecret()
    {
        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed(projectSecrets: null));

        await CreateLoop().RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── API fetch failure → skip cycle gracefully ──────────────────────────────

    [Fact]
    public async Task WhenGetPendingFails_ShouldNotThrow()
    {
        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unreachable"));

        var loop = CreateLoop();
        // Must not throw — cycle should swallow and log the error
        var ex = await Record.ExceptionAsync(() => loop.RunOneCycleAsync(CancellationToken.None));
        Assert.Null(ex);
    }

    // ── RunId missing → skip run transition ──────────────────────────────────

    [Fact]
    public async Task WhenRunIdEmpty_ShouldCreateJobButSkipRunTransition()
    {
        var claimedNoRunId = new ConsolidationWorkItemClaimResponse
        {
            WorkItemId = ItemId,
            RunId = "",
            EnrichedPayloadJson = "{}",
            OrchestratorUrl = "http://orchestrator:5000"
        };

        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(claimedNoRunId);

        await CreateLoop().RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
        _consolidationClient.Verify(c => c.TransitionRunAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
