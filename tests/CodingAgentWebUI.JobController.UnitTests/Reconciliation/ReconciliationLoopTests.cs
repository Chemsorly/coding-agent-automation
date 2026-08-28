using AwesomeAssertions;
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
            AgentJobTimeoutSeconds = 7200,
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
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-_options.AgentJobTimeoutSeconds - 1),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1"
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.AgentJobTimeoutSeconds), It.IsAny<CancellationToken>()))
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

        // Verify GetActiveAsync was called with the correct timeout parameter.
        // Without this, a wrong parameter would cause the mock to return empty, PostStatusAsync
        // would never be called, and the test would silently pass as a false green.
        _workItemClient.Verify(c => c.GetActiveAsync(
            It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds),
            It.IsAny<CancellationToken>()), Times.Once);

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

// ─── Error / exception paths ──────────────────────────────────────────────────

public sealed class ReconciliationLoopErrorTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _workItemClient = new();
    private readonly Mock<IKubernetesJobClient> _k8sClient = new();
    private readonly DispatchServiceOptions _options = new()
    {
        Namespace = "test-ns",
        AgentJobTimeoutSeconds = 7200,
        ChatPodConnectTimeoutSeconds = 120
    };

    private ReconciliationLoop CreateLoop(PvcPool? pvcPool = null) =>
        new(_workItemClient.Object, _k8sClient.Object, pvcPool ?? new PvcPool([]), _options);

    // ─── ReconcileOnceAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ReconcileOnce_WhenListJobsThrows_DoesNotPropagate()
    {
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("k8s unavailable"));

        var loop = CreateLoop();

        // Should not throw — exception is caught and reconciliation is skipped
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileOnce_WhenPostStatusThrows_PvcStillReleased()
    {
        const string pvcName = "kiro-pvc-err";
        var pool = new PvcPool([pvcName]);
        var id = Guid.NewGuid();
        pool.TryClaim(id);

        var jobName = $"caa-agent-{id:N}"[..21];
        var job = MakeJob(jobName, id, succeeded: true, pvcName: pvcName);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB error"));

        var loop = CreateLoop(pool);
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // PVC must still be released even when PostStatusAsync throws
        Assert.Equal(1, pool.AvailableCount);
    }

    [Fact]
    public async Task ReconcileOnce_JobWithUnparsableWorkItemId_IsSkipped()
    {
        // Job has caa/work-item-id but value is not a valid GUID — should be skipped
        var job = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = "caa-agent-badguid00000",
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["caa/work-item-id"] = "not-a-guid"
                }
            },
            Spec = new V1JobSpec { Template = new V1PodTemplateSpec { Spec = new V1PodSpec { Volumes = [] } } },
            Status = new V1JobStatus { Succeeded = 1 }
        };

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileOnce_JobWithNoWorkItemIdLabel_IsSkipped()
    {
        var job = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = "caa-agent-nolabel0000",
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator"
                    // no caa/work-item-id
                }
            },
            Spec = new V1JobSpec { Template = new V1PodTemplateSpec { Spec = new V1PodSpec { Volumes = [] } } },
            Status = new V1JobStatus { Succeeded = 1 }
        };

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── GetJobPhase fallback to counters ─────────────────────────────────

    [Fact]
    public async Task ReconcileOnce_JobSucceededViaCounter_NotConditions_IsHandled()
    {
        // Job.Status.Succeeded = 1 but no Conditions set — fallback to counter
        var id = Guid.NewGuid();
        var job = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"caa-agent-{id:N}"[..21],
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["caa/work-item-id"] = id.ToString()
                }
            },
            Spec = new V1JobSpec { Template = new V1PodTemplateSpec { Spec = new V1PodSpec { Volumes = [] } } },
            Status = new V1JobStatus { Succeeded = 1, Conditions = [] } // empty conditions list
        };

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileOnce_JobFailedViaCounter_NotConditions_IsHandled()
    {
        var id = Guid.NewGuid();
        var job = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"caa-agent-{id:N}"[..21],
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["caa/work-item-id"] = id.ToString()
                }
            },
            Spec = new V1JobSpec { Template = new V1PodTemplateSpec { Spec = new V1PodSpec { Volumes = [] } } },
            Status = new V1JobStatus { Failed = 1, Conditions = [] }
        };

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "AgentError"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileOnce_FailedJob_ErrorMessageFromCondition()
    {
        // When conditions include a Failed condition with a Message, that message is passed through
        var id = Guid.NewGuid();
        var job = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"caa-agent-{id:N}"[..21],
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["caa/work-item-id"] = id.ToString()
                }
            },
            Spec = new V1JobSpec { Template = new V1PodTemplateSpec { Spec = new V1PodSpec { Volumes = [] } } },
            Status = new V1JobStatus
            {
                Failed = 1,
                Conditions =
                [
                    new V1JobCondition
                    {
                        Type = "Failed",
                        Status = "True",
                        Message = "BackoffLimitExceeded"
                    }
                ]
            }
        };

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.ErrorMessage == "BackoffLimitExceeded"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileOnce_ActiveJob_NoAction()
    {
        var id = Guid.NewGuid();
        var job = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"caa-agent-{id:N}"[..21],
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["caa/work-item-id"] = id.ToString()
                }
            },
            Spec = new V1JobSpec { Template = new V1PodTemplateSpec { Spec = new V1PodSpec { Volumes = [] } } },
            Status = new V1JobStatus { Active = 1 } // still running
        };

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── EnforceTimeoutsAsync error paths ────────────────────────────────

    [Fact]
    public async Task EnforceTimeouts_WhenGetActiveThrows_DoesNotPropagate()
    {
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB unavailable"));

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // No status posted — exception caught
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnforceTimeouts_WhenPostStatusThrows_ContinuesToNextItem()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var item1 = new ActiveWorkItemDto
        {
            Id = id1,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.AgentJobTimeoutSeconds + 1)),
            AgentSelector = "dotnet",
            IssueIdentifier = "owner/repo#1"
        };
        var item2 = new ActiveWorkItemDto
        {
            Id = id2,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.AgentJobTimeoutSeconds + 1)),
            AgentSelector = "dotnet",
            IssueIdentifier = "owner/repo#2"
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.AgentJobTimeoutSeconds), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item1, item2]);

        // First call throws, second should still be attempted
        _workItemClient.SetupSequence(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB transient"))
            .Returns(Task.CompletedTask);

        _k8sClient.Setup(c => c.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Both items were attempted (first threw, second succeeded)
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task EnforceTimeouts_DispatchedItem_IsSkipped()
    {
        // Only Running items should be timed out by EnforceTimeoutsAsync
        var id = Guid.NewGuid();
        var dispatchedItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Dispatched, // not Running
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.AgentJobTimeoutSeconds + 1)),
            AgentSelector = "dotnet",
            IssueIdentifier = "owner/repo#1"
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.AgentJobTimeoutSeconds), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── EnforceDispatchedTimeoutAsync error paths ────────────────────────

    [Fact]
    public async Task EnforceDispatchedTimeout_WhenGetActiveThrows_DoesNotPropagate()
    {
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB unavailable"));

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnforceDispatchedTimeout_WhenListJobsThrows_DoesNotPropagate()
    {
        var id = Guid.NewGuid();
        var dispatchedItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Dispatched,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1)),
            AgentSelector = "dotnet",
            IssueIdentifier = "owner/repo#1"
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("k8s unavailable"));

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnforceDispatchedTimeout_WhenJobExists_NoStatusPosted()
    {
        var id = Guid.NewGuid();
        var expectedJobName = $"caa-agent-{id:N}"[..21];

        var dispatchedItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Dispatched,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1)),
            AgentSelector = "dotnet",
            IssueIdentifier = "owner/repo#1"
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        // K8s Job exists for this work item
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList
            {
                Items =
                [
                    new V1Job { Metadata = new V1ObjectMeta { Name = expectedJobName } }
                ]
            });

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Regression test: consolidation/brain runs are dispatched by the API's DispatchLifecycleService
    /// which uses a different job name format ("caa-{first8hex}") from DispatchLoop ("caa-agent-{first11hex}").
    /// The reconciliation must use the stored K8sJobName from the DTO, not recompute it —
    /// otherwise it kills live pods after 120s even though they are running fine.
    /// </summary>
    [Fact]
    public async Task EnforceDispatchedTimeout_WhenK8sJobNameSetAndJobExists_UsesStoredNameNotComputed()
    {
        var id = Guid.NewGuid();
        // API-format name (DispatchLifecycleService): "caa-{first8hex}"
        var apiGeneratedJobName = $"caa-{id:N}"[..12]; // "caa-" + 8 hex chars
        // Job controller format (DispatchLoop.GenerateJobName): "caa-agent-{first11hex}"
        var controllerGeneratedJobName = $"caa-agent-{id:N}"[..21];

        // K8sJobName is set to the API-format name (as stored in the DB by DispatchLifecycleService)
        var dispatchedItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Dispatched,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1)),
            AgentSelector = "dotnet,dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            K8sJobName = apiGeneratedJobName
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        // Only the API-format job exists in K8s (controller-format name is absent)
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList
            {
                Items = [new V1Job { Metadata = new V1ObjectMeta { Name = apiGeneratedJobName } }]
            });

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        // Must NOT post Failed — the job exists under its stored name
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnforceDispatchedTimeout_WhenK8sJobNameSetButJobMissing_StillMarksFailed()
    {
        var id = Guid.NewGuid();
        var apiGeneratedJobName = $"caa-{id:N}"[..12];

        var dispatchedItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Dispatched,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1)),
            AgentSelector = "dotnet,dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            K8sJobName = apiGeneratedJobName
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        // No jobs exist at all
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        // Job is genuinely missing — should still mark Failed
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnforceDispatchedTimeout_NoDispatchedItems_NoJobListQuery()
    {
        // If no items are Dispatched after filtering, skip the ListJobsAsync call entirely
        var id = Guid.NewGuid();
        var runningItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running, // not Dispatched
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1)),
            AgentSelector = "dotnet",
            IssueIdentifier = "owner/repo#1"
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<CancellationToken>()))
            .ReturnsAsync([runningItem]);

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.ListJobsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── CleanupOrphansAsync error paths ──────────────────────────────────

    [Fact]
    public async Task CleanupOrphans_WhenListJobsThrows_DoesNotPropagate()
    {
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("k8s unavailable"));

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupOrphans_WhenGetActiveThrows_DoesNotPropagate()
    {
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB unavailable"));

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupOrphans_WhenDeleteJobThrows_DoesNotPropagate()
    {
        var orphanJob = MakeJob("caa-agent-orphan000000", workItemId: null, active: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [orphanJob] });
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _k8sClient.Setup(c => c.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("k8s error"));

        var loop = CreateLoop();

        // Should not throw — delete exception is swallowed
        var act = async () => await loop.CleanupOrphansAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("delete exceptions must be swallowed per the resilience contract");
    }

    [Fact]
    public async Task CleanupOrphans_JobWithActiveWorkItem_IsNotDeleted()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-agent-{id:N}"[..21];
        var job = MakeJob(jobName, id, active: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        // Work item is still active
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ActiveWorkItemDto
                {
                    Id = id,
                    Status = WorkItemStatus.Running,
                    DispatchedAt = DateTimeOffset.UtcNow,
                    AgentSelector = "dotnet",
                    IssueIdentifier = "owner/repo#1"
                }
            ]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.DeleteJobAsync(
            jobName, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── CancellationToken respected ─────────────────────────────────────

    [Fact]
    public async Task ReconcileOnce_CancellationToken_StopsProcessing()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var job1 = MakeJob($"caa-agent-{id1:N}"[..21], id1, succeeded: true);
        var job2 = MakeJob($"caa-agent-{id2:N}"[..21], id2, succeeded: true);

        using var cts = new CancellationTokenSource();

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job1, job2] });

        // Cancel after first PostStatusAsync
        _workItemClient.Setup(c => c.PostStatusAsync(id1, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => cts.Cancel());

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(cts.Token);

        // Only first item processed before cancellation
        _workItemClient.Verify(c => c.PostStatusAsync(id1, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Once);
        _workItemClient.Verify(c => c.PostStatusAsync(id2, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

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
}
