using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.JobController.Reconciliation;
using k8s.Models;

namespace CodingAgentWebUI.JobController.UnitTests.Reconciliation;

/// <summary>
/// Unit tests for ReconciliationLoop — the K8s Job watch and timeout enforcement logic.
/// Tests are written before implementation (TDD: Task 12b).
/// </summary>
public sealed class ReconciliationLoopTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _workItemClient = new();
    private readonly Mock<IKubernetesJobClient> _k8sClient = new();
    private readonly DispatchServiceOptions _options;

    private static readonly Guid ItemId = Guid.NewGuid();

    public ReconciliationLoopTests()
    {
        _options = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            ChatSessionMaxDurationSeconds = 7200,
            ChatPodConnectTimeoutSeconds = 120
        };

        // Default: no active jobs
        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        // Default: no active work items
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private ReconciliationLoop CreateLoop(PvcPool? pvcPool = null) =>
        new(_workItemClient.Object, _k8sClient.Object, pvcPool ?? new PvcPool([]), _options);

    private static string JobNameFor(Guid id) => $"caa-agent-{id:N}"[..21];

    // ─── K8s Succeeded event ─────────────────────────────────────────────────

    [Fact]
    public async Task WhenJobSucceeds_ShouldCallPostStatusAsync_WithSucceeded()
    {
        var jobName = JobNameFor(ItemId);
        var job = MakeJob(jobName, ItemId, succeeded: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── K8s Failed event ────────────────────────────────────────────────────

    [Fact]
    public async Task WhenJobFails_ShouldCallPostStatusAsync_WithFailed_AgentError()
    {
        var jobName = JobNameFor(ItemId);
        var job = MakeJob(jobName, ItemId, failed: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "AgentError"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Timeout enforcement ──────────────────────────────────────────────────

    [Fact]
    public async Task WhenTimeoutExceeded_ShouldCallPostStatusAsync_AndDeleteJob()
    {
        var jobName = JobNameFor(ItemId);
        var timedOutItem = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-_options.ChatSessionMaxDurationSeconds - 1),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1"
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatSessionMaxDurationSeconds), It.IsAny<CancellationToken>()))
            .ReturnsAsync([timedOutItem]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        _k8sClient.Verify(c => c.DeleteJobAsync(jobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Short-circuit Dispatched sweep ──────────────────────────────────────

    [Fact]
    public async Task WhenDispatchedItemExceedsConnectTimeout_ShouldCallPostStatusAsync_Immediately()
    {
        // WorkItem in Dispatched state for > chatPodConnectTimeoutSeconds with no K8s Job
        var dispatchedItem = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Dispatched,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1"
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        // No K8s Job exists for this work item
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Orphan cleanup ───────────────────────────────────────────────────────

    [Fact]
    public async Task WhenOrphanJobFound_ShouldDeleteJob_NoStatusPost()
    {
        // K8s Job exists but no work item ID matches any active item
        var orphanJob = MakeJob("caa-agent-orphan000000", workItemId: null, active: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [orphanJob] });

        // GetActiveAsync returns nothing — no active work items
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.DeleteJobAsync(
            "caa-agent-orphan000000", _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Stale terminal work item ─────────────────────────────────────────────

    [Fact]
    public async Task WhenStaleTerminalWorkItem_ShouldDeleteJob_NoStatusPost()
    {
        var jobName = JobNameFor(ItemId);
        // Succeeded job that is old — stale retention
        var staleJob = MakeJob(jobName, ItemId, succeeded: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [staleJob] });

        // No active work item matching the ID (it's in terminal state, not returned by GetActiveAsync)
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        // Reconcile detects no active work item for a terminal job — should just delete it
        await loop.CleanupOrphansAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.DeleteJobAsync(jobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── PVC release on job completion ────────────────────────────────────────

    [Fact]
    public async Task WhenJobSucceeds_ShouldReleasePvc()
    {
        const string pvcName = "kiro-pvc-0";
        var pool = new PvcPool([pvcName]);
        pool.TryClaim(ItemId); // claim it first

        var jobName = JobNameFor(ItemId);
        var job = MakeJob(jobName, ItemId, succeeded: true, pvcName: pvcName);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop(pool);
        await loop.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(1, pool.AvailableCount);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static V1Job MakeJob(
        string name,
        Guid? workItemId,
        bool succeeded = false,
        bool failed = false,
        bool active = false,
        string? pvcName = null)
    {
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/managed-by"] = "caa-orchestrator"
        };
        if (workItemId.HasValue)
            labels["caa/work-item-id"] = workItemId.Value.ToString();

        V1JobStatus status;
        if (succeeded)
            status = new V1JobStatus { Succeeded = 1, Conditions = [new V1JobCondition { Type = "Complete", Status = "True" }] };
        else if (failed)
            status = new V1JobStatus { Failed = 1, Conditions = [new V1JobCondition { Type = "Failed", Status = "True" }] };
        else
            status = new V1JobStatus { Active = active ? 1 : 0 };

        var volumes = new List<V1Volume>();
        if (pvcName is not null)
        {
            volumes.Add(new V1Volume
            {
                Name = "kiro-cli-data",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = pvcName }
            });
        }

        return new V1Job
        {
            Metadata = new V1ObjectMeta { Name = name, Labels = labels },
            Spec = new V1JobSpec
            {
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec { Volumes = volumes }
                }
            },
            Status = status
        };
    }

    // ─── Chat job protection ──────────────────────────────────────────────────

    [Fact]
    public async Task CleanupOrphans_WhenJobIsChatJob_ShouldNotDelete()
    {
        // Chat jobs have caa/chat-session-id but NO caa/work-item-id.
        // CleanupOrphansAsync must not delete them — they are managed by ChatJobDispatcher.
        var chatJob = MakeChatJob("caa-chat-6469b528", sessionId: Guid.NewGuid());

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [chatJob] });

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupOrphans_WhenMixedChatAndOrphanJobs_ShouldOnlyDeleteOrphan()
    {
        // One chat job (must survive) + one orphaned impl job (must be deleted)
        var chatJob = MakeChatJob("caa-chat-aabbccdd", sessionId: Guid.NewGuid());
        var orphanJob = MakeJob("caa-agent-orphan000000", workItemId: null, active: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [chatJob, orphanJob] });

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        // Only the orphan impl job should be deleted
        _k8sClient.Verify(c => c.DeleteJobAsync(
            "caa-agent-orphan000000", _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
        _k8sClient.Verify(c => c.DeleteJobAsync(
            "caa-chat-aabbccdd", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static V1Job MakeChatJob(string name, Guid sessionId)
    {
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
            ["app.kubernetes.io/component"] = "agent-job",
            ["caa/chat-session-id"] = sessionId.ToString(),
            ["caa/chat-selector"] = "dotnet10.opencode"
            // intentionally NO caa/work-item-id — this is what distinguishes chat jobs
        };

        return new V1Job
        {
            Metadata = new V1ObjectMeta { Name = name, Labels = labels },
            Spec = new V1JobSpec
            {
                Template = new V1PodTemplateSpec { Spec = new V1PodSpec { Volumes = [] } }
            },
            Status = new V1JobStatus { Active = 1 }
        };
    }

    // ─── Dispatched timeout lower-boundary guard ──────────────────────────────

    [Fact]
    public async Task WhenDispatchedItemBelowConnectTimeout_ShouldNotCallPostStatusAsync()
    {
        // WorkItem in Dispatched state for LESS than chatPodConnectTimeoutSeconds — must NOT fire.
        // Guards against an off-by-one that fires for any Dispatched item regardless of age.

        // GetActiveAsync with chatPodConnectTimeoutSeconds returns empty (the item hasn't exceeded the threshold)
        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]); // API-side threshold not exceeded — item not returned

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}