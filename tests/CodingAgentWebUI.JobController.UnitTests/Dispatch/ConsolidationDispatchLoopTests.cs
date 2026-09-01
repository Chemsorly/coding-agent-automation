using AwesomeAssertions;
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

    // Shared process-wide PVC selection lock — same singleton that would be injected by DI in production.
    private readonly PvcSelectLock _pvcSelectLock = new();

    private static readonly Guid ItemId = Guid.NewGuid();
    private const string RunId = "consolidation-run-1";

    public ConsolidationDispatchLoopTests()
    {
        _options = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            PollIntervalSeconds = 1,
            RateLimitPerSecond = 100,
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

    private static PendingWorkItemDto MakePending(string agentSelector = "dotnet10,opencode", int timeoutSeconds = 0) =>
        new()
        {
            Id = ItemId,
            IssueIdentifier = RunId,
            IssueProviderConfigId = "gh-1",
            TaskType = WorkItemTaskType.Consolidation,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = agentSelector,
            RetryCount = 0,
            TimeoutSeconds = timeoutSeconds
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
        new(_consolidationClient.Object, _k8sClient.Object, _templateStore, _options, _pvcSelectLock);

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

    /// <summary>
    /// PVC starvation must NOT call RequeueAsync — doing so would increment RetryCount
    /// on every poll cycle (issue #2129). The item stays Pending and is picked up again
    /// on the next cycle once a PVC is released.
    ///
    /// AC (b): all PVCs claimed → item held, no requeue.
    /// </summary>
    [Fact]
    public async Task WhenAllPvcsClaimed_ShouldHoldItemInPending_NoRequeue()
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
            KiroPvcPool = ["kiro-pvc-0", "kiro-pvc-1"]
        };

        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);

        // Both PVCs are claimed by live Jobs
        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList
            {
                Items =
                [
                    MakeJobWithPvc("job-0", "kiro-pvc-0"),
                    MakeJobWithPvc("job-1", "kiro-pvc-1")
                ]
            });

        var loop = new ConsolidationDispatchLoop(
            _consolidationClient.Object, _k8sClient.Object,
            kiroStore, twoVcOptions,
            _pvcSelectLock);

        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // PVC starvation must NOT call RequeueAsync (would increment RetryCount)
        _consolidationClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        // PVC starvation must NOT fire any run-state transition (no claim, no job)
        _consolidationClient.Verify(c => c.TransitionRunAsync(
            It.IsAny<string>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        // PVC check is before ClaimAsync, so no claim should have been attempted
        _consolidationClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// AC (a): PVC available → job created with correct PVC name.
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
            ChatPodConnectTimeoutSeconds = 120,
            KiroPvcPool = ["kiro-pvc-0", "kiro-pvc-1"]
        };

        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        // Only kiro-pvc-0 is claimed; kiro-pvc-1 is free
        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [MakeJobWithPvc("job-0", "kiro-pvc-0")] });

        V1Job? capturedJob = null;
        _k8sClient
            .Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((job, _, _) => capturedJob = job)
            .Returns(Task.CompletedTask);

        var loop = new ConsolidationDispatchLoop(
            _consolidationClient.Object, _k8sClient.Object,
            kiroStore, twoVcOptions,
            _pvcSelectLock);

        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        capturedJob.Should().NotBeNull();
        var mountedPvc = capturedJob!.Spec.Template.Spec.Volumes
            .FirstOrDefault(v => v.PersistentVolumeClaim?.ClaimName is not null)
            ?.PersistentVolumeClaim?.ClaimName;
        mountedPvc.Should().Be("kiro-pvc-1");
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
            limitedStore, _options,
            _pvcSelectLock);

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

    // ── Item timeout drives activeDeadlineSeconds ──────────────────────────────

    [Fact]
    public async Task WhenItemTimeoutSet_K8sJob_ActiveDeadlineSeconds_UsesItemTimeout()
    {
        // item timeout (28800s) → activeDeadlineSeconds == 28860 (28800 + 60 buffer)
        _consolidationClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending(timeoutSeconds: 28800)]);
        _consolidationClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        V1Job? capturedJob = null;
        _k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((job, _, _) => capturedJob = job)
            .Returns(Task.CompletedTask);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        capturedJob.Should().NotBeNull();
        capturedJob!.Spec.ActiveDeadlineSeconds.Should().Be(28860L); // 28800 + 60
    }

    [Fact]
    public async Task WhenItemTimeoutIs900_K8sJob_ActiveDeadlineSeconds_Is960()
    {
        // item timeout (900s = 15 min) → activeDeadlineSeconds == 960 (900 + 60 buffer)
        // Verifies per-project AgentTimeout=15m → activeDeadlineSeconds=960 (acceptance criterion)
        // TODO: Add two sibling tests to complete ConsolidationDispatchLoop timeout coverage:
        // 1. WhenItemTimeoutIsGlobalDefault_K8sJob_ActiveDeadlineSeconds_Is1860 — passes
        //    MakePending(timeoutSeconds: 1800) and asserts activeDeadlineSeconds == 1860L.
        // 2. WhenItemTimeoutIsZero_FallsBackToGlobalDefault_K8sJob_Is1860 — passes
        //    MakePending(timeoutSeconds: 0) and asserts activeDeadlineSeconds == 1860L,
        //    verifying the zero-fallback path for legacy rows.
        // (TestQualityReviewer review [WARNING] @ ConsolidationDispatchLoopTests.cs:417)
        _consolidationClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending(timeoutSeconds: 900)]);
        _consolidationClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        V1Job? capturedJob = null;
        _k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((job, _, _) => capturedJob = job)
            .Returns(Task.CompletedTask);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        capturedJob.Should().NotBeNull();
        capturedJob!.Spec.ActiveDeadlineSeconds.Should().Be(960L); // 900 + 60
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

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
