using AwesomeAssertions;
using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using k8s.Models;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

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

    // Shared process-wide PVC selection lock — same singleton that would be injected by DI in production.
    private readonly PvcSelectLock _pvcSelectLock = new();

    // Provider factory mocks — default setup returns an eligible issue (open + agent:next).
    // This keeps all existing tests passing unchanged after the IProviderFactory ctor param is added.
    private readonly Mock<IProviderFactory> _providerFactory = new();
    private readonly Mock<IIssueProvider> _issueProvider = new();

    private readonly JobTemplateStore _templateStore;
    private readonly DispatchServiceOptions _options;

    private static readonly Guid ItemId = Guid.NewGuid();

    // Default provider config returned for IssueProviderConfigId "gh-1".
    private static readonly ProviderConfig DefaultProviderConfig = new()
    {
        Id = "gh-1",
        Kind = ProviderKind.Issue,
        ProviderType = "GitHub",
        DisplayName = "Test GitHub"
    };

    public DispatchLoopTests()
    {
        _options = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            PollIntervalSeconds = 1,
            RateLimitPerSecond = 100,
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

        // Default eligible-issue behavior — issue is open, has agent:next.
        // All existing tests rely on this default so they pass without modification.
        _issueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "1",
                Title = "Test issue",
                Description = "",
                Labels = new[] { AgentLabels.Next }
            });
        _issueProvider
            .Setup(p => p.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _providerFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(_issueProvider.Object);

        _configClient
            .Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { DefaultProviderConfig });
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
            _templateStore, _options, _pvcSelectLock, _providerFactory.Object);

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

    /// <summary>
    /// PVC starvation must NOT call RequeueAsync — the item is already Pending and should
    /// be held there silently until a PVC becomes available. Calling RequeueAsync would
    /// increment RetryCount on every 10s poll cycle, corrupting the field (issue #2129).
    ///
    /// PVC starvation is detected by SelectAvailablePvcAsync returning null when all configured
    /// PVCs are mounted by live K8s Jobs.
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
        var oneVcOptions = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            RateLimitPerSecond = 100,
            KiroPvcPool = ["kiro-pvc-0"] // pool has one PVC
        };

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);

        // The only PVC is already mounted by a live job
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList
            {
                Items =
                [
                    MakeJobWithPvc("existing-job", "kiro-pvc-0")
                ]
            });

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            kiroStore, oneVcOptions, _pvcSelectLock, _providerFactory.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // PVC unavailability must NOT mutate the item's RetryCount — no requeue call
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // PVC check must happen BEFORE ClaimAsync — if ClaimAsync fires, the item transitions
        // Pending→Dispatched server-side with no K8s Job, stranding it indefinitely (issue #2129).
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// When a PVC is available, the kiro agent should have its Job created with that PVC mounted.
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

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        // kiro-pvc-0 is claimed; kiro-pvc-1 is free
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [MakeJobWithPvc("existing-job", "kiro-pvc-0")] });

        V1Job? capturedJob = null;
        _k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((job, _, _) => capturedJob = job)
            .Returns(Task.CompletedTask);

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            kiroStore, twoVcOptions, _pvcSelectLock, _providerFactory.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        capturedJob.Should().NotBeNull();
        var mountedPvc = capturedJob!.Spec.Template.Spec.Volumes
            .FirstOrDefault(v => v.PersistentVolumeClaim?.ClaimName is not null)
            ?.PersistentVolumeClaim?.ClaimName;
        mountedPvc.Should().Be("kiro-pvc-1");
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
            limitedStore, _options, _pvcSelectLock, _providerFactory.Object);

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
    public async Task WhenItemHasExplicitTimeout_K8sJob_ActiveDeadlineSeconds_UsesItemTimeout()
    {
        // item timeout (28800s) is set explicitly — effectiveTimeout = 28800s → activeDeadlineSeconds == 28860
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending(timeoutSeconds: 28800)]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
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
    public async Task WhenItemTimeoutIsZero_K8sJob_ActiveDeadlineSeconds_UsesDefaultAgentTimeout()
    {
        // item.TimeoutSeconds == 0 (not set) → falls back to PipelineConstants.DefaultAgentTimeout (30 min = 1800s)
        // activeDeadlineSeconds == 1800 + 60 == 1860
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending(timeoutSeconds: 0)]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        V1Job? capturedJob = null;
        _k8sClient.Setup(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<V1Job, string, CancellationToken>((job, _, _) => capturedJob = job)
            .Returns(Task.CompletedTask);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        capturedJob.Should().NotBeNull();
        // DefaultAgentTimeout = 30 min = 1800s; JobSpecBuilder adds 60s grace period → 1860
        capturedJob!.Spec.ActiveDeadlineSeconds.Should().Be(1860L); // 1800 + 60
    }

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
                Active = 1,
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
            store, _options, _pvcSelectLock, _providerFactory.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // With maxConcurrent=2 and only 1 truly active job, the item should be claimed and dispatched
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TODO: Add a tighter boundary variant of WhenOneRunningAndOneCompletedJobForSameSelector_ConcurrencyCountIsOne
    // using maxConcurrent=1 with one running job + one completed job. Count must equal exactly 1 to
    // block dispatch (not proceed). This distinguishes a count of 1 from a count of 0 — the current
    // maxConcurrent=2 test only verifies dispatch proceeds (count < 2) but would also pass if the active
    // job were incorrectly excluded (count=0, also < 2). A maxConcurrent=1 variant would fail in that case.

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
            store, _options, _pvcSelectLock, _providerFactory.Object);

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
            store, _options, _pvcSelectLock, _providerFactory.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Failed job within retention window must not block dispatch
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TODO: Add counter-fallback path tests for IsJobTerminal where Conditions is null or empty.
    // The current WhenOnlyJobIsFailed test sets both a "Failed" condition AND Failed=1 in counters,
    // so IsJobTerminal returns true via the conditions branch and the counter-fallback
    // (Failed > 0 && Active == 0) is never exercised. Add:
    //   - WhenOnlyJobIsFailed_NoConditions_ConcurrencyCountIsZero: Conditions=null, Failed=1, Active=0
    //   - WhenOnlyJobIsSucceeded_NoConditions_ConcurrencyCountIsZero: Conditions=null, Succeeded=1
    // to cover the fallback paths that IsJobTerminal documents as necessary.

    /// <summary>
    /// Regression test for issue #2176 — retrying job variant.
    /// A job with Failed=1, Active=1 and no terminal condition is still retrying (Kubernetes
    /// is creating a new pod). It must NOT be treated as terminal and must count toward concurrency.
    /// </summary>
    [Fact]
    public async Task WhenJobIsRetrying_ConcurrencyCountIsOne_AndDispatchIsBlocked()
    {
        const string yaml = """
            - labels: dotnet10,opencode
              image: chemsorly/coding-agent:opencode-dotnet10
              providerType: opencode
              maxConcurrent: 1
            """;
        var store = JobTemplateStore.LoadFromYaml(yaml);

        // Retrying job — first pod attempt failed but Active=1 means a retry pod is running
        var retryingJob = new V1Job
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
                Failed = 1,
                Active = 1,
                Conditions = null // "Failed" condition is only set after all retries are exhausted
            }
        };

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [retryingJob] });

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,opencode")]);

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            store, _options, _pvcSelectLock, _providerFactory.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Retrying job still occupies a concurrency slot — dispatch must be blocked
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // TODO [WARNING]: The assertions above verify that ClaimAsync and CreateJobAsync were never called,
        // but do not distinguish between "blocked by concurrency limit" and other early-exit paths (e.g.
        // BuildConcurrencyMapAsync threw and returned an empty map, eligibility gate failure, etc.).
        // A tighter assertion would also verify RequeueAsync is NOT called (confirming no error requeue
        // occurred) and PostStatusAsync is NOT called (confirming no cancellation occurred), making the
        // "dispatch blocked by concurrency" semantic explicit and ruling out silent short-circuits.
    }

    // TODO: Restore concurrent PVC-selection tests now that the lock scope has been extended to cover
    // SelectAvailablePvcAsync + ClaimAsync + CreateJobAsync atomically (TOCTOU fix for issue #2176).
    // The previously deleted tests (ConcurrentDispatch_PicksDistinctPvcs,
    // SameLoop_ConcurrentCycles_SemaphoreSerializesSelection,
    // CrossLoop_DispatchAndConsolidation_SharedLock_PicksDistinctPvcs) validated the old invariant
    // that SelectAvailablePvcAsync + CreateJobAsync ran atomically. The new wider lock scope
    // provides the same invariant — new tests should verify that two concurrent kiro dispatches
    // (same loop or cross-loop) select distinct PVCs even when both observe identical K8s state
    // before either creates a Job.

    // TODO: Add equal-values edge case test — WhenItemTimeoutEqualsGlobal_K8sJob_ActiveDeadlineSeconds_UsesSharedTimeout
    // where item.TimeoutSeconds == agentJobTimeoutSeconds (e.g. both 7200s) → activeDeadlineSeconds == 7260.
    // This boundary condition confirms that floor and ceiling converge cleanly at the same value.

    // ─── Eligibility gate — issue closed (AC #1) ──────────────────────────────

    /// <summary>
    /// AC #1: A Pending WorkItem whose upstream issue is closed must be cancelled (not dispatched)
    /// during the next DispatchLoop cycle.
    /// AC #6: RetryCount must not be incremented (RequeueAsync must not be called).
    /// </summary>
    [Fact]
    public async Task WhenIssueIsClosed_ShouldCancelWorkItemAndNotDispatch()
    {
        _issueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        // AC #1: cancellation posted with Cancelled status and a non-empty reason
        // TODO: Assert ErrorMessage contains "Issue closed" specifically, not just any non-empty string.
        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u =>
                u.Status == nameof(WorkItemStatus.Cancelled) &&
                u.ErrorMessage != null && u.ErrorMessage.Length > 0),
            It.IsAny<CancellationToken>()),
            Times.Once);

        // Item must not have been claimed or dispatched
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // AC #6: RetryCount must not be incremented — RequeueAsync must NOT be called
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Eligibility gate — ineligible labels (AC #2) ─────────────────────────

    // TODO: Consolidate into a single [Theory]/[InlineData] and add ErrorMessage assertions
    // that verify the specific label name is included in the cancellation reason.

    /// <summary>AC #2: issue has agent:error — must cancel.</summary>
    [Fact]
    public async Task WhenIssueHasIneligibleLabel_Error_ShouldCancelWorkItem()
    {
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "1", Title = "Test", Description = "",
                Labels = new[] { AgentLabels.Error }
            });

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == nameof(WorkItemStatus.Cancelled)),
            It.IsAny<CancellationToken>()),
            Times.Once);
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>AC #2: issue has agent:needs-refinement — must cancel.</summary>
    [Fact]
    public async Task WhenIssueHasIneligibleLabel_NeedsRefinement_ShouldCancelWorkItem()
    {
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "1", Title = "Test", Description = "",
                Labels = new[] { AgentLabels.NeedsRefinement }
            });

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == nameof(WorkItemStatus.Cancelled)),
            It.IsAny<CancellationToken>()),
            Times.Once);
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>AC #2: issue has agent:wont-do — must cancel.</summary>
    [Fact]
    public async Task WhenIssueHasIneligibleLabel_WontDo_ShouldCancelWorkItem()
    {
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "1", Title = "Test", Description = "",
                Labels = new[] { AgentLabels.WontDo }
            });

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == nameof(WorkItemStatus.Cancelled)),
            It.IsAny<CancellationToken>()),
            Times.Once);
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>AC #2: issue has agent:cancelled — must cancel.</summary>
    [Fact]
    public async Task WhenIssueHasIneligibleLabel_Cancelled_ShouldCancelWorkItem()
    {
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "1", Title = "Test", Description = "",
                Labels = new[] { AgentLabels.Cancelled }
            });

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == nameof(WorkItemStatus.Cancelled)),
            It.IsAny<CancellationToken>()),
            Times.Once);
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Eligibility gate — regression: open issue dispatches normally (AC #3) ─

    /// <summary>
    /// AC #3: A Pending WorkItem whose issue is open with agent:next must be dispatched normally.
    /// This is the regression check — the eligibility gate must NOT prevent normal dispatch.
    /// </summary>
    [Fact]
    public async Task WhenIssueIsOpenWithAgentNext_ShouldDispatchNormally()
    {
        // Default setup already returns open + agent:next — just verify dispatch proceeds
        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        // Item must be claimed and dispatched
        _workItemClient.Verify(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);

        // Must NOT be cancelled
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(),
            It.Is<WorkItemStatusUpdate>(u => u.Status == nameof(WorkItemStatus.Cancelled)),
            It.IsAny<CancellationToken>()),
            Times.Never);

        // Must NOT be requeued
        _workItemClient.Verify(c => c.RequeueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Eligibility gate — fail open on network error (AC #4) ───────────────

    /// <summary>
    /// AC #4: If the eligibility check fails (network error), the WorkItem must NOT be cancelled.
    /// It is skipped for the current cycle. Fail open — never cancel on inconclusive check.
    /// </summary>
    [Fact]
    public async Task WhenEligibilityCheckThrows_ShouldSkipItemWithoutCancelling()
    {
        _issueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("GitHub API unavailable"));

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        // TODO: This test only covers IsIssueClosedAsync failure. Add a test for GetIssueAsync
        // failure (issue open, label fetch fails) to verify the same fail-open behaviour.

        // Must NOT cancel (fail open)
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(),
            It.Is<WorkItemStatusUpdate>(u => u.Status == nameof(WorkItemStatus.Cancelled)),
            It.IsAny<CancellationToken>()),
            Times.Never);

        // Must NOT claim or dispatch (item is skipped for this cycle)
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Eligibility gate — per-cycle cache (AC #5) ───────────────────────────

    /// <summary>
    /// AC #5: Multiple Pending WorkItems referencing the same issue must result in only one
    /// GetIssueAsync call per dispatch cycle (per-cycle cache prevents N provider calls for N items).
    /// </summary>
    [Fact]
    public async Task WhenMultipleItemsReferenceSameIssue_ShouldCallProviderOnce()
    {
        // TODO: Assert GetProviderConfigsWithSecretsAsync call count (not covered by this test).
        // TODO: Add test for FailOpen result caching — second item hitting cached FailOpen must
        // not re-call GetProviderConfigsWithSecretsAsync.
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var item1 = new PendingWorkItemDto
        {
            Id = id1, IssueIdentifier = "1", IssueProviderConfigId = "gh-1",
            TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = "dotnet10,opencode", RetryCount = 0, TimeoutSeconds = 0
        };
        var item2 = new PendingWorkItemDto
        {
            Id = id2, IssueIdentifier = "1", IssueProviderConfigId = "gh-1",
            TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = "dotnet10,opencode", RetryCount = 0, TimeoutSeconds = 0
        };

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item1, item2]);
        _workItemClient.Setup(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemClaimResponse { WorkItemId = id1, RunId = "run-1", PayloadJson = "{}", OrchestratorUrl = "http://orchestrator:5000" });

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        // Both items must be dispatched
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), _options.Namespace, It.IsAny<CancellationToken>()), Times.Exactly(2));

        // Per-cycle cache: provider called exactly once for the shared (IssueProviderConfigId, IssueIdentifier) pair
        _issueProvider.Verify(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()), Times.Once);
        _issueProvider.Verify(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Eligibility gate — missing provider config (fail-open) ──────────────

    /// <summary>
    /// When the provider config for IssueProviderConfigId is not found, the item must be skipped
    /// (fail-open: missing config is not grounds for cancellation).
    /// </summary>
    [Fact]
    public async Task WhenProviderConfigNotFound_ShouldSkipItemWithoutCancelling()
    {
        _configClient
            .Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);

        var loop = CreateLoop();
        await loop.RunOneCycleAsync(CancellationToken.None);

        // Must NOT cancel — missing config is fail-open (skip, not cancel)
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(),
            It.Is<WorkItemStatusUpdate>(u => u.Status == nameof(WorkItemStatus.Cancelled)),
            It.IsAny<CancellationToken>()),
            Times.Never);

        // Must NOT claim
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Eligibility gate — SafeCancelWorkItemAsync swallows PostStatusAsync failure ─

    /// <summary>
    /// If PostStatusAsync throws (e.g., 400 invalid transition — item was claimed by another
    /// instance between GetPendingAsync and the cancel call), RunOneCycleAsync must not propagate
    /// the exception.
    /// </summary>
    [Fact]
    public async Task WhenCancelPostStatusFails_ShouldNotThrow()
    {
        _issueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending()]);
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("400 Bad Request — invalid transition"));

        var loop = CreateLoop();

        // Must not throw — SafeCancelWorkItemAsync swallows the exception
        await loop.RunOneCycleAsync(CancellationToken.None);
    }

    // ─── PVC selection: completed jobs within retention window ────────────────

    /// <summary>
    /// Regression test for issue #2176 — PVC selection must also exclude terminal jobs.
    /// A completed K8s Job within the log-retention window still lists its PVC in
    /// .Spec.Template.Spec.Volumes. SelectAvailablePvcAsync must filter terminal jobs so
    /// that a PVC mounted by a completed job is treated as available for re-use, keeping
    /// concurrency counting and PVC selection consistent.
    ///
    /// Scenario: pool has one PVC; the only K8s Job mounting it is completed.
    /// Expected: PVC is treated as free → dispatch proceeds.
    /// </summary>
    [Fact]
    public async Task WhenOnlyJobMountingPvcIsCompleted_PvcIsConsideredFree_AndDispatchProceeds()
    {
        const string kiroYaml = """
            - labels: dotnet10,kiro
              image: chemsorly/coding-agent:kiro-dotnet10
              providerType: kiro
              maxConcurrent: 0
            """;
        var kiroStore = JobTemplateStore.LoadFromYaml(kiroYaml);
        var oneVcOptions = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            RateLimitPerSecond = 100,
            ChatPodConnectTimeoutSeconds = 120,
            KiroPvcPool = ["kiro-pvc-0"] // pool has one PVC
        };

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);
        _workItemClient.Setup(c => c.ClaimAsync(ItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeClaimed());

        // The only job mounting kiro-pvc-0 is a completed job (terminal)
        var completedJobWithPvc = new V1Job
        {
            Metadata = new V1ObjectMeta { Name = "completed-job" },
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
                                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = "kiro-pvc-0" }
                            }
                        ]
                    }
                }
            },
            Status = new V1JobStatus
            {
                Conditions =
                [
                    new V1JobCondition { Type = "Complete", Status = "True" }
                ],
                Succeeded = 1,
                Active = 0
            }
        };

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [completedJobWithPvc] });

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            kiroStore, oneVcOptions, _pvcSelectLock, _providerFactory.Object);

        await loop.RunOneCycleAsync(CancellationToken.None);

        // Completed job's PVC must not block dispatch — item should be claimed and a Job created
        _workItemClient.Verify(c => c.ClaimAsync(It.IsAny<Guid>(), It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(c => c.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Metric / telemetry tests ─────────────────────────────────────────────
    // These tests create a local MeterListener per test method (not a class-level field),
    // so each listener is active only for the duration of its owning test method.
    // Assertions use Contain-style checks, which tolerate stray cross-test recordings
    // that may land in the bag from parallel tests on the same process-wide meter.
    // A MetricsTestCollection ("Metrics") now exists in this assembly; if snapshot-delta
    // assertions are ever needed here, extract these methods into a dedicated class and
    // add [Collection("Metrics")] to that class.

    [Fact]
    public async Task WhenPvcPoolExhausted_ShouldIncrementPvcPoolExhaustionsCounter()
    {
        // Arrange — kiro template, single PVC already mounted by a live job
        const string kiroYaml = """
            - labels: dotnet10,kiro
              image: chemsorly/coding-agent:kiro-dotnet10
              providerType: kiro
              maxConcurrent: 0
            """;
        var kiroStore = JobTemplateStore.LoadFromYaml(kiroYaml);
        var oneVcOptions = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            RateLimitPerSecond = 100,
            KiroPvcPool = ["kiro-pvc-0"]
        };

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePending("dotnet10,kiro")]);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList
            {
                Items = [MakeJobWithPvc("existing-job", "kiro-pvc-0")]
            });

        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            kiroStore, oneVcOptions, _pvcSelectLock, _providerFactory.Object);

        // Wire up MeterListener to capture Counter<long> measurements from WorkDistribution meter
        var recordings = new ConcurrentBag<(string InstrumentName, long Value, string? Pool)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            string? pool = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "pool") { pool = tag.Value?.ToString(); break; }
            }
            recordings.Add((instrument.Name, measurement, pool));
        });
        // TODO [WARNING]: listener.Start() MUST remain before RunOneCycleAsync. Start() triggers a
        // retroactive InstrumentPublished callback for already-existing static instruments on
        // WorkDistributionTelemetry.Meter, enabling measurement capture. Moving Start() after the
        // await would miss all measurements and the assertion would silently pass vacuously.
        listener.Start();

        // Act
        await loop.RunOneCycleAsync(CancellationToken.None);

        // Assert — at least one PvcPoolExhaustions recording with value=1, pool=kiro
        recordings.Should().Contain(
            r => r.InstrumentName == "workdistribution.pvc_pool_exhaustions"
                 && r.Value == 1L
                 && r.Pool == "kiro",
            "PvcPoolExhaustions must be incremented by 1 with pool=kiro when no PVC is available");
        // TODO [WARNING]: Consider also asserting _k8sClient.Verify(c => c.CreateJobAsync(...), Times.Never)
        // to ensure the early-return actually stopped dispatch. Without it, a future regression that removes
        // the early-return would still pass this test if the counter fires before execution continues.
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

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

