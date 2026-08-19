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
            ChatSessionMaxDurationSeconds = 7200,
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
    public async Task WhenPvcPoolExhausted_ShouldCallRequeueAsync_NotCreateJob()
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
            KiroPvcPool = [] // empty pool
        };

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            kiroStore, new PvcPool(emptyPoolOptions.KiroPvcPool), emptyPoolOptions);

        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _workItemClient.Verify(c => c.RequeueAsync(ItemId, It.IsAny<CancellationToken>()), Times.Once);
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

    // ─── Secret creation path (Req 8a.3) ─────────────────────────────────────────

    [Fact]
    public async Task WhenClaimSucceeds_ShouldCallCreateSecretAsync_WithOwnerReference()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var jobUid = "test-job-uid-123";
        _k8sClient.Setup(c => c.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Name = DispatchLoop.GenerateJobName(ItemId), Uid = jobUid } });

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        // Secret must be created with ownerReference pointing to the Job UID
        _k8sClient.Verify(c => c.CreateSecretAsync(
            It.Is<V1Secret>(s =>
                s.Metadata.Name == DispatchLoop.GenerateDerivedKeySecretName(ItemId) &&
                s.Metadata.OwnerReferences != null &&
                s.Metadata.OwnerReferences.Any(r => r.Uid == jobUid)),
            _options.Namespace,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenReadJobFails_ShouldNotCallCreateSecretAsync_ButShouldContinueDispatch()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        // ReadJobAsync fails (K8s API error)
        _k8sClient.Setup(c => c.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("K8s API unavailable"));

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        // Job was created — dispatch continues
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        // Secret was NOT created (ReadJobAsync failed)
        _k8sClient.Verify(c => c.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // Must NOT requeue (Job exists and is running)
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenCreateSecretFails_ShouldNotRequeue_JobContinues()
    {
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());
        _k8sClient.Setup(c => c.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Name = DispatchLoop.GenerateJobName(ItemId), Uid = "uid-1" } });
        _k8sClient.Setup(c => c.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("K8s API unavailable for secrets"));

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        // Job was created; Secret creation failed
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        // Agent pod will start without key; ReconciliationService will handle cleanup.
        // Must NOT requeue — Job exists and is running.
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
