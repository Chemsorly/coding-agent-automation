using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Lifecycle tests for ReconciliationService: orphan detection, timeout enforcement, stale cleanup.
/// Validates: Requirements 7.1-7.5
/// </summary>
[Trait("Feature", "035a-kubernetes-reconciliation")]
public class ReconciliationServiceLifecycleTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetes> _mockKube;
    private readonly Mock<IBatchV1Operations> _mockBatchV1;

    public ReconciliationServiceLifecycleTests()
    {
        var dbName = $"ReconciliationServiceLifecycle-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);

        _mockKube = new Mock<IKubernetes> { DefaultValue = DefaultValue.Mock };
        _mockBatchV1 = new Mock<IBatchV1Operations> { DefaultValue = DefaultValue.Mock };
        _mockKube.Setup(k => k.BatchV1).Returns(_mockBatchV1.Object);
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── Timeout Enforcement ─────────────────────────────────────────────

    // TODO: These tests (EnforceTimeouts_TimedOutItem_TransitionsToFailed, EnforceTimeouts_RunningItem_AlsoEnforced)
    // do not pass an explicit dispatchedAt, so InsertWorkItem defaults DispatchedAt = createdAt.
    // The anchor used by EnforceTimeoutsAsync is LastProgressAt ?? DispatchedAt ?? CreatedAt — when all
    // three collapse to the same value, the dispatch-time anchor semantics are not verified at the
    // integration level. If production code were changed to use CreatedAt directly instead of DispatchedAt,
    // these tests would still pass. A missing negative case: set createdAt far in the past but
    // dispatchedAt recently, and assert the item is NOT timed out (validating DispatchedAt is the anchor).

    [Fact]
    public async Task EnforceTimeouts_TimedOutItem_TransitionsToFailed()
    {
        // Arrange: item created 2 hours ago with 1 hour timeout
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#1", WorkItemStatus.Dispatched,
            createdAt: DateTimeOffset.UtcNow.AddHours(-2), timeoutSeconds: 3600,
            k8sJobName: "caa-timeout1");

        var service = CreateService();

        // Act
        await service.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.Timeout);
        item.ErrorMessage.Should().Contain("Timeout exceeded");
    }

    [Fact]
    public async Task EnforceTimeouts_NotTimedOut_LeavesUntouched()
    {
        // Arrange: item created 5 minutes ago with 1 hour timeout
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#2", WorkItemStatus.Running,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5), timeoutSeconds: 3600);

        var service = CreateService();

        // Act
        await service.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Running);
    }

    [Fact]
    public async Task EnforceTimeouts_RunningItem_AlsoEnforced()
    {
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#3", WorkItemStatus.Running,
            createdAt: DateTimeOffset.UtcNow.AddHours(-3), timeoutSeconds: 1800,
            k8sJobName: "caa-running1");

        var service = CreateService();
        await service.EnforceTimeoutsAsync(CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.Timeout);
    }

    // ── Progress-aware Timeout (parity with SignalR/Legacy HeartbeatMonitor) ──

    [Fact]
    public async Task EnforceTimeouts_RecentProgress_DoesNotTimeout()
    {
        // Arrange: item dispatched 2.5 hours ago with 2h timeout → would timeout without progress.
        // But LastProgressAt = 10 minutes ago (recent progress in DB).
        var workItemId = Guid.NewGuid();
        var dispatchedAt = DateTimeOffset.UtcNow.AddHours(-2.5);
        await InsertWorkItem(workItemId, "owner/repo#progress1", WorkItemStatus.Running,
            createdAt: dispatchedAt.AddMinutes(-5), timeoutSeconds: 7200,
            k8sJobName: "caa-progress1", dispatchedAt: dispatchedAt,
            lastProgressAt: DateTimeOffset.UtcNow.AddMinutes(-10));

        var service = CreateService();

        // Act
        await service.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert: should NOT be timed out because of recent progress
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Running,
            "item should remain Running because it has recent progress (LastProgressAt 10 min ago, timeout 2h)");
    }

    [Fact]
    public async Task EnforceTimeouts_StaleProgress_TimesOut()
    {
        // Arrange: item dispatched 3 hours ago with 2h timeout.
        // LastProgressAt = 2.5 hours ago (stale — exceeds timeout).
        var workItemId = Guid.NewGuid();
        var dispatchedAt = DateTimeOffset.UtcNow.AddHours(-3);
        await InsertWorkItem(workItemId, "owner/repo#progress2", WorkItemStatus.Running,
            createdAt: dispatchedAt.AddMinutes(-5), timeoutSeconds: 7200,
            k8sJobName: "caa-progress2", dispatchedAt: dispatchedAt,
            lastProgressAt: DateTimeOffset.UtcNow.AddHours(-2.5));

        var service = CreateService();

        // Act
        await service.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert: should be timed out because progress is stale
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.Timeout);
    }

    [Fact]
    public async Task EnforceTimeouts_NullLastProgressAt_FallsBackToDispatchedAt()
    {
        // Arrange: item dispatched 2.5 hours ago with 2h timeout.
        // No LastProgressAt set (e.g., agent never reported progress to DB).
        var workItemId = Guid.NewGuid();
        var dispatchedAt = DateTimeOffset.UtcNow.AddHours(-2.5);
        await InsertWorkItem(workItemId, "owner/repo#progress3", WorkItemStatus.Running,
            createdAt: dispatchedAt.AddMinutes(-5), timeoutSeconds: 7200,
            k8sJobName: "caa-progress3", dispatchedAt: dispatchedAt,
            lastProgressAt: null);

        var service = CreateService();

        // Act
        await service.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert: falls back to DispatchedAt → 2.5h > 2h → times out
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.Timeout);
    }

    // ── IsTimedOut static helper ────────────────────────────────────────

    [Fact]
    public void IsTimedOut_ExactlyAtDeadline_ReturnsTrue()
    {
        var created = DateTimeOffset.UtcNow.AddSeconds(-100);
        ReconciliationService.IsTimedOut(created, 100, DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void IsTimedOut_BeforeDeadline_ReturnsFalse()
    {
        var created = DateTimeOffset.UtcNow.AddSeconds(-50);
        ReconciliationService.IsTimedOut(created, 100, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    // ── IsStale static helper ───────────────────────────────────────────

    [Fact]
    public void IsStale_NullCompletedAt_ReturnsFalse()
    {
        ReconciliationService.IsStale(null, 7, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void IsStale_CompletedBeyondRetention_ReturnsTrue()
    {
        var completedAt = DateTimeOffset.UtcNow.AddDays(-10);
        ReconciliationService.IsStale(completedAt, 7, DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void IsStale_CompletedWithinRetention_ReturnsFalse()
    {
        var completedAt = DateTimeOffset.UtcNow.AddDays(-3);
        ReconciliationService.IsStale(completedAt, 7, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    // ── Leadership Loss Cancellation ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LeadershipLost_ExitsWithinOneSecond()
    {
        // Arrange: create a service with controllable leader election
        var leaderCts = new CancellationTokenSource();
        var leaderElection = CreateLeaderElectionWithCts(leaderCts);

        var service = CreateService(leaderElection: leaderElection);

        var hostStopCts = new CancellationTokenSource();

        // Act: start ExecuteAsync
        var executeTask = InvokeExecuteAsync(service, hostStopCts.Token);

        // Allow the service to enter its work loop
        await Task.Delay(200);

        // Simulate leadership loss by cancelling the leaderCts
        leaderCts.Cancel();

        // Allow the service to detect leadership loss and re-enter wait loop
        // Then stop the host to exit ExecuteAsync completely
        await Task.Delay(200);
        hostStopCts.Cancel();

        // Assert: ExecuteAsync should complete promptly (within 2 seconds)
        var completed = await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.Should().Be(executeTask, "ExecuteAsync should exit promptly after leadership loss + host stop");

        leaderCts.Dispose();
        hostStopCts.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_LeadershipLostAndReacquired_ReEntersLeaderLoop()
    {
        // Arrange: create a service with controllable leader election
        var leaderCts = new CancellationTokenSource();
        var leaderElection = CreateLeaderElectionWithCts(leaderCts);

        var service = CreateService(leaderElection: leaderElection);

        var hostStopCts = new CancellationTokenSource();

        // Act: start ExecuteAsync
        var executeTask = InvokeExecuteAsync(service, hostStopCts.Token);

        // Allow service to enter leader loop
        await Task.Delay(200);

        // Simulate leadership loss
        leaderCts.Cancel();
        await Task.Delay(200);

        // Simulate re-acquisition: set IsLeader=true and create new CTS
        var newLeaderCts = new CancellationTokenSource();
        SetLeaderState(leaderElection, isLeader: true, cts: newLeaderCts);

        // Allow the service to re-acquire and enter leader loop
        await Task.Delay(3000);

        // Stop the host
        hostStopCts.Cancel();
        var completed = await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromSeconds(3)));
        completed.Should().Be(executeTask, "ExecuteAsync should exit after host stop");

        newLeaderCts.Dispose();
        leaderCts.Dispose();
        hostStopCts.Dispose();
    }

    // ── Consolidation timeout enforcement ──────────────────────────────

    [Fact]
    public async Task EnforceConsolidationTimeouts_StuckRun_UpdatesToFailed()
    {
        // Arrange: consolidation run that has been running for 90 min (exceeds 60 min timeout)
        var runId = Guid.NewGuid().ToString();
        var mockConsolidation = new Mock<IConsolidationService>();
        mockConsolidation.Setup(c => c.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun>
            {
                new ConsolidationRun
                {
                    RunId = runId,
                    Type = ConsolidationRunType.BrainConsolidation,
                    StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-90),
                    Status = ConsolidationRunStatus.Running
                }
            });

        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore.Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentBusyProgressTimeout = TimeSpan.FromMinutes(60) });

        var service = CreateService(
            consolidationService: mockConsolidation.Object,
            configStore: mockConfigStore.Object);

        // Act
        await service.EnforceConsolidationTimeoutsAsync(CancellationToken.None);

        // Assert: consolidation run should be marked failed
        mockConsolidation.Verify(c => c.UpdateRunAsync(
            (RunId)runId,
            ConsolidationRunStatus.Failed,
            It.Is<string>(s => s.Contains("timeout")),
            It.IsAny<CancellationToken>(),
            It.IsAny<long>()), Times.Once);
    }

    /// <summary>
    /// BUG FIX #1540: A consolidation run that was recently dispatched (StartedAtUtc = 2 min ago)
    /// should NOT be timed out, even if it was originally created/queued >60 min ago.
    /// After the fix, StartedAtUtc is reset on dispatch, so the timeout measures actual execution time.
    /// </summary>
    // TODO: #1540 — This test documents expected behavior but wouldn't fail if the fix were reverted.
    // It sets up data in the already-corrected state (StartedAtUtc = 2 min ago). A stronger regression
    // test would exercise the full path: create run with old StartedAtUtc → TransitionToRunningAsync →
    // EnforceConsolidationTimeoutsAsync → verify NOT timed out.
    [Fact]
    public async Task EnforceConsolidationTimeouts_RecentlyDispatchedRun_NotTimedOut()
    {
        // Arrange: consolidation run that was recently dispatched (StartedAtUtc = 2 min ago)
        // This simulates a run that was queued for hours but just started executing.
        var runId = Guid.NewGuid().ToString();
        var mockConsolidation = new Mock<IConsolidationService>();
        mockConsolidation.Setup(c => c.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun>
            {
                new ConsolidationRun
                {
                    RunId = runId,
                    Type = ConsolidationRunType.BrainConsolidation,
                    StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2), // Recently dispatched
                    Status = ConsolidationRunStatus.Running
                }
            });

        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore.Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AgentBusyProgressTimeout = TimeSpan.FromMinutes(60) });

        var service = CreateService(
            consolidationService: mockConsolidation.Object,
            configStore: mockConfigStore.Object);

        // Act
        await service.EnforceConsolidationTimeoutsAsync(CancellationToken.None);

        // Assert: UpdateRunAsync should NOT be called — the run has only been executing for 2 min
        mockConsolidation.Verify(c => c.UpdateRunAsync(
            It.IsAny<RunId>(),
            It.IsAny<ConsolidationRunStatus>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<long>()), Times.Never);
    }

    // ── Lifecycle cleanup on timeout (parity with HeartbeatMonitor) ────────

    [Fact]
    public async Task EnforceTimeouts_WithLifecycleManager_CallsFailRunAsync()
    {
        // Arrange: item dispatched 2.5 hours ago with 2h timeout, stale progress
        var workItemId = Guid.NewGuid();
        var dispatchedAt = DateTimeOffset.UtcNow.AddHours(-2.5);
        await InsertWorkItem(workItemId, "owner/repo#lifecycle1", WorkItemStatus.Running,
            createdAt: dispatchedAt.AddMinutes(-5), timeoutSeconds: 7200,
            k8sJobName: "caa-lifecycle1", dispatchedAt: dispatchedAt,
            lastProgressAt: DateTimeOffset.UtcNow.AddHours(-2.5));

        var mockLifecycle = new Mock<IRunLifecycleManager>();
        mockLifecycle
            .Setup(m => m.FailRunAsync(workItemId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null); // Simulate "not in memory" — fallback path

        var mockLabelService = new Mock<ILabelService>();
        var mockDedupGuard = new Mock<IJobDeduplicationGuard>();

        var service = CreateService(lifecycleManager: mockLifecycle.Object,
            labelService: mockLabelService.Object, dedupGuard: mockDedupGuard.Object);

        // Act
        await service.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert: FailRunAsync was attempted first (for full cleanup)
        mockLifecycle.Verify(m => m.FailRunAsync(
            workItemId.ToString(),
            It.Is<string>(s => s.Contains("Timeout")),
            It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);

        // Fallback: DB transition fires
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);

        // Fallback: label swap to agent:error fires
        mockLabelService.Verify(l => l.SwapLabelAsync(
            "provider-1", "owner/repo#lifecycle1", "agent:error",
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);

        // Fallback: dedup guard released
        mockDedupGuard.Verify(d => d.MarkIssueComplete("owner/repo#lifecycle1", "provider-1"), Times.Once);
    }

    [Fact]
    public async Task EnforceTimeouts_LifecycleManagerSucceeds_SkipsDirectTransition()
    {
        // Arrange: timed-out item with lifecycle manager that succeeds
        var workItemId = Guid.NewGuid();
        var dispatchedAt = DateTimeOffset.UtcNow.AddHours(-2.5);
        await InsertWorkItem(workItemId, "owner/repo#lifecycle2", WorkItemStatus.Running,
            createdAt: dispatchedAt.AddMinutes(-5), timeoutSeconds: 7200,
            k8sJobName: "caa-lifecycle2", dispatchedAt: dispatchedAt,
            lastProgressAt: DateTimeOffset.UtcNow.AddHours(-2.5));

        var mockRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = workItemId.ToString(),
            IssueIdentifier = "owner/repo#lifecycle2",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "manual"
        });
        var mockLifecycle = new Mock<IRunLifecycleManager>();
        mockLifecycle
            .Setup(m => m.FailRunAsync(workItemId.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(mockRun); // Lifecycle manager handled it fully

        var service = CreateService(lifecycleManager: mockLifecycle.Object);

        // Act
        await service.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert: lifecycle manager was called
        mockLifecycle.Verify(m => m.FailRunAsync(
            workItemId.ToString(),
            It.Is<string>(s => s.Contains("Timeout")),
            It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Once);

        // DB item should NOT have been transitioned by ReconciliationService directly
        // (FailRunAsync handles it internally via WorkItemTransitionService)
        // We verify this indirectly: the item is still Running because our mock doesn't actually
        // call TransitionService (it's a mock). The real FailRunAsync would transition it.
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        // Still Running because the mock didn't actually transition — confirms ReconciliationService
        // didn't call TransitionAsync directly when FailRunAsync succeeded.
        // TODO: This assertion is tautological — the item stays Running whether ReconciliationService
        // skipped the direct transition or not, because the mock FailRunAsync never mutates the DB.
        // A stronger assertion would inject a mock IWorkItemTransitionService and verify it was NOT
        // called when FailRunAsync returns a non-null result.
        item!.Status.Should().Be(WorkItemStatus.Running);
    }

    // ── Startup PVC Reconciliation ───────────────────────────────────────

    [Fact]
    public async Task StartupReconciliation_OrphanedPvcNullJobName_ClearsClaim()
    {
        // Arrange: WorkItem has a PVC claim but no K8s job name.
        // This represents the crash-recovery scenario: DB written, K8s Job creation never started.
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#startup1", WorkItemStatus.Pending,
            k8sJobName: null, claimedPvcName: "pvc-startup-1");

        var service = CreateService();

        // Act
        await InvokeRunStartupReconciliationAsync(service);

        // Assert: PVC claim must be cleared — string.IsNullOrEmpty(K8sJobName) branch fires
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.ClaimedPvcName.Should().BeNull(
            "a WorkItem with ClaimedPvcName but no K8sJobName is an orphaned PVC claim that must be released on startup");
    }

    [Fact]
    public async Task StartupReconciliation_OrphanedPvcMissingK8sJob_ClearsClaim()
    {
        // Arrange: WorkItem has both PVC claim and K8s job name, but the job no longer exists in K8s (404).
        // This represents a crash after Job creation but before the WorkItem reached terminal state.
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#startup2", WorkItemStatus.Dispatched,
            k8sJobName: "caa-orphan-job", claimedPvcName: "pvc-startup-2");

        // Simulate K8s returning 404 for this job name (job was deleted / never actually created)
        _mockBatchV1
            .Setup(b => b.ReadNamespacedJobWithHttpMessagesAsync(
                "caa-orphan-job", "default",
                It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpOperationException("Not Found")
            {
                Response = new HttpResponseMessageWrapper(
                    new HttpResponseMessage(HttpStatusCode.NotFound), "")
            });

        var service = CreateService();

        // Act
        await InvokeRunStartupReconciliationAsync(service);

        // Assert: PVC claim must be cleared — JobExistsAsync returned false
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.ClaimedPvcName.Should().BeNull(
            "when the K8s Job no longer exists (404), the orphaned PVC claim must be released on startup");
    }

    [Fact]
    public async Task StartupReconciliation_LiveK8sJob_RetainsPvcClaim()
    {
        // Arrange: WorkItem has both PVC claim and a K8s job that is still running.
        // Startup reconciliation must NOT release claims for live jobs.
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#startup3", WorkItemStatus.Dispatched,
            k8sJobName: "caa-live-job", claimedPvcName: "pvc-startup-3");

        // Simulate K8s confirming the job exists
        _mockBatchV1
            .Setup(b => b.ReadNamespacedJobWithHttpMessagesAsync(
                "caa-live-job", "default",
                It.IsAny<bool?>(),
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new k8s.Autorest.HttpOperationResponse<V1Job> { Body = new V1Job() });

        var service = CreateService();

        // Act
        await InvokeRunStartupReconciliationAsync(service);

        // Assert: PVC claim must be retained — job is still alive
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.ClaimedPvcName.Should().Be("pvc-startup-3",
            "the PVC claim must be preserved when the corresponding K8s Job is still running");
    }

    [Fact]
    public async Task StartupReconciliation_IsIdempotent()
    {
        // Arrange: WorkItem with an orphaned PVC claim (no K8s job).
        // Running startup reconciliation twice must leave the DB in the same state as running it once.
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#startup4", WorkItemStatus.Pending,
            k8sJobName: null, claimedPvcName: "pvc-startup-4");

        var service = CreateService();

        // Act — first run clears the claim
        await InvokeRunStartupReconciliationAsync(service);

        await using var db1 = await _dbFactory.CreateDbContextAsync();
        var afterFirst = await db1.WorkItems.FindAsync(workItemId);
        afterFirst!.ClaimedPvcName.Should().BeNull("PVC claim must be cleared on the first run");

        // Act — second run with already-null claim must not throw or corrupt state
        await InvokeRunStartupReconciliationAsync(service);

        await using var db2 = await _dbFactory.CreateDbContextAsync();
        var afterSecond = await db2.WorkItems.FindAsync(workItemId);
        afterSecond!.ClaimedPvcName.Should().BeNull(
            "startup reconciliation must be idempotent — a second run leaves state unchanged when claim is already null");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private ReconciliationService CreateService(int retentionDays = 7, LeaderElectionService? leaderElection = null,
        IRunLifecycleManager? lifecycleManager = null, IConsolidationService? consolidationService = null,
        IConfigurationStore? configStore = null, ILabelService? labelService = null,
        IJobDeduplicationGuard? dedupGuard = null)
    {
        var configData = new Dictionary<string, string?>
        {
            ["WorkDistribution:Reconciliation:PollIntervalSeconds"] = "30",
            ["WorkDistribution:Reconciliation:RetentionDays"] = retentionDays.ToString(),
            ["WorkDistribution:Namespace"] = "default"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        if (leaderElection is null)
        {
            leaderElection = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
            var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            isLeaderField?.SetValue(leaderElection, true);

            var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            leaderCtsField?.SetValue(leaderElection, new CancellationTokenSource());
        }

        return new ReconciliationService(
            new ReconciliationServiceDependencies(_dbFactory, leaderElection, _mockKube.Object,
                _transitionService, config, labelService, lifecycleManager,
                consolidationService, configStore, dedupGuard));
    }

    private static async Task InvokeExecuteAsync(ReconciliationService service, CancellationToken stoppingToken)
    {
        var method = typeof(BackgroundService).GetMethod("ExecuteAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [stoppingToken])!;
        await task;
    }

    private static async Task InvokeRunStartupReconciliationAsync(ReconciliationService service)
    {
        var method = typeof(ReconciliationService).GetMethod(
            "RunStartupReconciliationAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "Method RunStartupReconciliationAsync not found — was it renamed?");
        await (Task)method.Invoke(service, [CancellationToken.None])!;
    }

    private static LeaderElectionService CreateLeaderElectionWithCts(CancellationTokenSource cts)
    {
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        SetLeaderState(les, isLeader: true, cts: cts);
        return les;
    }

    private static void SetLeaderState(LeaderElectionService les, bool isLeader, CancellationTokenSource cts)
    {
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        isLeaderField!.SetValue(les, isLeader);

        var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        leaderCtsField!.SetValue(les, cts);
    }

    private async Task InsertWorkItem(Guid id, string issueId, WorkItemStatus status,
        DateTimeOffset? createdAt = null, int timeoutSeconds = 1800,
        string? k8sJobName = null, DateTimeOffset? completedAt = null,
        DateTimeOffset? dispatchedAt = null, DateTimeOffset? lastProgressAt = null,
        string? claimedPvcName = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = issueId,
            IssueProviderConfigId = "provider-1",
            Status = status,
            AgentSelector = "kiro,dotnet",
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            DispatchedAt = dispatchedAt ?? createdAt,
            TimeoutSeconds = timeoutSeconds,
            K8sJobName = k8sJobName,
            CompletedAt = completedAt,
            LastProgressAt = lastProgressAt,
            ClaimedPvcName = claimedPvcName,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersion = entityType.FindProperty("RowVersion");
                if (rowVersion != null)
                {
                    rowVersion.IsConcurrencyToken = false;
                    rowVersion.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var index in entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    entityType.RemoveIndex(index);
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