// ─── Metric / telemetry tests ─────────────────────────────────────────────────
// These tests use MeterListener directly (IDisposable, no [Collection] fixture)
// because the JobController test project has no Metrics collection definition.
// The static PipelineTelemetry.Meter is process-wide, so concurrent tests may fire
// QueueWaitTime.Record(...) while a listener is active. Assertions use Contain-style
// checks to remain robust against concurrent test noise.
// TODO [WARNING]: DispatchLoopMetricTests is not in a [Collection] fixture to serialize execution
// against other test classes that listen on PipelineTelemetry.Meter. If two instances run
// concurrently, _recordings may capture measurements from the other test's dispatch. A false
// negative is possible (but low-probability) if a concurrent test fires a matching recording
// before this test's listener is started. The Contain-style assertion prevents false positives.

public sealed class DispatchLoopMetricTests : IDisposable
{
    private readonly Mock<IPipelineApiWorkItemClient> _workItemClient = new();
    private readonly Mock<IPipelineApiConfigClient> _configClient = new();
    private readonly Mock<IKubernetesJobClient> _k8sClient = new();
    private readonly PvcSelectLock _pvcSelectLock = new();
    private readonly Mock<IProviderFactory> _providerFactory = new();
    private readonly Mock<IIssueProvider> _issueProvider = new();

