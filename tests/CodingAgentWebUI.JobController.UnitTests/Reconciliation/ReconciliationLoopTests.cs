using AwesomeAssertions;
using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.JobController.Reconciliation;
using CodingAgentWebUI.Pipeline;
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
        // Use the global default (30 min = 1800s) as the per-item timeout.
        // The item has been running for 1801s, exceeding its timeout.
        const int itemTimeoutSeconds = 1800; // PipelineConstants.DefaultAgentTimeout
        var timedOutItem = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(itemTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds
        };

        // EnforceTimeoutsAsync queries with TimeoutCanaryMinAgeSeconds (60s) as the pre-filter
        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<CancellationToken>()))
            .ReturnsAsync([timedOutItem]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        _k8sClient.Verify(c => c.DeleteJobAsync(jobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
        // TODO: Add Verify that GetActiveAsync was called exactly once with the canary threshold (60)
        // to guard against a regression where EnforceTimeoutsAsync passes a wrong argument and the
        // mock returns empty — in that case the PostStatusAsync Times.Once assertion would fail, but
        // the root cause (wrong query argument) would be obscured. A dedicated Verify closes the gap.
        // Note: the mock setup above already uses It.Is<int>(n => n == 60) so a wrong argument would
        // cause the mock to return empty and PostStatusAsync would not be called, making the
        // Times.Once assertion fail — but adding an explicit Verify makes the intent unambiguous.
        // (TestQualityReviewer review [WARNING] @ ReconciliationLoopTests.cs:89)
    }

    [Fact]
    public async Task WhenPerProjectTimeoutExceeded_ShouldEnforcePerItemTimeout()
    {
        var jobName = JobNameFor(ItemId);
        // Per-project AgentTimeout = 15 min (900s).
        // Item has been running for 901s — must be timed out.
        const int itemTimeoutSeconds = 900;
        var timedOutItem = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(itemTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([timedOutItem]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        _k8sClient.Verify(c => c.DeleteJobAsync(jobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenPerProjectTimeoutNotYetExceeded_ShouldNotTimeout()
    {
        // Per-project AgentTimeout = 15 min (900s).
        // Item has been running for only 500s — must NOT be timed out.
        const int itemTimeoutSeconds = 900;
        var notYetTimedOutItem = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-500),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([notYetTimedOutItem]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(),
            It.IsAny<WorkItemStatusUpdate>(),
            It.IsAny<CancellationToken>()), Times.Never);
        // TODO: Add a Verify that GetActiveAsync was called with the canary threshold (60) to
        // confirm the query threshold hasn't regressed. Currently uses It.IsAny<int>() because
        // the mock must return the item regardless; a separate Verify call would close the gap.
        // See review finding [WARNING] — TestQualityReviewer @ ReconciliationLoopTests.cs:152.
    }

    [Fact]
    public async Task WhenTimeoutSecondsIsZero_FallsBackToGlobalDefault()
    {
        var jobName = JobNameFor(ItemId);
        // TimeoutSeconds = 0 means field was not stored (pre-dates this feature).
        // Fall back to PipelineConstants.DefaultAgentTimeout (30 min = 1800s).
        // Item has been running for 1801s — must be timed out via fallback.
        // TODO: Replace magic number 1800 with (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds
        // so a change to DefaultAgentTimeout causes this test to fail rather than silently pass.
        // See review finding [WARNING] — TestQualityReviewer @ ReconciliationLoopTests.cs:187.
        const int globalDefaultSeconds = 1800;
        var legacyItem = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(globalDefaultSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = 0 // legacy: field not stored
        };

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([legacyItem]);

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
        const int itemTimeoutSeconds = 1800; // global default (30 min)

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

        // EnforceTimeoutsAsync queries with TimeoutCanaryMinAgeSeconds (60s) as pre-filter
        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<CancellationToken>()))
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
                It.Is<int>(n => n == 60), It.IsAny<CancellationToken>()))
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

    // ─── Null-fallback path tests (K8sJobName = null) ─────────────────────────

    /// <summary>
    /// Regression guard: when K8sJobName is null (legacy WorkItem dispatched before the field was
    /// persisted), EnforceTimeoutsAsync must fall back to <see cref="JobNameFactory.ForWorkItem"/>
    /// and attempt to delete the job under that name.
    /// </summary>
    [Fact]
    public async Task EnforceAgentTimeout_WhenK8sJobNameIsNull_FallsBackToForWorkItemFormat()
    {
        var id = Guid.NewGuid();
        var expectedFallbackJobName = JobNameFactory.ForWorkItem(id);

        var runningItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1801)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            K8sJobName = null // legacy — field not persisted at dispatch time
        };

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([runningItem]);
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Fallback job name is the ForWorkItem format (caa-agent-{first11hex})
        _k8sClient.Verify(c => c.DeleteJobAsync(
            expectedFallbackJobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression guard: when K8sJobName is null (legacy WorkItem), EnforceDispatchedTimeoutAsync
    /// must fall back to <see cref="JobNameFactory.ForWorkItem"/> when checking whether a live K8s
    /// Job exists for the item.
    /// </summary>
    [Fact]
    public async Task EnforceDispatchedTimeout_WhenK8sJobNameIsNull_FallsBackToForWorkItemFormat()
    {
        var id = Guid.NewGuid();
        var expectedFallbackJobName = JobNameFactory.ForWorkItem(id);

        var dispatchedItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Dispatched,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            K8sJobName = null // legacy — field not persisted at dispatch time
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        // The fallback-computed job name does NOT appear in the live job list,
        // so the item is treated as orphaned and marked Failed.
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        // Item should be marked Failed because no live job was found under the fallback name
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

}

// ─── Metric / telemetry tests ─────────────────────────────────────────────────
// These tests use MeterListener directly to capture instrument recordings from the
// static WorkDistributionTelemetry.Meter and PipelineTelemetry.Meter. Both meters are
// process-wide, so parallel tests in the same process can fire recordings into an active
// listener and cause spurious snapshot-delta failures. [Collection("Metrics")] serializes
// all metric tests in this project, eliminating the race window.

[Collection("Metrics")]
public sealed class ReconciliationLoopMetricTests : IDisposable
{
    private readonly Mock<IPipelineApiWorkItemClient> _workItemClient = new();
    private readonly Mock<IKubernetesJobClient> _k8sClient = new();
    private readonly DispatchServiceOptions _options;

    private readonly MeterListener _listener = new();

    // WorkDistribution meter recordings: (InstrumentName, DoubleValue, LongValue, AgentSelector)
    private readonly ConcurrentBag<(string InstrumentName, double DoubleValue, long LongValue, string? AgentSelector)> _recordings = [];

    // Pipeline meter recordings — separate bags for counters and histograms to avoid
    // needing a union type. Tags are captured as a materialized list for assertion.
    private readonly ConcurrentBag<(string InstrumentName, long Value, List<KeyValuePair<string, object?>> Tags)> _pipelineCounters = [];
    private readonly ConcurrentBag<(string InstrumentName, double Value, List<KeyValuePair<string, object?>> Tags)> _pipelineHistograms = [];

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

        // Enable all instruments on both the WorkDistribution and Pipeline meters
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName ||
                instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        // Capture Histogram<double> recordings — routed by meter name
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName)
            {
                string? selector = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "agent_selector") { selector = tag.Value?.ToString(); break; }
                }
                _recordings.Add((instrument.Name, measurement, 0L, selector));
            }
            else if (instrument.Meter.Name == PipelineTelemetry.SourceName)
            {
                var tagList = new List<KeyValuePair<string, object?>>();
                foreach (var tag in tags) tagList.Add(tag);
                _pipelineHistograms.Add((instrument.Name, measurement, tagList));
            }
        });

        // Capture Counter<long> recordings — routed by meter name
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName)
            {
                string? selector = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "agent_selector") { selector = tag.Value?.ToString(); break; }
                }
                _recordings.Add((instrument.Name, 0d, measurement, selector));
            }
            else if (instrument.Meter.Name == PipelineTelemetry.SourceName)
            {
                var tagList = new List<KeyValuePair<string, object?>>();
                foreach (var tag in tags) tagList.Add(tag);
                _pipelineCounters.Add((instrument.Name, measurement, tagList));
            }
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
        // Arrange
        var item = new ActiveWorkItemDto
        {
            Id = Guid.NewGuid(),
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            AgentSelector = "test",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = 1800 // global default
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<CancellationToken>()))
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

    // ─── AC: DispatchedAt = UtcNow - 7200s → enforcement proceeds, canary not incremented ──

    [Fact]
    public async Task EnforceTimeouts_WhenExecutionAgeAtOrAbove60s_ProceedsNormally_NoCanaryIncrement()
    {
        // Arrange
        var id = Guid.NewGuid();
        const int itemTimeoutSeconds = 1800; // global default (30 min)
        var item = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-7200),
            AgentSelector = "test",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<CancellationToken>()))
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

        // Assert — execution age histogram recorded (≈ 7200s)
        // TODO: This Contain assertion does not verify the recording happened exactly once for this
        // item. If a future refactor moves or duplicates the Record(...) call, this would still pass.
        // Consider adding a count assertion (e.g. count of matching entries == 1 via snapshot-delta)
        // to confirm the item was recorded exactly once before enforcement. (TestQuality review warning)
        _recordings.Should().Contain(
            r => r.InstrumentName == "workdistribution.timeout_execution_age_seconds"
                 && r.DoubleValue >= 3600.0
                 && r.AgentSelector == "test",
            "timeout_execution_age_seconds must record ≈ 7200s");
    }

    // ─── AC: DispatchedAt = null → fallback to per-item effective timeout → enforcement proceeds ──

    [Fact]
    public async Task EnforceTimeouts_WhenDispatchedAtIsNull_UsesFallbackAge_ProceedsNormally()
    {
        // Arrange
        var id = Guid.NewGuid();
        // TODO: Replace magic number 1800 with (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds
        // so a change to DefaultAgentTimeout causes this test to fail rather than silently pass
        // with an item age and expected value both derived from the same stale literal.
        // (TestQualityReviewer review [WARNING] @ ReconciliationLoopMetricTests.cs:1411)
        const int itemTimeoutSeconds = 1800; // global default
        var item = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = null,
            AgentSelector = "test",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds
        };

        // When DispatchedAt is null, executionAgeSeconds falls back to effectiveTimeoutSeconds,
        // which is >= 60s (canary threshold), so GetActiveAsync is called with 60 as pre-filter.
        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<CancellationToken>()))
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

        // Assert — execution age histogram recorded with exact fallback value (1800.0 = global default, not clock-based)
        // TODO: This Contain assertion does not bound the number of matching recordings. Using
        // Should().ContainSingle(...) would make the intent explicit and catch loop-iteration bugs
        // where Record(...) is called multiple times for the same item. (TestQuality review warning)
        _recordings.Should().Contain(
            r => r.InstrumentName == "workdistribution.timeout_execution_age_seconds"
                 && r.DoubleValue == (double)itemTimeoutSeconds
                 && r.AgentSelector == "test",
            $"timeout_execution_age_seconds must record exactly {itemTimeoutSeconds}s for null DispatchedAt (falls back to effective timeout)");
    }

    // ─── pipeline.jobs.* emission tests (Issue #2256) ────────────────────────────
    // These tests verify that WorkDistributionTelemetry.LogTerminalStatus also emits
    // PipelineTelemetry.JobsCompleted / JobsFailed / JobDuration from the long-lived
    // Job Controller process, fixing the pod-exit OTLP flush race.

    [Fact]
    public void LogTerminalStatus_Succeeded_EmitsPipelineJobsCompleted()
    {
        // Snapshot before to tolerate any stray recordings from parallel tests
        var countBefore = _pipelineCounters.Count(
            r => r.InstrumentName == "pipeline.jobs.completed" && r.Value == 1L);

        WorkDistributionTelemetry.LogTerminalStatus(
            Guid.NewGuid(), WorkItemStatus.Succeeded, TimeSpan.FromSeconds(120), null, null);

        var countAfter = _pipelineCounters.Count(
            r => r.InstrumentName == "pipeline.jobs.completed" && r.Value == 1L);

        (countAfter - countBefore).Should().Be(1,
            "pipeline.jobs.completed must be incremented once for a Succeeded status");

        // NOTE: This test embeds two distinct behavioral assertions. The second
        // LogTerminalStatus call below (used only to verify the negative path) also increments
        // pipeline.jobs.completed, which bleeds into the shared snapshot bag and could confuse
        // concurrent tests. The negative assertion is also weak: failedCountBefore may already
        // include stray recordings, so "no change" passes even if the second call misbehaves.
        // Consider splitting into a dedicated [Fact] for the negative path using a fresh MeterListener.

        // pipeline.jobs.failed must NOT be emitted for a Succeeded transition
        var failedCountBefore = _pipelineCounters.Count(r => r.InstrumentName == "pipeline.jobs.failed");
        WorkDistributionTelemetry.LogTerminalStatus(
            Guid.NewGuid(), WorkItemStatus.Succeeded, null, null, null);
        var failedCountAfter = _pipelineCounters.Count(r => r.InstrumentName == "pipeline.jobs.failed");
        failedCountAfter.Should().Be(failedCountBefore,
            "pipeline.jobs.failed must not be emitted for a Succeeded transition");
    }

    [Fact]
    public void LogTerminalStatus_Succeeded_EmitsPipelineJobsDuration()
    {
        // Use a fixed duration for a deterministic assertion value (brain entry: fixed past timestamps)
        var countBefore = _pipelineHistograms.Count(
            r => r.InstrumentName == "pipeline.jobs.duration" && Math.Abs(r.Value - 120.0) < 0.001);

        WorkDistributionTelemetry.LogTerminalStatus(
            Guid.NewGuid(), WorkItemStatus.Succeeded, TimeSpan.FromSeconds(120), null, null);

        var countAfter = _pipelineHistograms.Count(
            r => r.InstrumentName == "pipeline.jobs.duration" && Math.Abs(r.Value - 120.0) < 0.001);

        (countAfter - countBefore).Should().Be(1,
            "pipeline.jobs.duration must be recorded once with value 120.0s for a 120s duration");
    }

    [Fact]
    public void LogTerminalStatus_Failed_EmitsPipelineJobsFailed_WithSnakeCaseTag()
    {
        // Snapshot before to tolerate stray recordings
        var failedCountBefore = _pipelineCounters.Count(r => r.InstrumentName == "pipeline.jobs.failed");

        WorkDistributionTelemetry.LogTerminalStatus(
            Guid.NewGuid(), WorkItemStatus.Failed, TimeSpan.FromSeconds(60), null, FailureReason.Timeout);

        var failedCountAfter = _pipelineCounters.Count(r => r.InstrumentName == "pipeline.jobs.failed");
        (failedCountAfter - failedCountBefore).Should().Be(1,
            "pipeline.jobs.failed must be incremented once for a Failed status");

        // Assert snake_case failure_reason tag — "Timeout" → "timeout"
        _pipelineCounters.Should().Contain(
            r => r.InstrumentName == "pipeline.jobs.failed"
                 && r.Tags.Any(t => t.Key == "failure_reason" && (string?)t.Value == "timeout"),
            "failure_reason tag must be snake_case 'timeout', not PascalCase 'Timeout'");

        // NOTE: The negative assertion below (completed not emitted for Failed) is weak:
        // completedCountBefore is captured after the first LogTerminalStatus call has already run,
        // so it avoids contamination from that call, but it is still vulnerable to a race window
        // where stray parallel tests fire between the snapshot and the assertion. The assertion
        // would pass even if the production code incorrectly emitted pipeline.jobs.completed for
        // a Failed status, as long as no other test incremented it between snapshot and check.
        // Consider isolating this negative path into a dedicated [Fact] with a fresh MeterListener.

        // pipeline.jobs.completed must NOT be emitted for a Failed transition
        var completedCountBefore = _pipelineCounters.Count(r => r.InstrumentName == "pipeline.jobs.completed");
        WorkDistributionTelemetry.LogTerminalStatus(
            Guid.NewGuid(), WorkItemStatus.Failed, null, null, FailureReason.Timeout);
        var completedCountAfter = _pipelineCounters.Count(r => r.InstrumentName == "pipeline.jobs.completed");
        completedCountAfter.Should().Be(completedCountBefore,
            "pipeline.jobs.completed must not be emitted for a Failed transition");
    }

    [Fact]
    public void LogTerminalStatus_Failed_AgentError_ProducesSnakeCaseTag()
    {
        var countBefore = _pipelineCounters.Count(
            r => r.InstrumentName == "pipeline.jobs.failed"
                 && r.Tags.Any(t => t.Key == "failure_reason" && (string?)t.Value == "agent_error"));

        WorkDistributionTelemetry.LogTerminalStatus(
            Guid.NewGuid(), WorkItemStatus.Failed, null, null, FailureReason.AgentError);

        var countAfter = _pipelineCounters.Count(
            r => r.InstrumentName == "pipeline.jobs.failed"
                 && r.Tags.Any(t => t.Key == "failure_reason" && (string?)t.Value == "agent_error"));

        (countAfter - countBefore).Should().Be(1,
            "FailureReason.AgentError must produce failure_reason='agent_error' (snake_case)");
    }

    [Fact]
    public void LogTerminalStatus_Failed_NullReason_ProducesUnknownTag()
    {
        // null failureReason must produce "unknown" — matches PipelineRunInstrumentation convention
        var countBefore = _pipelineCounters.Count(
            r => r.InstrumentName == "pipeline.jobs.failed"
                 && r.Tags.Any(t => t.Key == "failure_reason" && (string?)t.Value == "unknown"));

        WorkDistributionTelemetry.LogTerminalStatus(
            Guid.NewGuid(), WorkItemStatus.Failed, null, null, failureReason: null);

        var countAfter = _pipelineCounters.Count(
            r => r.InstrumentName == "pipeline.jobs.failed"
                 && r.Tags.Any(t => t.Key == "failure_reason" && (string?)t.Value == "unknown"));

        (countAfter - countBefore).Should().Be(1,
            "null failureReason must produce failure_reason='unknown', not 'none'");
    }

    [Fact]
    public void LogTerminalStatus_Failed_NoDuration_DoesNotEmitDuration()
    {
        // With duration: null the pipeline.jobs.duration histogram must not be emitted
        var histCountBefore = _pipelineHistograms.Count(r => r.InstrumentName == "pipeline.jobs.duration");

        WorkDistributionTelemetry.LogTerminalStatus(
            Guid.NewGuid(), WorkItemStatus.Failed, duration: null, null, FailureReason.Timeout);

        var histCountAfter = _pipelineHistograms.Count(r => r.InstrumentName == "pipeline.jobs.duration");
        histCountAfter.Should().Be(histCountBefore,
            "pipeline.jobs.duration must not be emitted when duration is null");

        // NOTE: This test only covers duration:null for Failed status. There is no equivalent
        // test for Succeeded with duration:null. The null guard in production applies to both, so an
        // accidental regression on the Succeeded path would not be caught. Add a parallel test:
        // LogTerminalStatus_Succeeded_NoDuration_DoesNotEmitDuration.

        // NOTE: The duration >= 0 guard is not tested for the boundary case of TimeSpan.Zero.
        // A zero-second duration is >= 0 and should be recorded. A future change tightening the guard
        // to > 0 would silently drop zero-duration recordings without a test failure. Consider adding
        // an explicit test: LogTerminalStatus_Succeeded_ZeroDuration_EmitsDurationWithZeroValue.
    }

    [Fact]
    public async Task ReconcileOnceAsync_SucceededJob_EmitsPipelineJobsCompleted()
    {
        // Arrange: K8s Succeeded job with StartTime + CompletionTime for a deterministic duration
        var id = Guid.NewGuid();
        var jobName = $"caa-agent-{id:N}"[..21];
        var startTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var completionTime = startTime.AddSeconds(300);
        var job = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["caa/work-item-id"] = id.ToString()
                }
            },
            Spec = new V1JobSpec { Template = new V1PodTemplateSpec { Spec = new V1PodSpec { Volumes = [] } } },
            Status = new V1JobStatus
            {
                Succeeded = 1,
                StartTime = startTime,
                CompletionTime = completionTime,
                Conditions = [new V1JobCondition { Type = "Complete", Status = "True" }]
            }
        };

        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var completedCountBefore = _pipelineCounters.Count(r => r.InstrumentName == "pipeline.jobs.completed");
        var durationCountBefore = _pipelineHistograms.Count(
            r => r.InstrumentName == "pipeline.jobs.duration" && Math.Abs(r.Value - 300.0) < 0.001);

        // Act
        var loop = new ReconciliationLoop(_workItemClient.Object, _k8sClient.Object, _options);
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // Assert: pipeline.jobs.completed incremented once
        var completedCountAfter = _pipelineCounters.Count(r => r.InstrumentName == "pipeline.jobs.completed");
        (completedCountAfter - completedCountBefore).Should().Be(1,
            "ReconcileOnceAsync with a Succeeded K8s job must emit pipeline.jobs.completed");

        // Assert: pipeline.jobs.duration recorded with correct value (300s)
        var durationCountAfter = _pipelineHistograms.Count(
            r => r.InstrumentName == "pipeline.jobs.duration" && Math.Abs(r.Value - 300.0) < 0.001);
        (durationCountAfter - durationCountBefore).Should().Be(1,
            "ReconcileOnceAsync must emit pipeline.jobs.duration = 300s for a job that ran 300s");

        // NOTE: This test does not assert that pipeline.jobs.failed is NOT emitted for the
        // Succeeded path through ReconcileOnceAsync. The unit-level tests cover this negative path via
        // LogTerminalStatus directly, but the end-to-end reconciliation path leaves it unverified here.
        // Consider adding: var failedCountAfter = _pipelineCounters.Count(r => r.InstrumentName == "pipeline.jobs.failed");
        // (failedCountAfter - failedCountBefore).Should().Be(0, "pipeline.jobs.failed must not be emitted for a Succeeded job");
    }

    [Fact]
    public async Task ReconcileOnceAsync_FailedJob_EmitsPipelineJobsFailed_WithAgentErrorTag()
    {
        // Arrange: K8s Failed job
        var id = Guid.NewGuid();
        var jobName = $"caa-agent-{id:N}"[..21];
        var job = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
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
                Conditions = [new V1JobCondition { Type = "Failed", Status = "True", Message = "BackoffLimitExceeded" }]
            }
        };

        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var failedCountBefore = _pipelineCounters.Count(
            r => r.InstrumentName == "pipeline.jobs.failed"
                 && r.Tags.Any(t => t.Key == "failure_reason" && (string?)t.Value == "agent_error"));

        // Act
        var loop = new ReconciliationLoop(_workItemClient.Object, _k8sClient.Object, _options);
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // Assert: pipeline.jobs.failed incremented with failure_reason="agent_error"
        var failedCountAfter = _pipelineCounters.Count(
            r => r.InstrumentName == "pipeline.jobs.failed"
                 && r.Tags.Any(t => t.Key == "failure_reason" && (string?)t.Value == "agent_error"));
        (failedCountAfter - failedCountBefore).Should().Be(1,
            "ReconcileOnceAsync with a Failed K8s job must emit pipeline.jobs.failed with failure_reason='agent_error'");

        // NOTE: This assertion only checks the tag-filtered count, not the total unfiltered
        // delta for pipeline.jobs.failed. If the production code emitted pipeline.jobs.failed twice for
        // the same job (e.g. a double-call bug in HandleJobCompletedAsync), the filtered count would
        // still increase by 1 if the second emission used a different failure_reason tag, and this test
        // would pass. Add an unfiltered delta assertion to catch double-emission bugs:
        // var totalFailedCountAfter = _pipelineCounters.Count(r => r.InstrumentName == "pipeline.jobs.failed");
        // (totalFailedCountAfter - totalFailedCountBefore).Should().Be(1, "pipeline.jobs.failed must be emitted exactly once");
    }
}
