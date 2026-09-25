using AwesomeAssertions;
using CodingAgent.JobController.Dispatch;
using CodingAgent.JobController.Reconciliation;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;
using k8s.Models;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CodingAgent.JobController.UnitTests.Reconciliation;

/// <summary>
/// Unit tests for ReconciliationLoop — the K8s Job watch and timeout enforcement logic.
/// Tests are written before implementation (TDD: Task 12b).
/// </summary>
/// <remarks>
/// Placed in the "Metrics" collection to serialize execution with
/// <see cref="ReconciliationLoopMetricTests"/> and <see cref="ReconciliationLoopErrorTests"/>.
/// Tests here call <c>ReconcileOnceAsync</c> with terminal K8s jobs, which flows through
/// <c>HandleJobCompletedAsync → WorkDistributionTelemetry.LogTerminalStatus →
/// PipelineTelemetry.JobsFailed.Add()</c>. Without serialization, those emissions bleed
/// into the snapshot-delta assertions in <see cref="ReconciliationLoopMetricTests"/> and
/// cause a spurious "delta of 2 instead of 1" failure.
/// </remarks>
[Collection("Metrics")]
public sealed class ReconciliationLoopTests : IDisposable
{
    private readonly Mock<IPipelineApiWorkItemClient> _workItemClient = new();
    private readonly Mock<IKubernetesJobClient> _k8sClient = new();
    private readonly DispatchServiceOptions _options;

    private static readonly Guid ItemId = Guid.NewGuid();

    // Class-level ActivityListener for span assertions (issue #2977).
    // Class-level (not per-method inline 'using var') avoids listener accumulation across tests.
    // [Collection("Metrics")] serialises this class against ReconciliationLoopMetricTests and
    // ReconciliationLoopErrorTests, preventing cross-class ActivitySource races.
    private readonly ActivityListener _activityListener;
    private readonly ConcurrentBag<Activity> _capturedActivities = [];

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
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => _capturedActivities.Add(a)
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public void Dispose() => _activityListener.Dispose();

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
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Succeeded job reconciliation must not proactively delete the job —
        // K8s TTL or CleanupOrphans handles that separately.
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── K8s Failed event ────────────────────────────────────────────────────