    private readonly JobTemplateStore _templateStore;
    private readonly DispatchServiceOptions _options;

    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<(string InstrumentName, double Value, string? RunType)> _recordings = [];

    private static readonly Guid MetricItemId = Guid.NewGuid();

    private static readonly ProviderConfig MetricProviderConfig = new()
    {
        Id = "gh-metrics",
        Kind = ProviderKind.Issue,
        ProviderType = "GitHub",
        DisplayName = "Metrics Test GitHub"
    };

    public DispatchLoopMetricTests()
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

        // Default: no active K8s jobs
        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        // Default: eligible issue (open, has agent:next)
        _issueProvider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _issueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "1",
                Title = "Test issue",
                Description = "",
                Labels = new[] { AgentLabels.Next }
            });
        _issueProvider
            .Setup(p => p.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _providerFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(_issueProvider.Object);

        _configClient
            .Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MetricProviderConfig });

        // Listen on PipelineTelemetry.Meter ("CodingAgent.Pipeline") — QueueWaitTime is defined there.
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            string? runType = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "run_type") { runType = tag.Value?.ToString(); break; }
            }
            _recordings.Add((instrument.Name, measurement, runType));
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    private DispatchLoop CreateLoop() =>
        new(_workItemClient.Object, _configClient.Object, _k8sClient.Object,
            _templateStore, _options, _pvcSelectLock, _providerFactory.Object);

    // ─── AC: QueueWaitTime.Record() fires on successful dispatch ─────────────
    // TODO [WARNING]: No test covers the negative path where TryCreateK8sJobAsync fails
    // (created = false; return). A regression that moves QueueWaitTime.Record() above the
    // early-return guard would go undetected. Add a companion test that stubs the K8s client
    // to fail job creation and asserts that no "dispatch.queue.wait_time" recording appears.

    /// <summary>
    /// AC: QueueWaitTime.Record() is called with the correct wait duration and run_type tag
    /// when a WorkItem is successfully dispatched.
    ///
    /// CreatedAt is set 30 seconds in the past so the recorded value is ~30 s. The upper bound
    /// guards against a year-scale value that would result if the implementation accidentally
    /// used UtcNow for both endpoints.
    /// </summary>
    [Fact]
    public async Task WhenDispatchSucceeds_RecordsQueueWaitTime_WithCorrectRunTypeTag()
    {
        // Arrange
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        var item = new PendingWorkItemDto
        {
            Id = MetricItemId,
            IssueIdentifier = "owner/repo#1",
            IssueProviderConfigId = "gh-metrics",
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = createdAt,
            AgentSelector = "dotnet10,opencode",
            RetryCount = 0,
            TimeoutSeconds = 0
        };

        _workItemClient.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _workItemClient.Setup(c => c.ClaimAsync(MetricItemId, It.IsAny<ClaimWorkItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemClaimResponse
            {
                WorkItemId = MetricItemId,
                RunId = "run-metric",
                PayloadJson = "{}",
                OrchestratorUrl = "http://orchestrator:5000"
            });
        _workItemClient.Setup(c => c.PostLabelSwapAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _configClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();

        // Act
        await loop.RunOneCycleAsync(CancellationToken.None);

        // Assert — a QueueWaitTime recording was captured for this dispatch
        // TODO [WARNING]: The lower bound (25.0) does not verify that item.CreatedAt specifically
        // was used as the start time. A regression substituting a different field (e.g., one 90 s
        // in the past) would produce a value still within [25, 120) and pass. Tightening the range
        // to e.g. >= 28.0 && < 40.0 would catch start-time substitution errors on non-loaded runners.
        _recordings.Should().Contain(
            r => r.InstrumentName == "dispatch.queue.wait_time"
                 && r.Value >= 25.0    // tolerates up to 5s clock jitter below 30s nominal wait
                 && r.Value < 120.0    // guards against year-scale value from mis-implementation
                 && r.RunType == "implementation",
            "QueueWaitTime must record the elapsed seconds from CreatedAt to dispatch with run_type='implementation'");
    }
}
