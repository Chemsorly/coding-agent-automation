using AwesomeAssertions;
using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.JobController.Reconciliation;
using CodingAgentWebUI.Pipeline.Telemetry;
using k8s.Models;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

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

    private ReconciliationLoop CreateLoop() =>
        new(_workItemClient.Object, _k8sClient.Object, _options);

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
        const int itemTimeoutSeconds = 900; // 15-min per-project timeout
        var timedOutItem = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(itemTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync([timedOutItem]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            // TODO: [WARNING] #2179 — ErrorMessage is not asserted here. If the formatting regresses
            // (e.g. still prints "7200s" or "0s" instead of "900s"), this test would still pass.
            // Add u.ErrorMessage == "Agent timeout after 900s" to pin the per-item timeout surfacing.
            It.IsAny<CancellationToken>()), Times.Once);

        _k8sClient.Verify(c => c.DeleteJobAsync(jobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Per-item timeout enforcement tests (AC: #2179) ──────────────────────
    // TODO: [WARNING] #2179 — None of the EnforceTimeoutsAsync tests below verify the call to
    // GetActiveAsync itself (only the downstream effects are asserted). If the implementation
    // regresses to passing a non-zero olderThanSeconds, the mock would not match, returning an
    // empty list, and the tests would fail with misleading "PostStatusAsync never called" errors.
    // Consider adding _workItemClient.Verify(c => c.GetActiveAsync(0, ...), Times.Once) to each
    // test to make argument regressions explicit and produce clear failure messages.

    [Fact]
    public async Task WhenRunningItemTimeoutExceeded_PerItemTimeout_ShouldMarkFailed()
    {
        // AC: Running item with TimeoutSeconds = 900, dispatched 910s ago → marked Failed
        var id = Guid.NewGuid();
        const int itemTimeoutSeconds = 900; // 15 min
        var item = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(itemTimeoutSeconds + 10)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#5",
            TimeoutSeconds = itemTimeoutSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenRunningItemTimeoutNotYetExceeded_PerItemTimeout_ShouldNotMarkFailed()
    {
        // AC: Running item with TimeoutSeconds = 1800, dispatched only 600s ago → not yet timed out
        var id = Guid.NewGuid();
        const int itemTimeoutSeconds = 1800; // 30 min
        var item = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-600),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#6",
            TimeoutSeconds = itemTimeoutSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenNullDispatchedAt_FallsBackToGlobalDefault_30Min_ShouldMarkFailed()
    {
        // AC: null DispatchedAt + zero TimeoutSeconds → fallback to 1800s; treated as "already past timeout"
        var id = Guid.NewGuid();
        var item = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = null,
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#7",
            TimeoutSeconds = 0 // legacy row — zero
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // When DispatchedAt is null and TimeoutSeconds = 0, both the executionAgeSeconds and
        // itemTimeoutSeconds fall back to DefaultAgentTimeout (1800s). executionAgeSeconds == itemTimeoutSeconds
        // → enforcement proceeds because the guard is strictly-less (executionAgeSeconds < itemTimeoutSeconds).
        // TODO: [WARNING] #2179 — This test relies on the exact-boundary case: equal-to the timeout triggers
        // enforcement (guard is <, not <=). If the guard is ever changed to <=, this test would fail for the
        // right reason. The ErrorMessage content ("Agent timeout after 1800s") is also unasserted — a regression
        // formatting the wrong value would not be caught by this test.
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);
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

    // ─── Terminal deduplication guard ─────────────────────────────────────────

    /// <summary>
    /// AC: two consecutive ReconcileOnceAsync calls with the same completed K8s Job result
    /// in exactly one PostStatusAsync call (the deduplication cache suppresses the second).
    /// </summary>
    [Fact]
    public async Task TwoConsecutiveReconcileCycles_SameCompletedJob_PostStatusCalledOnce()
    {
        // Arrange: same succeeded job is returned on both cycles (simulates 30s poll with job
        // still in the 600s K8s retention window)
        var jobName = JobNameFor(ItemId);
        var job = MakeJob(jobName, ItemId, succeeded: true);

        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop();

        // Act: two consecutive reconciliation cycles
        await loop.ReconcileOnceAsync(CancellationToken.None);
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // Assert: PostStatusAsync called exactly once across both cycles
        // TODO: ReconcileOnceAsync calls ListJobsAsync twice internally (once for the job watch loop
        // in ReconcileOnceAsync, once for orphan cleanup in CleanupOrphansAsync), so the mock returns
        // the completed job on all four ListJobsAsync calls (two cycles × two calls each). Verify that
        // CleanupOrphansAsync cannot independently invoke PostStatusAsync for this terminal job. If it
        // can, Times.Once would be insufficiently specific — deduplication via the primary path could
        // be bypassed while orphan cleanup fires instead, and this assertion would not detect it.
        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// AC: same deduplication behaviour for Failed jobs — two cycles, one PostStatusAsync call.
    /// </summary>
    [Fact]
    public async Task TwoConsecutiveReconcileCycles_SameFailedJob_PostStatusCalledOnce()
    {
        var jobName = JobNameFor(ItemId);
        var job = MakeJob(jobName, ItemId, failed: true);

        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop();

        await loop.ReconcileOnceAsync(CancellationToken.None);
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // TODO: Same caveat as TwoConsecutiveReconcileCycles_SameCompletedJob_PostStatusCalledOnce:
        // ListJobsAsync is called twice per ReconcileOnceAsync (job watch + orphan cleanup), so the
        // mock returns the failed job on all four calls across both cycles. If CleanupOrphansAsync
        // can also invoke PostStatusAsync for this job, Times.Once would not prove that the primary
        // deduplication path is working correctly — it could mask the primary path being suppressed
        // while the orphan-cleanup path fires instead.
        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "AgentError"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// AC: on leadership re-acquisition (OnLeadershipAcquired clears the cache), PostStatusAsync
    /// is called once more for the same completed job still present in the K8s retention window.
    /// </summary>
    [Fact]
    public async Task OnLeadershipAcquired_ClearsCacheAllowsRepost()
    {
        var jobName = JobNameFor(ItemId);
        var job = MakeJob(jobName, ItemId, succeeded: true);

        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop();

        // First leadership term — item reconciled once
        await loop.ReconcileOnceAsync(CancellationToken.None);
        _workItemClient.Verify(c => c.PostStatusAsync(ItemId, It.IsAny<WorkItemStatusUpdate>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // A second cycle in the same term must not re-post
        await loop.ReconcileOnceAsync(CancellationToken.None);
        _workItemClient.Verify(c => c.PostStatusAsync(ItemId, It.IsAny<WorkItemStatusUpdate>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Simulate leadership re-acquisition — cache cleared
        loop.OnLeadershipAcquired();

        // New leadership term — same job still present, must post once more
        await loop.ReconcileOnceAsync(CancellationToken.None);
        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ─── PVC release removed — reconciliation no longer manages PVC state ─────

    /// <summary>
    /// Reconciliation must NOT release any PVC — that responsibility was removed when
    /// PvcPool was deleted (issue #2200). This test verifies that job completion still
    /// posts the terminal status and does not throw due to the absent pool.
    /// </summary>
    [Fact]
    public async Task WhenJobSucceeds_ShouldPostStatus_NoPvcReleaseNeeded()
    {
        var jobName = JobNameFor(ItemId);
        var job = MakeJob(jobName, ItemId, succeeded: true, pvcName: "kiro-pvc-0");

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // Status must still be posted
        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"),
            It.IsAny<CancellationToken>()), Times.Once);
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
        ChatPodConnectTimeoutSeconds = 120
    };

    private ReconciliationLoop CreateLoop() =>
        new(_workItemClient.Object, _k8sClient.Object, _options);

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
    public async Task ReconcileOnce_WhenPostStatusThrows_ReconcileDoesNotPropagate()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-agent-{id:N}"[..21];
        var job = MakeJob(jobName, id, succeeded: true, pvcName: "kiro-pvc-err");

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB error"));

        // Reconciliation must not propagate the exception from PostStatusAsync
        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);
        // The DB error is swallowed — verify the status call was attempted (once, for the succeeded job)
        // but no further status calls were made (the item is not cached, so the next cycle will retry).
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression: when PostStatusAsync throws a transient error, the WorkItem ID must NOT be
    /// added to the deduplication cache so that the next reconciliation cycle retries the post.
    /// </summary>
    [Fact]
    public async Task ReconcileOnce_WhenPostStatusThrows_ItemNotCached_NextCycleRetries()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-agent-{id:N}"[..21];
        var job = MakeJob(jobName, id, succeeded: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        // First call throws a transient error; second call succeeds
        _workItemClient.SetupSequence(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Transient DB error"))
            .Returns(Task.CompletedTask);

        var loop = CreateLoop();

        // First cycle — PostStatusAsync throws
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // Second cycle — item was NOT cached on failure, so the post must be retried
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
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

        const int itemTimeoutSeconds = 1800; // 30 min — clearly past threshold

        var item1 = new ActiveWorkItemDto
        {
            Id = id1,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(itemTimeoutSeconds + 1)),
            AgentSelector = "dotnet",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds
        };
        var item2 = new ActiveWorkItemDto
        {
            Id = id2,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(itemTimeoutSeconds + 1)),
            AgentSelector = "dotnet",
            IssueIdentifier = "owner/repo#2",
            TimeoutSeconds = itemTimeoutSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 0), It.IsAny<CancellationToken>()))
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
        const int itemTimeoutSeconds = 1800;
        var dispatchedItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Dispatched, // not Running
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(itemTimeoutSeconds + 1)),
            AgentSelector = "dotnet",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 0), It.IsAny<CancellationToken>()))
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

// ─── Metric / telemetry tests ─────────────────────────────────────────────────
// These tests use MeterListener directly (IDisposable, no [Collection] fixture)
// because the JobController test project has no Metrics collection definition.
// The static WorkDistributionTelemetry.Meter is process-wide, so concurrent tests
// may fire TimeoutExecutionAge.Record(...) while a listener is active. Assertions
// use Contain-style checks and snapshot-delta patterns to remain robust.

public sealed class ReconciliationLoopMetricTests : IDisposable
{
    private readonly Mock<IPipelineApiWorkItemClient> _workItemClient = new();
    private readonly Mock<IKubernetesJobClient> _k8sClient = new();
    private readonly DispatchServiceOptions _options;

    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<(string InstrumentName, double DoubleValue, long LongValue, string? AgentSelector)> _recordings = [];

    public ReconciliationLoopMetricTests()
    {
        _options = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            ChatPodConnectTimeoutSeconds = 120
        };

        // Default: no active K8s jobs
        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        // Default: no active work items
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Enable all instruments on the WorkDistribution meter
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };

        // Capture Histogram<double> recordings (TimeoutExecutionAge)
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            string? selector = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "agent_selector") { selector = tag.Value?.ToString(); break; }
            }
            _recordings.Add((instrument.Name, measurement, 0L, selector));
        });

        // Capture Counter<long> recordings (TimeoutCanaryViolations)
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            string? selector = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "agent_selector") { selector = tag.Value?.ToString(); break; }
            }
            _recordings.Add((instrument.Name, 0d, measurement, selector));
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    private ReconciliationLoop CreateLoop() =>
        new(_workItemClient.Object, _k8sClient.Object, _options);

    // ─── AC: DispatchedAt = UtcNow - 30s → enforcement skipped, canary incremented ──

    [Fact]
    public async Task EnforceTimeouts_WhenExecutionAgeLessThan60s_SkipsEnforcementAndIncrementsCanaryCounter()
    {
        // Arrange — item with a 30-min timeout, dispatched only 30s ago (below canary threshold)
        var item = new ActiveWorkItemDto
        {
            Id = Guid.NewGuid(),
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            AgentSelector = "test",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = 1800
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        // Act
        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert — enforcement must be skipped
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Assert — canary counter incremented with correct tag
        _recordings.Should().Contain(
            r => r.InstrumentName == "workdistribution.timeout_canary_violations"
                 && r.LongValue == 1L
                 && r.AgentSelector == "test",
            "timeout_canary_violations must be incremented by 1 with agent_selector=test");

        // Assert — execution age histogram recorded (≈ 30s; window tolerates clock jitter)
        // TODO: The lower bound >= 25.0 gives only a 5s tolerance against wall-clock jitter between
        // UtcNow.AddSeconds(-30) in Arrange and the UtcNow call inside EnforceTimeoutsAsync. On a
        // heavily loaded CI agent with >5s thread preemption this assertion could fail spuriously.
        // Consider lowering the bound (e.g. >= 20.0) or using a time-frozen anchor to eliminate the
        // flakiness surface. (Correctness review warning)
        _recordings.Should().Contain(
            r => r.InstrumentName == "workdistribution.timeout_execution_age_seconds"
                 && r.DoubleValue >= 25.0 && r.DoubleValue < 60.0
                 && r.AgentSelector == "test",
            "timeout_execution_age_seconds must record ≈ 30s for a 30s-old work item");
    }

    // ─── AC: DispatchedAt = UtcNow - 1900s → enforcement proceeds, canary not incremented ──

    [Fact]
    public async Task EnforceTimeouts_WhenExecutionAgeAtOrAbove60s_ProceedsNormally_NoCanaryIncrement()
    {
        // Arrange — item with 1800s (30 min) timeout, dispatched 1900s ago (past timeout)
        var id = Guid.NewGuid();
        const int itemTimeoutSeconds = 1800;
        var item = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-1900),
            AgentSelector = "test",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Snapshot canary count before act to tolerate stray recordings from parallel tests
        var canaryCountBefore = _recordings.Count(
            r => r.InstrumentName == "workdistribution.timeout_canary_violations"
                 && r.AgentSelector == "test");

        // Act
        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert — enforcement must proceed
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Assert — canary counter NOT incremented (snapshot delta)
        var canaryCountAfter = _recordings.Count(
            r => r.InstrumentName == "workdistribution.timeout_canary_violations"
                 && r.AgentSelector == "test");
        canaryCountAfter.Should().Be(canaryCountBefore,
            "timeout_canary_violations must not be incremented when execution age >= 60s");

        // Assert — execution age histogram recorded (≈ 1900s)
        _recordings.Should().Contain(
            r => r.InstrumentName == "workdistribution.timeout_execution_age_seconds"
                 && r.DoubleValue >= 1800.0
                 && r.AgentSelector == "test",
            "timeout_execution_age_seconds must record ≈ 1900s");
    }

    // ─── AC: DispatchedAt = null → fallback to DefaultAgentTimeout (30 min) → enforcement proceeds ──

    [Fact]
    public async Task EnforceTimeouts_WhenDispatchedAtIsNull_UsesFallbackAge_ProceedsNormally()
    {
        // Arrange — null DispatchedAt; loop falls back to DefaultAgentTimeout (1800s).
        // The item's TimeoutSeconds is also 1800 so the fallback equals the per-item timeout —
        // enforcement proceeds because executionAgeSeconds == itemTimeoutSeconds >= itemTimeoutSeconds.
        var id = Guid.NewGuid();
        const double expectedFallbackAge = 1800.0; // PipelineConstants.DefaultAgentTimeout.TotalSeconds
        var item = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = null,
            AgentSelector = "test",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = 1800
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 0), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Snapshot canary count before act
        var canaryCountBefore = _recordings.Count(
            r => r.InstrumentName == "workdistribution.timeout_canary_violations"
                 && r.AgentSelector == "test");

        // Act
        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert — enforcement must proceed
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Assert — canary counter NOT incremented (snapshot delta)
        var canaryCountAfter = _recordings.Count(
            r => r.InstrumentName == "workdistribution.timeout_canary_violations"
                 && r.AgentSelector == "test");
        canaryCountAfter.Should().Be(canaryCountBefore,
            "timeout_canary_violations must not be incremented when DispatchedAt is null (falls back to full timeout age)");

        // Assert — execution age histogram recorded with exact fallback value (1800.0, not clock-based)
        _recordings.Should().Contain(
            r => r.InstrumentName == "workdistribution.timeout_execution_age_seconds"
                 && r.DoubleValue == expectedFallbackAge
                 && r.AgentSelector == "test",
            $"timeout_execution_age_seconds must record exactly {expectedFallbackAge}s for null DispatchedAt");
    }
}