    [Fact]
    public async Task WhenJobFails_ShouldCallPostStatusAsync_WithFailed_AgentError()
    {
        var jobName = JobNameFor(ItemId);
        var job = MakeJob(jobName, ItemId, failed: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        // WorkItem was claimed: Running → failure is an AgentError (issue #2956)
        _workItemClient.Setup(c => c.GetStatusAsync(ItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkItemStatus.Running);

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "AgentError"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Failed job reconciliation must not proactively delete the job —
        // only timeout enforcement deletes jobs.
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Issue #2956: a K8s Job that failed before any agent claimed it (WorkItem still Dispatched)
    /// must be recorded as InfrastructureFailure, not AgentError.
    /// </summary>
    [Fact]
    public async Task WhenJobFails_AndWorkItemNeverClaimed_ShouldPost_InfrastructureFailure()
    {
        var jobName = JobNameFor(ItemId);
        var job = MakeJob(jobName, ItemId, failed: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        // WorkItem was never claimed: still Dispatched → infrastructure failure
        _workItemClient.Setup(c => c.GetStatusAsync(ItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkItemStatus.Dispatched);

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "InfrastructureFailure"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Issue #2956: when GetStatusAsync fails, the fallback must still be AgentError (safe default).
    /// </summary>
    [Fact]
    public async Task WhenJobFails_AndGetStatusThrows_FallsBackTo_AgentError()
    {
        var jobName = JobNameFor(ItemId);
        var job = MakeJob(jobName, ItemId, failed: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        // GetStatusAsync throws — must fall back to AgentError
        _workItemClient.Setup(c => c.GetStatusAsync(ItemId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "AgentError"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
    // TODO: [WARNING] ClassifyJobFailureReasonAsync only checks status == Dispatched; all other
    // statuses (Running, Failed, Succeeded, null) fall through to AgentError. There is no test
    // for the boundary where GetStatusAsync returns a terminal status (e.g. WorkItemStatus.Failed
    // or WorkItemStatus.Succeeded — item already cleaned up via another path) to confirm the
    // fallthrough to AgentError is intentional and not a missed classification case.

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
            TimeoutSeconds = itemTimeoutSeconds,
            K8sJobName = jobName // stored at dispatch time — exercises the non-legacy fast path
        };

        // EnforceTimeoutsAsync queries with TimeoutCanaryMinAgeSeconds (60s) as the pre-filter
        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([timedOutItem]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        _k8sClient.Verify(c => c.DeleteJobAsync(jobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);

        // Verify GetActiveAsync was called with the canary threshold (60s) — closes the gap where
        // a wrong argument causes the mock to return empty and PostStatusAsync never fires, making
        // this test a false green that masks the root cause.
        _workItemClient.Verify(c => c.GetActiveAsync(
            It.Is<int>(n => n == 60),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
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
            TimeoutSeconds = itemTimeoutSeconds,
            K8sJobName = jobName // stored at dispatch time — exercises the non-legacy fast path
        };

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([notYetTimedOutItem]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(),
            It.IsAny<WorkItemStatusUpdate>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Verify GetActiveAsync was called with the canary threshold — confirms the query was issued
        // rather than being silently skipped (a wrong threshold would still return empty and
        // PostStatusAsync Times.Never would pass, masking the regression).
        _workItemClient.Verify(c => c.GetActiveAsync(
            It.Is<int>(n => n > 0),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A Running work item with <c>TimeoutSeconds = 0</c> must be silently skipped by
    /// <c>EnforceTimeoutsAsync</c> — it must never be force-failed, regardless of how long
    /// it has been running.
    /// <para>
    /// Rationale: the insert-path guard (issue #2745) ensures new rows always have a positive
    /// value. A zero value indicates a pre-migration or pre-guard row; the migration back-fills
    /// existing rows to 1800s. The defensive <c>continue</c> guard closes the rolling-deploy
    /// window: if the binary is updated before the migration runs, zero-valued rows must not be
    /// immediately force-failed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EnforceTimeouts_WhenTimeoutSecondsIsZero_SkipsItem()
    {
        // Item has been running far longer than any default timeout — the guard must still skip it.
        var item = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-7200),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = 0, // zero sentinel — pre-migration or pre-guard row
            K8sJobName = JobNameFor(ItemId)
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Must not be force-failed — guard must skip the item entirely.
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Confirm the query ran so the Times.Never assertions are not vacuously true.
        _workItemClient.Verify(c => c.GetActiveAsync(
            It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A Running work item with a negative <c>TimeoutSeconds</c> must be silently skipped by
    /// <c>EnforceTimeoutsAsync</c> — same guard as the zero case.
    /// </summary>
    [Fact]
    public async Task EnforceTimeouts_WhenTimeoutSecondsIsNegative_SkipsItem()
    {
        var item = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-7200),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = -1, // negative — not a valid timeout value
            K8sJobName = JobNameFor(ItemId)
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _workItemClient.Verify(c => c.GetActiveAsync(
            It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── null DispatchedAt — timeout must not fire within grace window ────────

    /// <summary>
    /// A Running WorkItem with null DispatchedAt and CreatedAt within the grace window
    /// must not be timed out. The fix uses an explicit <c>continue</c> before any metric
    /// recording when the item is within the <c>NullDispatchedAtGraceWindowSeconds</c> window,
    /// so no status post or job deletion occurs.
    /// </summary>
    [Fact]
    public async Task EnforceTimeouts_WhenDispatchedAtIsNull_ItemIsNotTimedOut()
    {
        var nullDispatchedItem = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = null,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-60), // within default 3600s grace window
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([nullDispatchedItem]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Item must survive — no status post, no job deletion
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Confirm the query was actually issued (guards against vacuous Times.Never pass)
        _workItemClient.Verify(c => c.GetActiveAsync(
            It.Is<int>(n => n == 60),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A null-DispatchedAt item within the grace window must remain un-timed-out across multiple
    /// reconciliation cycles. On every cycle the grace-window check fires and the item is skipped
    /// via <c>continue</c> before any metric recording.
    /// </summary>
    [Fact]
    public async Task EnforceTimeouts_WhenDispatchedAtIsNull_ItemRemainsUntimed_AcrossMultipleCycles()
    {
        var nullDispatchedItem = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = null,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-60), // within default 3600s grace window
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([nullDispatchedItem]);

        var loop = CreateLoop();

        // Three consecutive cycles — item must never be timed out
        await loop.EnforceTimeoutsAsync(CancellationToken.None);
        await loop.EnforceTimeoutsAsync(CancellationToken.None);
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // No timeout enforcement across all three cycles
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Confirm the loop ran all three cycles (guards against vacuous Times.Never pass)
        _workItemClient.Verify(c => c.GetActiveAsync(
            It.Is<int>(n => n == 60),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // ─── null DispatchedAt — grace window boundary tests ─────────────────────

    // TODO [WARNING]: EnforceTimeouts_NullDispatchedAt_CreatedAtWithinGraceWindow_DoesNotTimeout
    // is functionally identical to the pre-existing EnforceTimeouts_WhenDispatchedAtIsNull_ItemIsNotTimedOut
    // test above (both set CreatedAt = UtcNow.AddSeconds(-60), both assert PostStatusAsync/DeleteJobAsync
    // are Times.Never over a single cycle). The duplicate adds no coverage. Consider collapsing the two
    // into a single well-named test or removing the older one. (TestQualityReviewer review [WARNING])

    /// <summary>
    /// AC (within grace): null DispatchedAt + CreatedAt within the grace window → no timeout.
    /// The item is skipped without any status post or job deletion.
    /// </summary>
    [Fact]
    public async Task EnforceTimeouts_NullDispatchedAt_CreatedAtWithinGraceWindow_DoesNotTimeout()
    {
        var item = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = null,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-60), // well within default 3600s grace
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Confirm the query ran (guards against vacuous Times.Never pass)
        _workItemClient.Verify(c => c.GetActiveAsync(
            It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// AC (beyond grace): null DispatchedAt + CreatedAt beyond the grace window → force-fail
    /// within one reconciliation cycle. PostStatusAsync is called with Status="Failed" and
    /// FailureReason="Timeout"; DeleteJobAsync is called with the stored K8s Job name.
    /// </summary>
    // TODO [WARNING]: graceWindowSeconds is a hardcoded magic constant (3600) that re-declares
    // the DispatchServiceOptions default. If the default changes, this test will silently stop
    // exercising the beyond-grace path (the item will fall inside the new grace window and no
    // force-fail will occur). Derive the anchor from _options.NullDispatchedAtGraceWindowSeconds
    // so the test is self-consistent with the loop under test. (TestQualityReviewer review [WARNING])
    //
    // TODO [WARNING]: This test does not assert that a Warning log is emitted on the escalation path,
    // which is required by acceptance criterion 3. If the Log.Warning call were removed or moved to
    // the wrong branch, this test would still pass. Use a Serilog test sink (e.g.
    // Serilog.Sinks.TestCorrelator) to assert the Warning message is emitted when the grace window
    // expires. (Correctness + TestQualityReviewer review [WARNING])
    [Fact]
    public async Task EnforceTimeouts_NullDispatchedAt_CreatedAtBeyondGraceWindow_ForceFails()
    {
        const int graceWindowSeconds = 3600; // matches DispatchServiceOptions default
        var item = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = null,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-(graceWindowSeconds + 1)), // 1s beyond grace
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            K8sJobName = "caa-test-1234",
            TimeoutSeconds = (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Must be force-failed
        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u =>
                u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        // K8s Job must be deleted
        _k8sClient.Verify(c => c.DeleteJobAsync(
            "caa-test-1234", _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
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
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            It.IsAny<string?>(),
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
        // K8s Job exists but no work item ID matches any active item.
        // Give it an old CreationTimestamp so the retention window doesn't protect it.
        var orphanJob = MakeJob("caa-agent-orphan000000", workItemId: null, active: true,
            createdAt: DateTime.UtcNow.AddSeconds(-700)); // older than 600s retention window

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [orphanJob] });

        // GetActiveAsync returns nothing — no active work items
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        // Succeeded job that is old — stale retention. Give it an old startTime so the
        // retention window doesn't protect it from orphan cleanup.
        var staleJob = MakeJob(jobName, ItemId, succeeded: true,
            startTime: DateTime.UtcNow.AddSeconds(-700)); // older than 600s retention window

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [staleJob] });

        // No active work item matching the ID (it's in terminal state, not returned by GetActiveAsync)
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

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
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

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
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

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
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // Status must still be posted
        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"),
            It.IsAny<CancellationToken>()), Times.Once);

        // No job deletion — reconciliation of a succeeded job must not proactively delete it.
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static V1Job MakeJob(
        string name,
        Guid? workItemId,
        bool succeeded = false,
        bool failed = false,
        bool active = false,
        string? pvcName = null,
        DateTime? createdAt = null,
        DateTime? startTime = null,
        DateTime? completionTime = null)
    {
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/managed-by"] = "caa-orchestrator"
        };
        if (workItemId.HasValue)
            labels["caa/work-item-id"] = workItemId.Value.ToString();

        V1JobStatus status;
        if (succeeded)
            status = new V1JobStatus { Succeeded = 1, Conditions = [new V1JobCondition { Type = "Complete", Status = "True" }], CompletionTime = completionTime, StartTime = startTime };
        else if (failed)
            status = new V1JobStatus { Failed = 1, Conditions = [new V1JobCondition { Type = "Failed", Status = "True" }], CompletionTime = completionTime, StartTime = startTime };
        else
            status = new V1JobStatus { Active = active ? 1 : 0, StartTime = startTime, CompletionTime = completionTime };

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
            Metadata = new V1ObjectMeta { Name = name, Labels = labels, CreationTimestamp = createdAt },
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

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupOrphans_WhenMixedChatAndOrphanJobs_ShouldOnlyDeleteOrphan()
    {
        // One chat job (must survive) + one orphaned impl job (must be deleted).
        // Give the orphan an old CreationTimestamp so the retention window doesn't protect it.
        var chatJob = MakeChatJob("caa-chat-aabbccdd", sessionId: Guid.NewGuid());
        var orphanJob = MakeJob("caa-agent-orphan000000", workItemId: null, active: true,
            createdAt: DateTime.UtcNow.AddSeconds(-700)); // older than 600s retention window

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [chatJob, orphanJob] });

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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

    // ─── Orphan retention window: CreationTimestamp anchor (Issue #2950) ──────

    /// <summary>
    /// AC (#2950 regression test): a brand-new Job with no StartTime/CompletionTime and
    /// a CreationTimestamp within 600s must NOT be deleted, even when its WorkItem is not
    /// in the active set (Pending → Dispatched commit has not yet completed).
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_FreshJob_NoStartTime_WorkItemNotActive_IsNotDeleted()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-{id:N}"[..12];
        // Job created 5s ago — well within the 600s retention window. No StartTime yet.
        var freshJob = MakeJob(jobName, id, active: true,
            createdAt: DateTime.UtcNow.AddSeconds(-5));

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [freshJob] });

        // WorkItem is not in the active set yet (Pending → Dispatched commit still in flight)
        // TODO: tighten the first argument to It.Is<int>(n => n == 0) to pin the specific
        // GetActiveAsync(0, ...) calling convention used by CleanupOrphansAsync; using
        // It.IsAny<int>() would vacuously match if the cutoff argument ever changed.
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        // Must NOT be deleted — the CreationTimestamp protects it within the 600s window
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// AC: a Job with no StartTime/CompletionTime and a CreationTimestamp older than 600s
    /// must still be deleted (genuinely orphaned and never started).
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_NoStartTime_CreationTimestampOlderThan600s_IsDeleted()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-{id:N}"[..12];
        // Job created 700s ago with no StartTime — genuinely stuck/orphaned
        var stuckJob = MakeJob(jobName, id, active: false,
            createdAt: DateTime.UtcNow.AddSeconds(-700));

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [stuckJob] });

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        // Must be deleted — older than the retention window and has no timestamps
        _k8sClient.Verify(c => c.DeleteJobAsync(
            jobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// AC: a Job with no timestamps at all (no CreationTimestamp, no StartTime, no CompletionTime)
    /// must be deleted immediately — no anchor available.
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_NoTimestampsAtAll_IsDeleted()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-{id:N}"[..12];
        // Job with no timestamps at all (hand-built object)
        var noTimestampJob = MakeJob(jobName, id, active: false);
        // Ensure no CreationTimestamp is set (null by default in MakeJob)
        // TODO: the explicit null assignment below is redundant because MakeJob already defaults
        // createdAt to null; it can be removed once the intent is clear from the constructor call alone.
        noTimestampJob.Metadata!.CreationTimestamp = null;

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [noTimestampJob] });

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        // Must be deleted — no anchor available
        _k8sClient.Verify(c => c.DeleteJobAsync(
            jobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// AC: a Job with a CompletionTime within 600s is protected (existing behaviour unchanged).
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_CompletionTimeWithin600s_IsNotDeleted()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-{id:N}"[..12];
        var recentlyCompletedJob = MakeJob(jobName, id, succeeded: true,
            completionTime: DateTime.UtcNow.AddSeconds(-100)); // only 100s ago
        // TODO: a real succeeded K8s Job always has a StartTime; pass a consistent startTime here
        // (e.g. DateTime.UtcNow.AddSeconds(-200)) so the test builds a valid job shape and does not
        // silently over-test the fallback evaluation order if it ever changes.

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [recentlyCompletedJob] });

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        // CompletionTime is recent — must not be deleted yet
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// AC: a Job with a CompletionTime older than 600s is deleted (existing behaviour unchanged).
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_CompletionTimeOlderThan600s_IsDeleted()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-{id:N}"[..12];
        var oldCompletedJob = MakeJob(jobName, id, succeeded: true,
            completionTime: DateTime.UtcNow.AddSeconds(-700)); // 700s ago, past the window
        // TODO: a real succeeded K8s Job always has a StartTime; pass a consistent startTime here
        // (e.g. DateTime.UtcNow.AddSeconds(-800)) so the test builds a valid job shape and does not
        // silently over-test the fallback evaluation order if it ever changes.

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [oldCompletedJob] });

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        // CompletionTime is old enough — must be deleted
        _k8sClient.Verify(c => c.DeleteJobAsync(
            jobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// AC: a running Job (StartTime > 600s ago) whose WorkItem is terminal (not in active set)
    /// must be deleted. This is the PVC-release path — the DB frees the RWO PVC at the terminal
    /// transition, so the next kiro run needs that pod gone.
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_RunningJob_StartTimeOlderThan600s_TerminalWorkItem_IsDeleted()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-{id:N}"[..12];
        // Running job (no CompletionTime) but StartTime is old — WorkItem already terminal in DB
        var staleRunningJob = MakeJob(jobName, id, active: true,
            startTime: DateTime.UtcNow.AddSeconds(-700)); // started 700s ago

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [staleRunningJob] });

        // WorkItem is terminal (e.g. Succeeded/Failed/Cancelled) — not in active set
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        // StartTime is old enough and WorkItem is gone — must be deleted (PVC release)
        _k8sClient.Verify(c => c.DeleteJobAsync(
            jobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// AC: a fresh model-fetch Job (no caa/work-item-id label) with a recent CreationTimestamp
    /// is NOT deleted (protected by the CreationTimestamp retention window, not by chat-job guard).
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_FreshModelFetchJob_NoWorkItemId_IsNotDeleted()
    {
        // Model-fetch jobs carry managed-by=caa-orchestrator but no caa/work-item-id.
        // They are not chat jobs either (no caa/chat-session-id).
        // A fresh one must be protected by the CreationTimestamp retention window.
        var modelFetchJob = MakeJob("caa-model-fetch-abc", workItemId: null, active: true,
            createdAt: DateTime.UtcNow.AddSeconds(-5)); // fresh

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [modelFetchJob] });

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        // Fresh model-fetch job — must not be deleted
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// AC: two consecutive sweeps — a fresh Job is protected on the first sweep (active set empty),
    /// and still not deleted on the second sweep after the WorkItem appears as Dispatched.
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_TwoCycles_FreshJobProtectedThenDispatchedActive_IsNeverDeleted()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-{id:N}"[..12];
        // Job created 5s ago — within the 600s window
        var freshJob = MakeJob(jobName, id, active: true,
            createdAt: DateTime.UtcNow.AddSeconds(-5));

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [freshJob] });

        // Cycle 1: active set is empty (WorkItem still Pending)
        _workItemClient.SetupSequence(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([])   // first sweep — Pending, not yet active
            .ReturnsAsync(      // second sweep — WorkItem now Dispatched
            [
                new ActiveWorkItemDto
                {
                    Id = id,
                    Status = WorkItemStatus.Dispatched,
                    DispatchedAt = DateTimeOffset.UtcNow,
                    AgentSelector = "dotnet",
                    IssueIdentifier = "owner/repo#1"
                }
            ]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None); // sweep 1 — protected by CreationTimestamp
        await loop.CleanupOrphansAsync(CancellationToken.None); // sweep 2 — protected by active set
        // TODO: the second sweep is also protected by the 600s CreationTimestamp window (job is only
        // ~5s old), so this test does not isolate the active-set protection path. To truly exercise
        // sweep-2-via-active-set, a variant is needed where the job's CreationTimestamp is old enough
        // to lose retention protection but the WorkItem is returned as Dispatched in the active set.

        // Job must never be deleted across either sweep
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// AC: the dispatched-timeout path posts FailureReason = "Timeout" (not "DispatchTimeout"),
    /// matching the sibling timeout path and how it is persisted in WorkItemTransitionService.
    /// </summary>
    [Fact]
    public async Task EnforceDispatchedTimeout_PostsFailureReason_Timeout_NotDispatchTimeout()
    {
        var id = Guid.NewGuid();
        var dispatchedItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Dispatched,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1"
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        // No K8s Job exists
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        // Must use "Timeout" (nameof(FailureReason.Timeout)), NOT "DispatchTimeout"
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u =>
                u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Must NOT use "DispatchTimeout" — it is not a defined FailureReason member
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(),
            It.Is<WorkItemStatusUpdate>(u => u.FailureReason == "DispatchTimeout"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Dispatched timeout lower-boundary guard ──────────────────────────────

    [Fact]
    public async Task WhenDispatchedItemBelowConnectTimeout_ShouldNotCallPostStatusAsync()
    {
        // Guards against an off-by-one that fires for any Dispatched item regardless of age.

        // GetActiveAsync with chatPodConnectTimeoutSeconds returns empty (the item hasn't exceeded the threshold)
        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]); // API-side threshold not exceeded — item not returned

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── K8sJobName == null label-selector resolution (Issue #2474) ───────────

    /// <summary>
    /// AC: when K8sJobName is null, EnforceTimeoutsAsync must query by label selector and
    /// delete the job using the resolved name, NOT the ForWorkItem fallback name.
    /// </summary>
    [Fact]
    public async Task EnforceTimeout_WhenK8sJobNameIsNull_ResolvesJobViaLabelSelector_AndDeletes()
    {
        var id = Guid.NewGuid();
        // API-path job name format: "caa-{first8hex}" (ForBrain)
        var resolvedJobName = $"caa-{id:N}"[..12];
        const int itemTimeoutSeconds = 1800;

        var runningItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(itemTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds,
            K8sJobName = null // legacy — field not persisted at dispatch time
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([runningItem]);

        // Label-selector lookup returns the actual API-path job
        // TODO: tighten the label-selector predicate from Contains("caa/work-item-id") to
        // s == $"caa/work-item-id={id}" so the mock/verify only matches the exact selector
        // including the correct work-item Guid. The loose Contains predicate would silently match
        // a wrong-Guid selector, hiding a copy-paste regression.
        _k8sClient.Setup(c => c.ListJobsAsync(
                _options.Namespace,
                It.Is<string>(s => s.Contains("caa/work-item-id")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [MakeJob(resolvedJobName, id, active: true)] });

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Must delete using the label-resolved name, not ForWorkItem format
        _k8sClient.Verify(c => c.DeleteJobAsync(
            resolvedJobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);

        // Must NOT delete using the ForWorkItem fallback name (caa-agent-{first11hex})
        var oldFormatName = $"caa-agent-{id:N}"[..21];
        _k8sClient.Verify(c => c.DeleteJobAsync(
            oldFormatName, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Status must still be posted as Failed/Timeout
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Label-selector ListJobsAsync must have been called (proves the label-lookup path was taken)
        // TODO: tighten to s == $"caa/work-item-id={id}" (see Setup comment above)
        _k8sClient.Verify(c => c.ListJobsAsync(
            _options.Namespace,
            It.Is<string>(s => s.Contains("caa/work-item-id")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// When K8sJobName is null but the label-selector query returns no job (already cleaned up),
    /// EnforceTimeoutsAsync must post the Failed status but skip deletion entirely.
    /// </summary>
    [Fact]
    public async Task EnforceTimeout_WhenK8sJobNameIsNull_AndNoJobFoundViaLabel_SkipsDeletion()
    {
        var id = Guid.NewGuid();
        const int itemTimeoutSeconds = 1800;

        var runningItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(itemTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds,
            K8sJobName = null
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([runningItem]);

        // Label-selector query returns nothing — job was already cleaned up
        _k8sClient.Setup(c => c.ListJobsAsync(
                _options.Namespace,
                It.Is<string>(s => s.Contains("caa/work-item-id")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Status must be posted
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Must NOT call DeleteJobAsync at all — no job was found, nothing to delete
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// When K8sJobName is set (non-null), EnforceTimeoutsAsync must use it directly and
    /// must NOT issue a label-selector ListJobsAsync call.
    /// </summary>
    [Fact]
    public async Task EnforceTimeout_WhenK8sJobNameIsSet_DoesNotCallLabelSelector()
    {
        var id = Guid.NewGuid();
        const string storedJobName = "caa-a1b2c3d4"; // stored at dispatch time
        const int itemTimeoutSeconds = 1800;

        var runningItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(itemTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds,
            K8sJobName = storedJobName // non-null — fast path
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([runningItem]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Must delete using the stored name
        _k8sClient.Verify(c => c.DeleteJobAsync(
            storedJobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);

        // Must NOT call ListJobsAsync with a caa/work-item-id label selector
        _k8sClient.Verify(c => c.ListJobsAsync(
            It.IsAny<string>(),
            It.Is<string>(s => s.Contains("caa/work-item-id")),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// When K8sJobName is null and the live job list contains a job with the matching
    /// caa/work-item-id label, EnforceDispatchedTimeoutAsync must NOT time out the item —
    /// the job is live under a different name than ForWorkItem would compute.
    /// Regression for the bug where ForWorkItem name wasn't in liveJobNames → false DispatchTimeout.
    /// </summary>
    [Fact]
    public async Task EnforceDispatchedTimeout_WhenK8sJobNameIsNull_AndJobExistsViaLabel_DoesNotTimeOut()
    {
        var id = Guid.NewGuid();
        // API-path job name: "caa-{first8hex}" — will NOT appear in liveJobNames under ForWorkItem format
        var apiJobName = $"caa-{id:N}"[..12];

        var dispatchedItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Dispatched,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            K8sJobName = null // legacy row
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        // Live job list contains the actual API-path job with the correct caa/work-item-id label
        // TODO: tighten the ListJobsAsync mock to assert the correct namespace and label-selector
        // arguments. The production code issues the broad "app.kubernetes.io/managed-by=caa-orchestrator"
        // selector here (not a per-item selector). Using It.IsAny for both args means a regression
        // that changes the selector or namespace would still satisfy this mock, hiding the breakage.
        // Preferred: It.Is<string>(s => s == _options.Namespace) and
        //            It.Is<string>(s => s == "app.kubernetes.io/managed-by=caa-orchestrator").
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [MakeJob(apiJobName, id, active: true)] });

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        // Must NOT post Failed — the job was found via label, item is NOT orphaned
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// When K8sJobName is null and the live job list is empty (no job with the matching label),
    /// EnforceDispatchedTimeoutAsync must mark the item Failed/DispatchTimeout.
    /// </summary>
    [Fact]
    public async Task EnforceDispatchedTimeout_WhenK8sJobNameIsNull_AndNoJobInLiveList_TimesOutItem()
    {
        var id = Guid.NewGuid();

        var dispatchedItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Dispatched,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            K8sJobName = null
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        // No live jobs at all
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        // Item is genuinely orphaned — must be marked Failed/Timeout
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Characterisation tests: grace-window boundary and null-CreatedAt (Issue #2774) ──

    /// <summary>
    /// Characterisation test (Issue #2774 prerequisite, Gap 1):
    /// A null-DispatchedAt item with CreatedAt aged exactly equal to
    /// NullDispatchedAtGraceWindowSeconds must NOT be skipped — the strict less-than
    /// condition (<c>&lt;</c>, not <c>&lt;=</c>) means equality proceeds to enforcement.
    /// The item is old enough to clear the canary guard (graceWindowSeconds=3600 &gt;&gt; 60s).
    /// </summary>
    [Fact]
    public async Task EnforceTimeouts_NullDispatchedAt_CreatedAtExactlyAtGraceBoundary_ProceedsToEnforcement()
    {
        // graceWindowSeconds == NullDispatchedAtGraceWindowSeconds (default 3600s).
        // Using the options value directly so the test stays self-consistent if the default changes.
        var graceWindowSeconds = _options.NullDispatchedAtGraceWindowSeconds;
        var item = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = null,
            // Exactly at the boundary — createdAgeSeconds == graceWindowSeconds, not strictly less.
            // TODO [WARNING]: DateTimeOffset.UtcNow is sampled here at construction time. By the time
            // ResolveExecutionAge re-samples UtcNow, wall-clock drift ensures createdAgeSeconds is
            // strictly GREATER than graceWindowSeconds, so this test never exercises the exact
            // boundary it documents. It only verifies the already-covered "beyond grace window" path
            // and would pass vacuously if the < were changed to <=. To reliably pin the exact
            // boundary, inject a clock abstraction (Func<DateTimeOffset> or IClock) so both
            // samples return the same instant.
            // (TestQualityReviewer [WARNING])
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-graceWindowSeconds),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            K8sJobName = "caa-boundary-test",
            TimeoutSeconds = (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Anti-vacuity guard: confirm the query ran and the item was seen by the loop.
        _workItemClient.Verify(c => c.GetActiveAsync(
            It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // createdAgeSeconds == graceWindowSeconds → condition (age < grace) is false → NOT skipped.
        // The item proceeds to enforcement (PostStatusAsync must be called).
        _workItemClient.Verify(c => c.PostStatusAsync(
            ItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);
        // TODO [WARNING]: This test does not assert that no canary violation counter was emitted.
        // If a future refactor inadvertently routes the grace-fallback path through the canary check
        // before returning Enforceable, a spurious canary increment would go undetected. Add a
        //   canaryCollector.GetMeasurementSnapshot().Should().BeEmpty()
        // assertion here (mirroring the approach in ReconciliationLoopMetricTests) to lock down
        // this invariant.
        // (TestQualityReviewer [WARNING])
    }

    /// <summary>
    /// Characterisation test (Issue #2774 prerequisite, Gap 2):
    /// A work item with both DispatchedAt and CreatedAt null is permanently deferred —
    /// the null-CreatedAt fallback sets createdAgeSeconds = 0.0, which is always less
    /// than any positive grace window, so the item is never enforced.
    /// </summary>
    // TODO [WARNING]: This test characterises a KNOWN BUG — null CreatedAt causes permanent
    // deferral with no log or metric, making the item invisible to operators. The inline
    // TODO [WARNING] in ResolveExecutionAge describes the fix (emit a Log.Warning and consider
    // proceeding to enforcement). If that bug is fixed, this test will fail and must be updated
    // to assert the corrected behaviour. Do NOT treat a pass here as validation of correct
    // behaviour — it validates the current broken behaviour.
    // (TestQualityReviewer [WARNING])
    [Fact]
    public async Task EnforceTimeouts_NullDispatchedAt_NullCreatedAt_IsAlwaysSkipped()
    {
        var item = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = null,
            CreatedAt = null, // null CreatedAt → age falls back to 0.0 → always within grace window
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Anti-vacuity guard: confirm the query ran and the loop processed the item.
        // Without this, a mock that returns empty from GetActiveAsync would cause
        // Times.Never to pass vacuously, making the test a false green.
        _workItemClient.Verify(c => c.GetActiveAsync(
            It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Item must be permanently deferred — 0.0 < graceWindowSeconds → WithinGrace → no enforcement.
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Reconciliation span emissions (issue #2977) ───────────────────────────

    /// <summary>
    /// AC: When a K8s Job fails, Reconcile.JobFailed span is emitted exactly once with
    /// the work_item_id and failure_reason tags.
    /// </summary>
    [Fact]
    public async Task ReconcileOnce_WhenJobFailed_EmitsReconcileJobFailedSpan()
    {
        var jobName = JobNameFor(ItemId);
        var job = MakeJob(jobName, ItemId, failed: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _workItemClient.Setup(c => c.GetStatusAsync(ItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkItemStatus.Running);

        _capturedActivities.Clear();
        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        var span = _capturedActivities.FirstOrDefault(a => a.OperationName == "Reconcile.JobFailed");
        span.Should().NotBeNull("Reconcile.JobFailed must be emitted when a K8s Job fails");
        span!.GetTagItem("work_item_id").Should().Be(ItemId);
        // TODO: This assertion is too weak — any non-null string passes. Since GetStatusAsync returns
        // WorkItemStatus.Running, the expected value is "AgentError". Strengthen to:
        //   span.GetTagItem("failure_reason").Should().Be("AgentError");
        span.GetTagItem("failure_reason").Should().NotBeNull("failure_reason tag must be set");
    }

    /// <summary>
    /// AC: When a K8s Job SUCCEEDS, Reconcile.JobFailed span is NOT emitted.
    /// The span is placed in case JobPhaseFailed: branch only — not in HandleJobCompletedAsync
    /// which is called for both succeeded and failed jobs.
    /// </summary>
    [Fact]
    public async Task ReconcileOnce_WhenJobSucceeds_DoesNotEmitReconcileJobFailedSpan()
    {
        var jobName = JobNameFor(ItemId);
        var job = MakeJob(jobName, ItemId, succeeded: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _capturedActivities.Clear();
        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _capturedActivities
            .Where(a => a.OperationName == "Reconcile.JobFailed")
            .Should().BeEmpty("Reconcile.JobFailed must NOT fire on succeeded jobs — only on failed jobs");
    }

    /// <summary>
    /// AC: When EnforceTimeoutsAsync times out a Running WorkItem, Reconcile.Timeout span
    /// is emitted with work_item_id, agent_selector, and timeout_seconds tags.
    /// </summary>
    [Fact]
    public async Task EnforceTimeouts_WhenItemTimedOut_EmitsReconcileTimeoutSpan()
    {
        const int timeoutSeconds = 1800;
        var timedOutItem = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(timeoutSeconds + 1)),
            AgentSelector = "kiro,dotnet",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = timeoutSeconds,
            K8sJobName = JobNameFor(ItemId)
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([timedOutItem]);
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(), It.Is<string>(s => s.Contains("work-item-id")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] }); // no live job found

        _capturedActivities.Clear();
        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        var span = _capturedActivities.FirstOrDefault(a => a.OperationName == "Reconcile.Timeout");
        span.Should().NotBeNull("Reconcile.Timeout must be emitted when a Running WorkItem is timed out");
        span!.GetTagItem("work_item_id").Should().Be(ItemId);
        span.GetTagItem("agent_selector").Should().Be("kiro,dotnet");
        span.GetTagItem("timeout_seconds").Should().Be(timeoutSeconds);
    }

    /// <summary>
    /// AC: When EnforceTimeoutsAsync finds no timed-out items (idle cycle), no Reconcile.Timeout
    /// span is emitted.
    /// </summary>
    [Fact]
    public async Task EnforceTimeouts_WhenNoItemsTimedOut_DoesNotEmitReconcileTimeoutSpan()
    {
        const int timeoutSeconds = 1800;
        var notYetTimedOut = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-60),  // only 60s, well below 1800s timeout
            AgentSelector = "kiro,dotnet",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = timeoutSeconds,
            K8sJobName = JobNameFor(ItemId)
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([notYetTimedOut]);

        _capturedActivities.Clear();
        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        _capturedActivities
            .Where(a => a.OperationName == "Reconcile.Timeout")
            .Should().BeEmpty("items within their timeout must not emit Reconcile.Timeout spans");
    }

    /// <summary>
    /// AC: When EnforceDispatchedTimeoutAsync finds a Dispatched item with no live K8s Job,
    /// Reconcile.DispatchedTimeout span is emitted with work_item_id tag.
    /// </summary>
    [Fact]
    public async Task EnforceDispatchedTimeout_WhenDispatchedItemHasNoJob_EmitsReconcileDispatchedTimeoutSpan()
    {
        const int connectTimeoutSeconds = 120;
        var stuckItem = new ActiveWorkItemDto
        {
            Id = ItemId,
            Status = WorkItemStatus.Dispatched,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(connectTimeoutSeconds + 1)),
            AgentSelector = "kiro,dotnet",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = 3600,
            K8sJobName = $"caa-{ItemId:N}"[..15]
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == connectTimeoutSeconds), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([stuckItem]);
        // No live jobs in the cluster → item is orphaned
        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(), It.Is<string>(s => s.Contains("managed-by")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _capturedActivities.Clear();
        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        var span = _capturedActivities.FirstOrDefault(a => a.OperationName == "Reconcile.DispatchedTimeout");
        span.Should().NotBeNull("Reconcile.DispatchedTimeout must be emitted when a Dispatched item has no live K8s Job");
        span!.GetTagItem("work_item_id").Should().Be(ItemId);
    }

    /// <summary>
    /// AC: When CleanupOrphansAsync finds and deletes an orphaned K8s Job, Reconcile.OrphanCleanup
    /// span is emitted with job_name and orphan_reason tags.
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_WhenOrphanDeleted_EmitsReconcileOrphanCleanupSpan()
    {
        var orphanJobName = "caa-orphan-job";
        var orphanJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = orphanJobName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator"
                },
                // Old completion time (> 600s ago) → past retention window → will be deleted
                CreationTimestamp = DateTime.UtcNow.AddSeconds(-700)
            },
            Spec = new V1JobSpec { Template = new V1PodTemplateSpec { Spec = new V1PodSpec { Volumes = [] } } },
            Status = new V1JobStatus { CompletionTime = DateTime.UtcNow.AddSeconds(-700) }
        };

        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [orphanJob] });
        // No active work items → the job is an orphan
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _k8sClient.Setup(c => c.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _capturedActivities.Clear();
        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        var span = _capturedActivities.FirstOrDefault(a => a.OperationName == "Reconcile.OrphanCleanup");
        span.Should().NotBeNull("Reconcile.OrphanCleanup must be emitted when an orphaned job is deleted");
        span!.GetTagItem("job_name").Should().Be(orphanJobName);
        // TODO: This assertion is too weak — any non-null string passes. The job has no caa/work-item-id
        // label, so production code sets orphanReason = "no caa/work-item-id label". Strengthen to:
        //   span.GetTagItem("orphan_reason").Should().Be("no caa/work-item-id label");
        span.GetTagItem("orphan_reason").Should().NotBeNull("orphan_reason tag must be set");
    }

    /// <summary>
    /// AC: When CleanupOrphansAsync finds no orphans (idle cycle), no Reconcile.OrphanCleanup
    /// span is emitted.
    /// </summary>
    [Fact]
    public async Task CleanupOrphans_WhenNoOrphans_DoesNotEmitReconcileOrphanCleanupSpan()
    {
        // No jobs in cluster → no orphans to clean
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        _capturedActivities.Clear();
        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        _capturedActivities
            .Where(a => a.OperationName == "Reconcile.OrphanCleanup")
            .Should().BeEmpty("idle cycles with no orphans must not emit Reconcile.OrphanCleanup spans");
    }

}

// ─── Error / exception paths ──────────────────────────────────────────────────

[Collection("Metrics")]
public sealed class ReconciliationLoopErrorTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _workItemClient = new();
    private readonly Mock<IKubernetesJobClient> _k8sClient = new();
    private readonly DispatchServiceOptions _options = new()
    {
        Namespace = "test-ns",
        ChatPodConnectTimeoutSeconds = 120
    };

    private ReconciliationLoop CreateLoop(Serilog.ILogger? logger = null) =>
        new(_workItemClient.Object, _k8sClient.Object, _options, logger: logger);

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
            .ReturnsAsync(true);

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
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

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
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

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
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

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
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item1, item2]);

        // First call throws, second should still be attempted
        _workItemClient.SetupSequence(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB transient"))
            .ReturnsAsync(true);

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
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            IssueIdentifier = "owner/repo#1",
            K8sJobName = expectedJobName // stored — exercises the non-legacy fast path
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        // K8s Job exists for this work item (job name matches the stored K8sJobName)
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
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB unavailable"));

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupOrphans_WhenDeleteJobThrows_DoesNotPropagate()
    {
        // Give the orphan an old CreationTimestamp so the retention window doesn't protect it.
        var orphanJob = MakeJob("caa-agent-orphan000000", workItemId: null, active: true,
            createdAt: DateTime.UtcNow.AddSeconds(-700));

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [orphanJob] });
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            .ReturnsAsync(true)
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
        string? pvcName = null,
        DateTime? createdAt = null,
        DateTime? startTime = null,
        DateTime? completionTime = null)
    {
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/managed-by"] = "caa-orchestrator"
        };
        if (workItemId.HasValue)
            labels["caa/work-item-id"] = workItemId.Value.ToString();

        V1JobStatus status;
        if (succeeded)
            status = new V1JobStatus { Succeeded = 1, Conditions = [new V1JobCondition { Type = "Complete", Status = "True" }], CompletionTime = completionTime, StartTime = startTime };
        else if (failed)
            status = new V1JobStatus { Failed = 1, Conditions = [new V1JobCondition { Type = "Failed", Status = "True" }], CompletionTime = completionTime, StartTime = startTime };
        else
            status = new V1JobStatus { Active = active ? 1 : 0, StartTime = startTime, CompletionTime = completionTime };

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
            Metadata = new V1ObjectMeta { Name = name, Labels = labels, CreationTimestamp = createdAt },
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
    /// Regression guard (Issue #2474): when K8sJobName is null (legacy WorkItem dispatched before
    /// the field was persisted), EnforceTimeoutsAsync must resolve the actual K8s Job name via
    /// label selector and delete it — NOT fall back to JobNameFactory.ForWorkItem.
    /// </summary>
    [Fact]
    public async Task EnforceAgentTimeout_WhenK8sJobNameIsNull_ResolvesViaLabelSelector_NotForWorkItemFallback()
    {
        var id = Guid.NewGuid();
        // API-path job name (ForBrain format): "caa-{first8hex}"
        var resolvedJobName = $"caa-{id:N}"[..12];

        var runningItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1801)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = 1800,
            K8sJobName = null // legacy — field not persisted at dispatch time
        };

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([runningItem]);
        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Label-selector query returns the actual API-path job
        _k8sClient.Setup(c => c.ListJobsAsync(
                It.IsAny<string>(),
                It.Is<string>(s => s.Contains("caa/work-item-id")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList
            {
                Items =
                [
                    new V1Job
                    {
                        Metadata = new V1ObjectMeta
                        {
                            Name = resolvedJobName,
                            Labels = new Dictionary<string, string>
                            {
                                ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                                ["caa/work-item-id"] = id.ToString()
                            }
                        },
                        Status = new V1JobStatus { Active = 1 }
                    }
                ]
            });

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Must delete using the label-resolved API-path name
        _k8sClient.Verify(c => c.DeleteJobAsync(
            resolvedJobName, _options.Namespace, It.IsAny<CancellationToken>()), Times.Once);

        // Must NOT use the old ForWorkItem format (caa-agent-{first11hex}) — that was the bug
        var oldFormatName = $"caa-agent-{id:N}"[..21];
        _k8sClient.Verify(c => c.DeleteJobAsync(
            oldFormatName, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Regression guard (Issue #2474): when K8sJobName is null (legacy WorkItem), the
    /// EnforceDispatchedTimeoutAsync must resolve job existence via the caa/work-item-id label
    /// from the already-fetched live job list, NOT via the ForWorkItem name fallback.
    /// When the live list contains the job under its actual API-path label, the item must NOT
    /// be timed out (the old bug falsely marked it Failed because ForWorkItem name ∉ liveJobNames).
    /// </summary>
    [Fact]
    public async Task EnforceDispatchedTimeout_WhenK8sJobNameIsNull_ResolvesViaLabel_NotForWorkItemFallback()
    {
        var id = Guid.NewGuid();
        // API-path job name: "caa-{first8hex}" — does NOT match ForWorkItem format
        var apiJobName = $"caa-{id:N}"[..12];

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
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        // Live job list contains the API-path job with the correct caa/work-item-id label.
        // The ForWorkItem name ("caa-agent-{first11hex}") is NOT in this list — this was the
        // condition that triggered the false DispatchTimeout bug before the fix.
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList
            {
                Items =
                [
                    new V1Job
                    {
                        Metadata = new V1ObjectMeta
                        {
                            Name = apiJobName,
                            Labels = new Dictionary<string, string>
                            {
                                ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                                ["caa/work-item-id"] = id.ToString()
                            }
                        },
                        Status = new V1JobStatus { Active = 1 }
                    }
                ]
            });

        _workItemClient.Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        // Job was found via label — item must NOT be marked Failed (was the bug: it WAS marked Failed)
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Items = null guard (Issue #2643) ─────────────────────────────────────

    /// <summary>
    /// Regression guard (Issue #2643): when ListJobsAsync returns V1JobList { Items = null }
    /// (valid K8s API behaviour on an empty cluster), EnforceDispatchedTimeoutAsync must not
    /// throw a NullReferenceException. Before the fix the NRE was caught by the outer try/catch
    /// and the entire sweep was silently skipped.
    ///
    /// Uses K8sJobName = null so the label-lookup branch (Site 2) is also exercised in addition
    /// to the liveJobNames.Select call (Site 1). PostStatusAsync must fire because the item has
    /// no matching K8s Job (null Items → empty set → isLive = false → DispatchTimeout).
    /// </summary>
    [Fact]
    public async Task EnforceDispatchedTimeoutAsync_WhenItemsIsNull_DoesNotThrow()
    {
        var id = Guid.NewGuid();
        var dispatchedItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Dispatched,
            K8sJobName = null, // ensures the label-lookup branch (Site 2) is reached
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(_options.ChatPodConnectTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1"
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == _options.ChatPodConnectTimeoutSeconds),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([dispatchedItem]);

        // K8s returns a valid response but with Items = null (the bug trigger)
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = null });

        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var loop = CreateLoop();
        await loop.EnforceDispatchedTimeoutAsync(CancellationToken.None);

        // null Items treated as empty → no live job found → item is orphaned → must be marked Failed
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression guard (Issue #2643): when the label-selector ListJobsAsync returns
    /// V1JobList { Items = null } (valid K8s API behaviour), EnforceTimeoutsAsync must not
    /// throw a NullReferenceException. Before the fix the NRE at labelJobs.Items.FirstOrDefault()
    /// (Site 3) was caught by the per-item try/catch and logged as an error, but the item was
    /// never marked Failed. After the fix the method posts Timeout status and skips deletion.
    /// </summary>
    [Fact]
    public async Task EnforceTimeoutsAsync_WhenLabelJobsItemsIsNull_DoesNotThrow()
    {
        var id = Guid.NewGuid();
        const int itemTimeoutSeconds = 1800;

        var runningItem = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            K8sJobName = null, // routes to label-selector path (Site 3)
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-(itemTimeoutSeconds + 1)),
            AgentSelector = "dotnet10,opencode",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds
        };

        // TODO: 60 is the value of the private constant TimeoutCanaryMinAgeSeconds in ReconciliationLoop.
        // If that constant ever changes, this matcher will silently not match and GetActiveAsync will
        // return an empty list, making the test vacuously pass (PostStatusAsync never called but
        // Times.Once would then fail — so the test is not silently green, but the failure would appear
        // unrelated to the constant change). Consider exposing the constant via a public property or
        // InternalsVisibleTo so the test can reference it directly rather than hardcoding 60.
        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([runningItem]);

        // Label-selector call returns a valid response but with Items = null (the bug trigger)
        // TODO: This mock only intercepts calls whose label-selector contains "caa/work-item-id".
        // The ReconciliationLoopErrorTests constructor does NOT set a catch-all ListJobsAsync default,
        // so any unmatched ListJobsAsync call (e.g. the "app.kubernetes.io/managed-by=caa-orchestrator"
        // selector used by EnforceDispatchedTimeoutAsync) will return null from Moq for the V1JobList
        // reference itself — not null Items, but a null list object — which could cause an NRE on a
        // different code path. EnforceTimeoutsAsync does not currently call that selector, so this is
        // safe today. If EnforceTimeoutsAsync is ever refactored to add a bulk pre-fetch, add a
        // catch-all default here (ReturnsAsync(new V1JobList { Items = [] })) to cover that path.
        _k8sClient.Setup(c => c.ListJobsAsync(
                _options.Namespace,
                It.Is<string>(s => s.Contains("caa/work-item-id")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = null });

        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // null Items treated as empty → no job resolved → status posted, deletion skipped
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        // No job name resolved from null Items → DeleteJobAsync must not be called
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Regression guard (Issue #2643): when ListJobsAsync returns V1JobList { Items = null },
    /// ReconcileOnceAsync must not throw a NullReferenceException. Before the fix the NRE on the
    /// foreach at jobs.Items (Site 4) would propagate out of the try block and return early,
    /// skipping all job processing. After the fix the foreach iterates an empty set cleanly.
    /// </summary>
    [Fact]
    public async Task ReconcileOnceAsync_WhenItemsIsNull_DoesNotThrow()
    {
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = null });

        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // null Items treated as empty → no jobs to process → no status updates
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);

        // Confirm ListJobsAsync was actually called (guards against vacuous Times.Never pass)
        _k8sClient.Verify(c => c.ListJobsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression guard (Issue #2643): when ListJobsAsync returns V1JobList { Items = null },
    /// CleanupOrphansAsync must not throw a NullReferenceException. Before the fix the NRE on the
    /// foreach at jobs.Items (Site 5) would propagate out of the try block and return early,
    /// skipping all orphan cleanup. After the fix the foreach iterates an empty set cleanly.
    /// </summary>
    [Fact]
    public async Task CleanupOrphansAsync_WhenItemsIsNull_DoesNotThrow()
    {
        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = null });

        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var loop = CreateLoop();
        await loop.CleanupOrphansAsync(CancellationToken.None);

        // null Items treated as empty → no orphans to delete
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Confirm ListJobsAsync was actually called (guards against vacuous Times.Never pass)
        _k8sClient.Verify(c => c.ListJobsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── 400-rejection caching (Issue #2673) ──────────────────────────────

    /// <summary>
    /// AC: when PostStatusAsync returns 400 (rejected transition), the WorkItem ID is added to
    /// _reconciledTerminalIds so the next reconciliation cycle does not retry the POST.
    /// Verifies acceptance criterion: "The retry loop no longer fires for the same WorkItem ID
    /// on subsequent reconciliation cycles."
    /// </summary>
    [Fact]
    public async Task ReconcileOnce_WhenPostStatusReturns400_ItemIsCached_NextCycleDoesNotRetry()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-agent-{id:N}"[..21];
        var job = MakeJob(jobName, id, succeeded: true);

        // Use SetupSequence to explicitly return the same job list on both reconciliation cycles.
        // This makes the causal proof clear: the job is visible on the second cycle, so the only
        // reason PostStatusAsync is not called a second time is that the ID was cached in
        // _reconciledTerminalIds by the 400 handler — not because the job disappeared from the list.
        _k8sClient.SetupSequence(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] })   // first cycle
            .ReturnsAsync(new V1JobList { Items = [job] });  // second cycle — job still visible

        // PostStatusAsync: first call throws a 400 (simulates WorkItem still in Pending state).
        // A second call is configured to throw InvalidOperationException so that if the 400-caching
        // fix is accidentally removed and the second cycle retries, the test fails loudly rather
        // than passing vacuously.
        _workItemClient.SetupSequence(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("WorkItem not in a transitionable state", null, System.Net.HttpStatusCode.BadRequest))
            .ThrowsAsync(new InvalidOperationException("PostStatusAsync called a second time — the 400-caching fix must have been removed"));

        var loop = CreateLoop();

        // First cycle — receives 400, must cache the ID
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // Second cycle — same job is still visible in the K8s list (proven by SetupSequence above),
        // but the ID is in _reconciledTerminalIds so PostStatusAsync must NOT be called again.
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // PostStatusAsync must have been called exactly once: the 400 response caused the ID
        // to be cached, suppressing the retry on the second cycle even though the job was
        // still present in the K8s list.
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// AC: when PostStatusAsync returns 400, the log entry is at Warning level — not Error.
    /// Uses injected Mock&lt;Serilog.ILogger&gt; to capture log calls.
    /// Verifies acceptance criterion: "The log entry for a 400 rejection is at Warning level
    /// (not Error)."
    /// </summary>
    [Fact]
    public async Task ReconcileOnce_WhenPostStatusReturns400_LogsWarningNotError()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-agent-{id:N}"[..21];
        var job = MakeJob(jobName, id, succeeded: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("WorkItem not in a transitionable state", null, System.Net.HttpStatusCode.BadRequest));

        // Inject a mock logger so we can verify log level calls
        var mockLogger = new Mock<Serilog.ILogger>();
        // ForContext<T>() is called in the constructor — set it up to return the mock itself
        // so all log calls go to the same mock instance and can be verified.
        mockLogger.Setup(l => l.ForContext<ReconciliationLoop>()).Returns(mockLogger.Object);

        var loop = CreateLoop(mockLogger.Object);
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // Must emit exactly one Warning with the WorkItem ID as a parameter.
        // Serilog uses generic overloads (Warning<T>(string, T)), so Moq correctly intercepts
        // Warning<Guid> when It.IsAny<Guid>() is used — no boxing ambiguity.
        // TODO: Times.Once here only matches the Warning<Guid>(string, Guid) overload. If a
        // future code change adds a Warning call with a different signature in the same code path,
        // that call would not be counted here and the test would still pass. If broader Warning
        // coverage is needed, verify against the full message template string instead.
        // (Review finding: correctness [WARNING], test quality [WARNING])
        mockLogger.Verify(
            l => l.Warning(It.IsAny<string>(), It.IsAny<Guid>()),
            Times.Once,
            "A 400-rejected completion must log at Warning level, not Error");

        // Must NOT emit any Error for the 400 case
        // (pair with a positive assertion above to ensure this isn't vacuously true).
        // TODO: The Error verify matches Error<T0,T1>(Exception, string, T0, T1) with T0=string,
        // T1=Guid. If the production Error call's signature changes (e.g. parameters reordered or
        // a type changes), the Times.Never assertion could pass vacuously (no invocation matched
        // the old signature even though Error was still logged under a new one). If this test
        // starts failing in unexpected ways, check whether the Error overload still matches.
        // (Review finding: test quality [WARNING])
        mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()),
            Times.Never,
            "A 400-rejected completion must NOT log at Error level");
    }

    /// <summary>
    /// AC: the 400-caching behaviour applies to Failed K8s jobs as well as Succeeded jobs —
    /// both phases call the same HandleJobCompletedAsync method.
    /// </summary>
    [Fact]
    public async Task ReconcileOnce_WhenPostStatusReturns400_ForFailedJob_ItemIsCached()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-agent-{id:N}"[..21];
        var job = MakeJob(jobName, id, failed: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("WorkItem not in a transitionable state", null, System.Net.HttpStatusCode.BadRequest));

        var loop = CreateLoop();

        // First cycle receives 400 — must cache
        await loop.ReconcileOnceAsync(CancellationToken.None);
        // Second cycle — ID must be cached, no retry
        await loop.ReconcileOnceAsync(CancellationToken.None);

        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression guard: a transient 5xx HttpRequestException must NOT be treated like a 400 —
    /// the WorkItem ID must not be cached and the next cycle must retry.
    /// Verifies acceptance criterion: "Transient HTTP errors (5xx) continue to be retried as before."
    /// </summary>
    [Fact]
    public async Task ReconcileOnce_WhenPostStatusThrows5xx_ItemNotCached_NextCycleStillRetries()
    {
        var id = Guid.NewGuid();
        var jobName = $"caa-agent-{id:N}"[..21];
        var job = MakeJob(jobName, id, succeeded: true);

        _k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [job] });

        // First call throws a 500 (transient); second call succeeds
        _workItemClient.SetupSequence(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Internal server error", null, System.Net.HttpStatusCode.InternalServerError))
            .ReturnsAsync(true);

        var loop = CreateLoop();

        // First cycle — 5xx is not cached, item must be retried next cycle
        await loop.ReconcileOnceAsync(CancellationToken.None);
        // Second cycle — ID was NOT cached after the 5xx, so the POST is retried and succeeds
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // PostStatusAsync called twice: once for the 5xx (not cached), once for the success
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

}

// ─── Metric / telemetry tests ─────────────────────────────────────────────────
// These tests use MeterListener directly (IDisposable).
// The static WorkDistributionTelemetry.Meter and PipelineTelemetry.Meter are process-wide.
// ReconciliationLoop tests now use TestMeterFactory for isolated instrument capture.
// LogTerminalStatus tests still use MeterListener against static meters since that method
// calls static PipelineTelemetry/WorkDistributionTelemetry instruments directly.
// Serialized with ReconciliationLoopTests via [Collection("Metrics")] to prevent parallel
// test runs from bleeding pipeline.jobs.failed emissions into this class's _pipelineCounters bag.
[Collection("Metrics")]
public sealed class ReconciliationLoopMetricTests : IDisposable
{
    private readonly Mock<IPipelineApiWorkItemClient> _workItemClient = new();
    private readonly Mock<IKubernetesJobClient> _k8sClient = new();
    private readonly DispatchServiceOptions _options;

    private readonly TestMeterFactory _workDistFactory = new();
    private readonly TestMeterFactory _pipelineFactory = new();

    private readonly MeterListener _listener = new();

    // WorkDistribution meter recordings (for LogTerminalStatus static calls): (InstrumentName, DoubleValue, LongValue, AgentSelector)
    private readonly ConcurrentBag<(string InstrumentName, double DoubleValue, long LongValue, string? AgentSelector)> _recordings = [];

    // Pipeline meter recordings — for LogTerminalStatus static calls
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
        _workItemClient.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Enable static meters for LogTerminalStatus tests
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName ||
                instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        // Capture Histogram<double> recordings
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

        // Capture Counter<long> recordings
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

    public void Dispose()
    {
        _listener.Dispose();
        _workDistFactory.Dispose();
        _pipelineFactory.Dispose();
    }

    private ReconciliationLoop CreateLoop() =>
        new(_workItemClient.Object, _k8sClient.Object, _options,
            pipelineMeterFactory: _pipelineFactory,
            workDistMeterFactory: _workDistFactory);

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
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        using var canaryCollector = new MetricCollector<long>(_workDistFactory, WorkDistributionTelemetry.MeterName, "workdistribution.timeout_canary_violations");
        using var ageCollector = new MetricCollector<double>(_workDistFactory, WorkDistributionTelemetry.MeterName, "workdistribution.timeout_execution_age_seconds");

        // Act
        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert — enforcement must be skipped
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
        _k8sClient.Verify(c => c.DeleteJobAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Assert — canary counter incremented with correct tag
        canaryCollector.GetMeasurementSnapshot().Should().Contain(
            m => m.Value == 1L && m.Tags.Contains(new KeyValuePair<string, object?>("agent_selector", "test")),
            "timeout_canary_violations must be incremented by 1 with agent_selector=test");

        // Assert — execution age histogram recorded (≈ 30s)
        ageCollector.GetMeasurementSnapshot().Should().Contain(
            m => m.Value >= 25.0 && m.Value < 60.0 && m.Tags.Contains(new KeyValuePair<string, object?>("agent_selector", "test")),
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
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var canaryCollector = new MetricCollector<long>(_workDistFactory, WorkDistributionTelemetry.MeterName, "workdistribution.timeout_canary_violations");
        using var ageCollector = new MetricCollector<double>(_workDistFactory, WorkDistributionTelemetry.MeterName, "workdistribution.timeout_execution_age_seconds");

        // Act
        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert — enforcement must proceed
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Assert — canary counter NOT incremented
        canaryCollector.GetMeasurementSnapshot().Should().BeEmpty(
            "timeout_canary_violations must not be incremented when execution age >= 60s");

        // Assert — execution age histogram recorded (≈ 7200s)
        ageCollector.GetMeasurementSnapshot().Should().Contain(
            m => m.Value >= 3600.0 && m.Tags.Contains(new KeyValuePair<string, object?>("agent_selector", "test")),
            "timeout_execution_age_seconds must record ≈ 7200s");
    }

    // ─── AC: DispatchedAt = null, within grace window → skipped, no metrics recorded ──

    /// <summary>
    /// A null-DispatchedAt item within the grace window is skipped via an explicit
    /// <c>continue</c> BEFORE <c>_timeoutExecutionAge.Record</c> and the canary guard.
    /// Neither the age histogram nor the canary counter must fire for expected grace-window
    /// deferrals — the canary metric signals INV-001 (wrong timestamp anchor bugs), not
    /// intentional deferrals. Recording here would pollute the signal and mask real bugs.
    /// </summary>
    [Fact]
    public async Task EnforceTimeouts_WhenDispatchedAtIsNull_WithinGraceWindow_RecordsNoMetrics()
    {
        // Arrange
        var id = Guid.NewGuid();
        var item = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = null,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-60), // within default 3600s grace window
            AgentSelector = "test",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        using var canaryCollector = new MetricCollector<long>(_workDistFactory, WorkDistributionTelemetry.MeterName, "workdistribution.timeout_canary_violations");
        using var ageCollector = new MetricCollector<double>(_workDistFactory, WorkDistributionTelemetry.MeterName, "workdistribution.timeout_execution_age_seconds");

        // Act
        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert — enforcement must NOT proceed (item is skipped by grace-window check, not timed out)
        _workItemClient.Verify(c => c.PostStatusAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()), Times.Never);

        // Assert — canary counter must NOT be incremented for expected grace-window deferrals.
        // The continue fires before the canary guard, so _timeoutCanaryViolations is not touched.
        canaryCollector.GetMeasurementSnapshot().Should().BeEmpty(
            "timeout_canary_violations must not be incremented for within-grace-window null-DispatchedAt items — " +
            "the canary metric signals INV-001 (wrong timestamp anchor bugs), not intentional deferrals");

        // Assert — execution age histogram must NOT be recorded (continue fires before Record call).
        ageCollector.GetMeasurementSnapshot().Should().BeEmpty(
            "timeout_execution_age_seconds must not record anything for within-grace-window items — " +
            "the continue fires before _timeoutExecutionAge.Record()");
    }

    // ─── AC: DispatchedAt = null, beyond grace window → records CreatedAt age, no canary ──

    /// <summary>
    /// A null-DispatchedAt item beyond the grace window is escalated: CreatedAt becomes the
    /// timeout anchor, <c>_timeoutExecutionAge</c> records the created age (not 0), and the
    /// canary counter must NOT be incremented because the age is well above 60s.
    /// </summary>
    [Fact]
    public async Task EnforceTimeouts_NullDispatchedAt_BeyondGraceWindow_RecordsCreatedAtAgeAndNoCanaryViolation()
    {
        // Arrange
        const int graceWindowSeconds = 3600; // matches DispatchServiceOptions default
        var id = Guid.NewGuid();
        var item = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = null,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-(graceWindowSeconds + 1)),
            AgentSelector = "test",
            IssueIdentifier = "owner/repo#1",
            K8sJobName = "caa-test-beyond-grace",
            TimeoutSeconds = (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var canaryCollector = new MetricCollector<long>(_workDistFactory, WorkDistributionTelemetry.MeterName, "workdistribution.timeout_canary_violations");
        using var ageCollector = new MetricCollector<double>(_workDistFactory, WorkDistributionTelemetry.MeterName, "workdistribution.timeout_execution_age_seconds");

        // Act
        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert — enforcement must proceed (item is force-failed)
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Assert — canary counter NOT incremented (createdAgeSeconds >> 60s, so canary guard does not fire)
        canaryCollector.GetMeasurementSnapshot().Should().BeEmpty(
            "timeout_canary_violations must not be incremented when null-DispatchedAt item is beyond grace window — " +
            "createdAgeSeconds > graceWindowSeconds >= 60s means the canary guard does not fire");

        // Assert — execution age histogram records the created age (≈ graceWindowSeconds + 1s, not 0)
        // TODO [WARNING]: lower-bound check (>= graceWindowSeconds) is correct but there is no upper-
        // bound check. A broken implementation that records an arbitrarily large value would still pass
        // this assertion. Add an upper bound such as m.Value < graceWindowSeconds + 60 to turn this into
        // a meaningful regression guard. (TestQualityReviewer review [WARNING])
        ageCollector.GetMeasurementSnapshot().Should().ContainSingle(
            m => m.Value >= graceWindowSeconds
                 && m.Tags.Contains(new KeyValuePair<string, object?>("agent_selector", "test")),
            $"timeout_execution_age_seconds must record the created age (≥ {graceWindowSeconds}s), not 0");
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
        // Use the class-level _pipelineCounters bag with a before/after delta to count emissions.
        // A scoped MeterListener subscribing to the static PipelineTelemetry meter would pick up
        // emissions from other test assemblies running in parallel in the same process on CI,
        // causing spurious double-counts. The delta approach is immune to pre-existing recordings
        // and is consistent with the pattern used by other tests in this class.
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
            .ReturnsAsync(true);

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
            .ReturnsAsync(true);

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

    // ─── Characterisation tests: canary-threshold overlap and null-CreatedAt metrics (Issue #2774) ──

    /// <summary>
    /// Characterisation test (Issue #2774 prerequisite, Gap 3):
    /// At TimeoutSeconds == 60 (the canary minimum), the canary guard
    /// (executionAgeSeconds &lt; 60) and the enforcement guard (executionAgeSeconds &lt; 60)
    /// share the same threshold. An item dispatched exactly 60s ago clears the canary guard
    /// (60 is not &lt; 60) and is immediately enforced. No canary counter must be incremented.
    /// </summary>
    [Fact]
    public async Task EnforceTimeouts_WhenTimeoutIs60s_AndAgeIs60s_EnforcesWithoutCanary()
    {
        var id = Guid.NewGuid();
        const int itemTimeoutSeconds = 60; // == TimeoutCanaryMinAgeSeconds
        var item = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-itemTimeoutSeconds), // age == 60s
            AgentSelector = "test",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = itemTimeoutSeconds,
            K8sJobName = "caa-canary-boundary"
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        using var canaryCollector = new MetricCollector<long>(_workDistFactory, WorkDistributionTelemetry.MeterName, "workdistribution.timeout_canary_violations");
        using var ageCollector = new MetricCollector<double>(_workDistFactory, WorkDistributionTelemetry.MeterName, "workdistribution.timeout_execution_age_seconds");

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Anti-vacuity: confirm the query ran.
        _workItemClient.Verify(c => c.GetActiveAsync(
            It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Enforcement must proceed — age (60s) is not strictly less than timeout (60s).
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed" && u.FailureReason == "Timeout"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Canary counter must NOT be incremented — age (60s) is not strictly less than 60.
        canaryCollector.GetMeasurementSnapshot().Should().BeEmpty(
            "timeout_canary_violations must not be incremented when execution age == TimeoutCanaryMinAgeSeconds (60s)");

        // Execution age histogram must have been recorded (≈ 60s).
        ageCollector.GetMeasurementSnapshot().Should().Contain(
            m => m.Value >= 55.0 && m.Value <= 65.0 && m.Tags.Contains(new KeyValuePair<string, object?>("agent_selector", "test")),
            "timeout_execution_age_seconds must record ≈ 60s for an item dispatched 60s ago");
    }

    /// <summary>
    /// Characterisation test (Issue #2774 prerequisite, Gap 4):
    /// A work item with both DispatchedAt and CreatedAt null must record no metrics —
    /// the grace-window return fires before _timeoutExecutionAge.Record and before the
    /// canary counter, so neither instrument must be touched.
    /// </summary>
    [Fact]
    public async Task EnforceTimeouts_NullDispatchedAt_NullCreatedAt_RecordsNoMetrics()
    {
        var id = Guid.NewGuid();
        var item = new ActiveWorkItemDto
        {
            Id = id,
            Status = WorkItemStatus.Running,
            DispatchedAt = null,
            CreatedAt = null, // null CreatedAt → age = 0.0 → always within grace window
            AgentSelector = "test",
            IssueIdentifier = "owner/repo#1",
            TimeoutSeconds = (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds
        };

        _workItemClient.Setup(c => c.GetActiveAsync(
                It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        using var canaryCollector = new MetricCollector<long>(_workDistFactory, WorkDistributionTelemetry.MeterName, "workdistribution.timeout_canary_violations");
        using var ageCollector = new MetricCollector<double>(_workDistFactory, WorkDistributionTelemetry.MeterName, "workdistribution.timeout_execution_age_seconds");

        var loop = CreateLoop();
        await loop.EnforceTimeoutsAsync(CancellationToken.None);

        // Anti-vacuity guard: confirm the query ran and the loop processed the item.
        _workItemClient.Verify(c => c.GetActiveAsync(
            It.Is<int>(n => n == 60), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // No metrics must be recorded — the WithinGrace return fires before Record or Add.
        canaryCollector.GetMeasurementSnapshot().Should().BeEmpty(
            "timeout_canary_violations must not be incremented when null-DispatchedAt + null-CreatedAt item is within grace window");
        ageCollector.GetMeasurementSnapshot().Should().BeEmpty(
            "timeout_execution_age_seconds must not be recorded when null-DispatchedAt + null-CreatedAt item is within grace window");
    }

    /// <summary>
    /// AC3 for issue #2802: when a K8s Failed Job is reconciled for an already-terminal work item
    /// (i.e. PostStatusAsync returns false / HTTP 204 No Content — idempotent no-op),
    /// HandleJobCompletedAsync must NOT emit any terminal metrics.
    ///
    /// This prevents double-counting the work item's terminal outcome: the Cancelled metric was
    /// already emitted when the item was first cancelled; the late Failed K8s callback must not
    /// add a second Failed metric entry.
    ///
    /// TODO: The symmetric case — K8s Succeeded job whose work item was already terminal (e.g.
    /// already Cancelled due to a race) — is not covered here. The if (transitioned) guard in
    /// HandleJobCompletedAsync applies equally to the Succeeded path; a regression that
    /// re-emits a Succeeded terminal metric for a no-op would not be caught by this test.
    /// Consider adding a ReconcileOnce_WhenPostStatusReturnsNoOp_ForAlreadyTerminalItem_SucceededJob
    /// variant to cover that path. See review finding (TestQualityReviewer) for issue #2802.
    /// </summary>
    [Fact]
    public async Task ReconcileOnce_WhenPostStatusReturnsNoOp_ForAlreadyTerminalItem_DoesNotEmitTerminalMetric()
    {
        // Arrange: a Failed K8s Job whose work item is already Cancelled (no-op path).
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

        // PostStatusAsync returns false: HTTP 204 No Content — the work item was already terminal
        // (e.g. Cancelled), so the API returns the idempotent no-op signal (issue #2802).
        _workItemClient.Setup(c => c.PostStatusAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Baseline: capture counts before the act so we isolate this test's emissions from
        // parallel tests that share the static meters.
        // TODO: The delta-count approach below is fragile when tests run in parallel: a concurrent
        // test emitting a workdistribution.workitems_terminated measurement between the Before
        // and After captures can produce a negative delta, which trivially satisfies Be(0) while
        // masking test pollution. The anti-vacuity assertion in Assert 1 partially mitigates this.
        // This is a pre-existing pattern in the class (not introduced by this PR). See review
        // finding (Correctness/TestQualityReviewer) for issue #2802.
        // TODO: The pipeline.jobs.failed counter (pipeline.jobs.failed) assertion below relies on
        // that counter being emitted from within the same if (transitioned) guard as
        // WorkDistributionTelemetry.LogTerminalStatus. If pipeline.jobs.failed is ever decoupled
        // from LogTerminalStatus and emitted outside the guard, the assertion would silently stop
        // covering it. Consider an explicit comment or test linking these meters if decoupling
        // is ever considered. See review finding (TestQualityReviewer) for issue #2802.
        var terminatedCountBefore = _recordings.Count(r => r.InstrumentName == "workdistribution.workitems_terminated");
        var durationCountBefore = _recordings.Count(r => r.InstrumentName == "workdistribution.job_execution_duration_seconds");
        var failedCountBefore = _pipelineCounters.Count(r => r.InstrumentName == "pipeline.jobs.failed");

        // Act
        var loop = CreateLoop();
        await loop.ReconcileOnceAsync(CancellationToken.None);

        // Assert 1: anti-vacuity — PostStatusAsync was called once (the no-op path was reached)
        _workItemClient.Verify(c => c.PostStatusAsync(
            id,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Failed"),
            It.IsAny<CancellationToken>()), Times.Once,
            "PostStatusAsync must still be called — the no-op must not skip the HTTP call");

        // Assert 2: no terminal metrics emitted (the primary assertion for issue #2802)
        var terminatedCountAfter = _recordings.Count(r => r.InstrumentName == "workdistribution.workitems_terminated");
        var durationCountAfter = _recordings.Count(r => r.InstrumentName == "workdistribution.job_execution_duration_seconds");
        var failedCountAfter = _pipelineCounters.Count(r => r.InstrumentName == "pipeline.jobs.failed");

        (terminatedCountAfter - terminatedCountBefore).Should().Be(0,
            "workdistribution.workitems_terminated must NOT be incremented when PostStatusAsync returns false (no-op)");
        (durationCountAfter - durationCountBefore).Should().Be(0,
            "workdistribution.job_execution_duration_seconds must NOT be recorded when PostStatusAsync returns false (no-op)");
        (failedCountAfter - failedCountBefore).Should().Be(0,
            "pipeline.jobs.failed must NOT be incremented when PostStatusAsync returns false (no-op)");
    }
}
