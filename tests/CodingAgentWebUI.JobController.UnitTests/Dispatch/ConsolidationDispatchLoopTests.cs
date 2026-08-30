using AwesomeAssertions;
using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.JobController.Reconciliation;
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
    private readonly Mock<IReconciliationTrigger> _reconciliationTrigger = new();
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
        new(_consolidationClient.Object, _k8sClient.Object, _templateStore, new PvcPool(_options.KiroPvcPool), _options, _reconciliationTrigger.Object);

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
    public async Task WhenPvcPoolExhausted_ShouldRequeueAndNotCreateJob()
    {
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
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var loop = new ConsolidationDispatchLoop(
            _consolidationClient.Object, _k8sClient.Object,
            kiroStore, new PvcPool([]), emptyPoolOptions,
            _reconciliationTrigger.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _consolidationClient.Verify(c => c.RequeueAsync(ItemId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenPvcPoolExhausted_ShouldCallRequestImmediateCycle()
    {
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
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var loop = new ConsolidationDispatchLoop(
            _consolidationClient.Object, _k8sClient.Object,
            kiroStore, new PvcPool([]), emptyPoolOptions,
            _reconciliationTrigger.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Trigger must fire exactly once — not zero (no-op) and not more than once per cycle
        _reconciliationTrigger.Verify(t => t.RequestImmediateCycle(), Times.Once);
        // No K8s Job should be created
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
            limitedStore, new PvcPool([]), _options,
            _reconciliationTrigger.Object);

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

    // ── Math.Max timeout selection ─────────────────────────────────────────────

    [Fact]
    public async Task WhenItemTimeoutExceedsGlobal_K8sJob_ActiveDeadlineSeconds_UsesItemTimeout()
    {
        // item timeout (28800s) > global timeout (7200s) → activeDeadlineSeconds == 28860
        _consolidationClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending(timeoutSeconds: 28800)]);
        _consolidationClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
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
        _consolidationClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending(timeoutSeconds: 3600)]);
        _consolidationClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
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

    // ── Concurrency map: terminal job filtering ────────────────────────────────

    /// <summary>
    /// Acceptance criteria (issue #2176): one running job + one completed job for the same
    /// selector → concurrency count is 1, not 2, so a new work item CAN be dispatched.
    /// </summary>
    [Fact]
    public async Task WhenOneRunningAndOneCompletedJobForSameSelector_ConcurrencyCountIsOne()
    {
        const string yaml = """
            - labels: dotnet10,opencode
              image: chemsorly/coding-agent:opencode-dotnet10
              providerType: opencode
              maxConcurrent: 2
            """;
        var limitedStore = JobTemplateStore.LoadFromYaml(yaml);

        var runningJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string> { ["caa/agent-selector"] = "dotnet10.opencode" }
            },
            Status = new V1JobStatus { Active = 1 }
        };
        var completedJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string> { ["caa/agent-selector"] = "dotnet10.opencode" }
            },
            Status = new V1JobStatus
            {
                Conditions =
                [
                    new V1JobCondition { Type = "Complete", Status = "True" }
                ]
            }
        };

        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [runningJob, completedJob] });

        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,opencode")]);
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var loop = new ConsolidationDispatchLoop(
            _consolidationClient.Object, _k8sClient.Object,
            limitedStore, new PvcPool([]), _options,
            _reconciliationTrigger.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // concurrency = 1 (running only), limit = 2 → should dispatch
        // TODO: maxConcurrent: 2 with one running job means active=1 < limit=2, so dispatch would succeed even
        // without the fix. The test only catches the regression because the completed job pushes count to 2
        // (== limit), which would block dispatch without the filter. This passes but relies on an exact
        // numerical coincidence. Consider rewriting with maxConcurrent: 1 so a single running job already
        // saturates the limit — making the completed job a decisive false +1 that would block dispatch.
        _consolidationClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Counter fallback path: K8s does not always set Conditions on all termination paths.
    /// When Conditions is empty but Succeeded > 0, the job must still be filtered out.
    /// </summary>
    [Fact]
    public async Task WhenJobTerminalViaSucceededCounter_IsNotCountedInConcurrency()
    {
        const string yaml = """
            - labels: dotnet10,opencode
              image: chemsorly/coding-agent:opencode-dotnet10
              providerType: opencode
              maxConcurrent: 1
            """;
        var limitedStore = JobTemplateStore.LoadFromYaml(yaml);

        var succeededJobNoConditions = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string> { ["caa/agent-selector"] = "dotnet10.opencode" }
            },
            Status = new V1JobStatus
            {
                Conditions = [],
                Succeeded = 1
            }
        };

        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [succeededJobNoConditions] });

        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,opencode")]);
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var loop = new ConsolidationDispatchLoop(
            _consolidationClient.Object, _k8sClient.Object,
            limitedStore, new PvcPool([]), _options,
            _reconciliationTrigger.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Succeeded counter → terminal → concurrency = 0 < limit 1 → should dispatch
        _consolidationClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Counter fallback path for failed jobs: when Conditions is empty but Failed > 0,
    /// the job must be filtered out.
    /// </summary>
    [Fact]
    public async Task WhenJobTerminalViaFailedCounter_IsNotCountedInConcurrency()
    {
        const string yaml = """
            - labels: dotnet10,opencode
              image: chemsorly/coding-agent:opencode-dotnet10
              providerType: opencode
              maxConcurrent: 1
            """;
        var limitedStore = JobTemplateStore.LoadFromYaml(yaml);

        var failedJobNoConditions = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string> { ["caa/agent-selector"] = "dotnet10.opencode" }
            },
            Status = new V1JobStatus
            {
                Conditions = [],
                Failed = 1
            }
        };

        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [failedJobNoConditions] });

        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,opencode")]);
        _consolidationClient
            .Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var loop = new ConsolidationDispatchLoop(
            _consolidationClient.Object, _k8sClient.Object,
            limitedStore, new PvcPool([]), _options,
            _reconciliationTrigger.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Failed counter → terminal → concurrency = 0 < limit 1 → should dispatch
        _consolidationClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression guard: a newly created K8s Job with null Status must be treated as active
    /// (counted toward concurrency), not mistakenly filtered out as terminal.
    /// </summary>
    [Fact]
    public async Task WhenJobActiveWithNoStatus_IsCounted()
    {
        const string yaml = """
            - labels: dotnet10,opencode
              image: chemsorly/coding-agent:opencode-dotnet10
              providerType: opencode
              maxConcurrent: 1
            """;
        var limitedStore = JobTemplateStore.LoadFromYaml(yaml);

        var nullStatusJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Labels = new Dictionary<string, string> { ["caa/agent-selector"] = "dotnet10.opencode" }
            }
            // Status intentionally null (newly created job)
        };

        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [nullStatusJob] });

        _consolidationClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,opencode")]);

        var loop = new ConsolidationDispatchLoop(
            _consolidationClient.Object, _k8sClient.Object,
            limitedStore, new PvcPool([]), _options,
            _reconciliationTrigger.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // null-status job counted as active → concurrency = 1 >= limit 1 → must NOT dispatch
        _consolidationClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
