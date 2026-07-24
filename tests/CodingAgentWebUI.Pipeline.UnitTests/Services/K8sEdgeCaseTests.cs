using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Reflection;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// K8s mode edge case tests covering scenarios not exercised by the main lifecycle tests:
/// - PVC pool exhaustion (all PVCs claimed → item stays Pending)
/// - ReconciliationService handling of Complete Job with non-terminal WorkItem (#1138 gap)
/// - Agent POST failure recovery via reconciliation timeout
/// - Race condition between agent POST and reconciliation timeout
/// </summary>
[Trait("Feature", "035a-kubernetes-edge-cases")]
public class K8sEdgeCaseTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<IKubernetesJobClient> _mockKubeClient;
    private readonly Mock<IKubernetes> _mockKube;
    private readonly Mock<IBatchV1Operations> _mockBatchV1;

    public K8sEdgeCaseTests()
    {
        var dbName = $"K8sEdgeCase-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _mockKubeClient = new Mock<IKubernetesJobClient>();
        _mockKube = new Mock<IKubernetes> { DefaultValue = DefaultValue.Mock };
        _mockBatchV1 = new Mock<IBatchV1Operations> { DefaultValue = DefaultValue.Mock };
        _mockKube.Setup(k => k.BatchV1).Returns(_mockBatchV1.Object);
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PVC Pool Exhaustion: kiro items stay Pending when all PVCs claimed
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PollAndDispatch_AllPvcsClaimed_KiroItemStaysPending()
    {
        // Arrange: 2 PVCs in the pool, both claimed by Running items
        var running1 = Guid.NewGuid();
        var running2 = Guid.NewGuid();
        await InsertWorkItem(running1, "owner/repo#running1", "kiro,dotnet", WorkItemStatus.Running,
            claimedPvc: "pvc-test-1");
        await InsertWorkItem(running2, "owner/repo#running2", "kiro,dotnet", WorkItemStatus.Running,
            claimedPvc: "pvc-test-2");

        // Insert a Pending item that needs a PVC
        var pendingId = Guid.NewGuid();
        await InsertWorkItem(pendingId, "owner/repo#needs-pvc", "kiro,dotnet", WorkItemStatus.Pending);

        var service = CreateDispatchService(pvcPool: new[] { "pvc-test-1", "pvc-test-2" });

        // Act
        await InvokePollAndDispatch(service);

        // Assert: Pending item is NOT dispatched (no PVC available) and NOT failed
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(pendingId);
        item!.Status.Should().Be(WorkItemStatus.Pending,
            "Kiro item should stay Pending when all PVCs are claimed — not dispatched, not failed");
        item.K8sJobName.Should().BeNull("No K8s Job should be created without a PVC");

        // No K8s Job creation attempted
        _mockKubeClient.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PollAndDispatch_PvcFreed_NextPollDispatches()
    {
        // Arrange: 1 PVC in pool, initially claimed
        var runningId = Guid.NewGuid();
        await InsertWorkItem(runningId, "owner/repo#done", "kiro,dotnet", WorkItemStatus.Running,
            claimedPvc: "pvc-single");

        var pendingId = Guid.NewGuid();
        await InsertWorkItem(pendingId, "owner/repo#waiting", "kiro,dotnet", WorkItemStatus.Pending);

        var service = CreateDispatchService(pvcPool: new[] { "pvc-single" });

        // First poll: PVC claimed → stays Pending
        await InvokePollAndDispatch(service);
        await using (var db1 = await _dbFactory.CreateDbContextAsync())
        {
            var item1 = await db1.WorkItems.FindAsync(pendingId);
            item1!.Status.Should().Be(WorkItemStatus.Pending);
        }

        // Free the PVC: transition running item to Succeeded (releases claim)
        await using (var db2 = await _dbFactory.CreateDbContextAsync())
        {
            var runningItem = await db2.WorkItems.FindAsync(runningId);
            runningItem!.Status = WorkItemStatus.Succeeded;
            runningItem.CompletedAt = DateTimeOffset.UtcNow;
            runningItem.ClaimedPvcName = null;
            await db2.SaveChangesAsync();
        }

        // Mock K8s to accept the Job creation
        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Second poll: PVC free → dispatches
        await InvokePollAndDispatch(service);
        await using var db3 = await _dbFactory.CreateDbContextAsync();
        var item3 = await db3.WorkItems.FindAsync(pendingId);
        item3!.Status.Should().Be(WorkItemStatus.Dispatched,
            "Item should be dispatched once PVC becomes available");
        item3.ClaimedPvcName.Should().Be("pvc-single");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Reconciliation: Timeout catches items where agent never POSTed status
    // (Simulates agent crash/OOM where no terminal status arrives)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnforceTimeouts_DispatchedItemPastTimeout_TransitionsToFailed()
    {
        // Scenario: DispatchService created Job 2 hours ago, agent pod was OOM-killed,
        // never POSTed any status. ReconciliationService timeout detects and fails it.
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#oom-killed", "kiro,dotnet", WorkItemStatus.Dispatched,
            createdAt: DateTimeOffset.UtcNow.AddHours(-2),
            timeoutSeconds: 3600,
            k8sJobName: "caa-oom-victim");

        // Act: verify timeout detection
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId);
        var anchor = item!.DispatchedAt ?? item.CreatedAt;
        var isTimedOut = ReconciliationService.IsTimedOut(anchor, item.TimeoutSeconds, DateTimeOffset.UtcNow);

        isTimedOut.Should().BeTrue("item created 2h ago with 1h timeout should be timed out");

        // Transition (same as ReconciliationService does)
        var transitioned = await _transitionService.TransitionAsync(workItemId, WorkItemStatus.Failed,
            w =>
            {
                w.CompletedAt = DateTimeOffset.UtcNow;
                w.FailureReason = FailureReason.Timeout;
                w.ErrorMessage = "Timeout exceeded: agent never reported status (possible OOM/SIGKILL)";
            }, CancellationToken.None);

        // Assert
        transitioned.Should().BeTrue();
        await using var verifyDb = await _dbFactory.CreateDbContextAsync();
        var failed = await verifyDb.WorkItems.FindAsync(workItemId);
        failed!.Status.Should().Be(WorkItemStatus.Failed);
        failed.FailureReason.Should().Be(FailureReason.Timeout);
        failed.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task EnforceTimeouts_RunningItemPastTimeout_AlsoFails()
    {
        // Scenario: Agent POSTed Running but then got killed — never POSTed terminal.
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#stuck-running", "kiro,dotnet", WorkItemStatus.Running,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-90),
            timeoutSeconds: 3600,
            k8sJobName: "caa-stuck-runner",
            assignedAgentId: "caa-pod-abc");

        var isTimedOut = ReconciliationService.IsTimedOut(
            DateTimeOffset.UtcNow.AddMinutes(-90), 3600, DateTimeOffset.UtcNow);
        isTimedOut.Should().BeTrue();

        var transitioned = await _transitionService.TransitionAsync(workItemId, WorkItemStatus.Failed,
            w =>
            {
                w.CompletedAt = DateTimeOffset.UtcNow;
                w.FailureReason = FailureReason.Timeout;
                w.ErrorMessage = "Timeout exceeded: agent Running but no completion within deadline";
            }, CancellationToken.None);

        transitioned.Should().BeTrue();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.Timeout);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Regression: timeout must use DispatchedAt, not CreatedAt
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnforceTimeouts_QueuedItemRecentlyDispatched_ShouldNotTimeout()
    {
        // Regression test for the bug where items queued for 2h+ were immediately
        // killed after dispatch because timeout was measured from CreatedAt.
        // The fix: timeout must measure from DispatchedAt (when execution starts).
        var workItemId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-3); // created 3h ago
        var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-5); // dispatched only 5 min ago

        await InsertWorkItem(workItemId, "owner/repo#queued-long", "kiro,dotnet", WorkItemStatus.Dispatched,
            createdAt: createdAt,
            timeoutSeconds: 7200, // 2h timeout
            k8sJobName: "caa-queued-long",
            dispatchedAt: dispatchedAt);

        // Act: check if ReconciliationService would timeout this item
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId);

        // The timeout anchor must be DispatchedAt, not CreatedAt
        var anchor = item!.DispatchedAt ?? item.CreatedAt;
        var isTimedOut = ReconciliationService.IsTimedOut(anchor, item.TimeoutSeconds, DateTimeOffset.UtcNow);

        isTimedOut.Should().BeFalse(
            "item was dispatched only 5 minutes ago — timeout should measure from dispatch time, not creation time");
    }

    [Fact]
    public async Task EnforceTimeouts_QueuedItemDispatchedPastTimeout_ShouldTimeout()
    {
        // Counterpart: item created long ago AND dispatched long ago — should timeout.
        var workItemId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-5);
        var dispatchedAt = DateTimeOffset.UtcNow.AddHours(-3); // dispatched 3h ago

        await InsertWorkItem(workItemId, "owner/repo#dispatched-long", "kiro,dotnet", WorkItemStatus.Running,
            createdAt: createdAt,
            timeoutSeconds: 7200, // 2h timeout — 3h since dispatch exceeds this
            k8sJobName: "caa-dispatched-long",
            assignedAgentId: "caa-pod-xyz",
            dispatchedAt: dispatchedAt);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId);

        var anchor = item!.DispatchedAt ?? item.CreatedAt;
        var isTimedOut = ReconciliationService.IsTimedOut(anchor, item.TimeoutSeconds, DateTimeOffset.UtcNow);

        isTimedOut.Should().BeTrue(
            "item was dispatched 3h ago with 2h timeout — should be timed out");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Race: concurrent timeout + agent POST — item reaches terminal state
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConcurrentTransition_ReconciliationAndAgentPost_BothReachTerminal()
    {
        // Scenario: Agent POSTs Succeeded while ReconciliationService simultaneously
        // tries to transition to Failed (timeout). Both attempt — item ends terminal.
        var workItemId = Guid.NewGuid();
        await InsertWorkItem(workItemId, "owner/repo#race", "kiro,dotnet", WorkItemStatus.Running,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-61),
            timeoutSeconds: 3600,
            k8sJobName: "caa-race-test");

        var agentTask = _transitionService.TransitionAsync(workItemId, WorkItemStatus.Succeeded,
            w => w.CompletedAt = DateTimeOffset.UtcNow, CancellationToken.None);
        var reconciliationTask = _transitionService.TransitionAsync(workItemId, WorkItemStatus.Failed,
            w =>
            {
                w.CompletedAt = DateTimeOffset.UtcNow;
                w.FailureReason = FailureReason.Timeout;
            }, CancellationToken.None);

        await Task.WhenAll(agentTask, reconciliationTask);

        // Item must be in a terminal state (no corruption)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().BeOneOf(
            new[] { WorkItemStatus.Succeeded, WorkItemStatus.Failed },
            because: "Item must reach a terminal state regardless of race outcome");
        item.CompletedAt.Should().NotBeNull();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Complete Job + non-terminal WorkItem (#1138 fix)
    // Watch handler: HandleJobCompletionAsync detects stuck WorkItems
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HandleJobCompletion_CompleteJob_DispatchedWorkItem_PastGracePeriod_TransitionsToFailed()
    {
        // Scenario: K8s Job completed >30s ago but WorkItem is still Dispatched
        // (agent crashed with exit 0, never POSTed terminal status).
        var workItemId = Guid.NewGuid();
        var k8sJobName = $"caa-{workItemId.ToString("N")[..8]}";
        await InsertWorkItem(workItemId, "owner/repo#stuck-dispatched", "kiro,dotnet", WorkItemStatus.Dispatched,
            k8sJobName: k8sJobName);

        var completeJob = CreateCompleteJob(k8sJobName, workItemId,
            completionTime: DateTime.UtcNow.AddSeconds(-60)); // Completed 60s ago (past 30s grace)

        var reconciliationService = CreateReconciliationService();

        // Act
        await InvokeHandleJobEventAsync(reconciliationService, WatchEventType.Modified, completeJob);

        // Assert: WorkItem transitioned to Failed
        // TODO: Also verify PVC release (item.ClaimedPvcName == null) and K8s Job deletion (mock verify TryDeleteJobAsync) to match acceptance criteria.
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
        item.ErrorMessage.Should().Contain("agent never reported terminal status");
        item.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleJobCompletion_CompleteJob_SucceededWorkItem_NoOp()
    {
        // Scenario: K8s Job completed AND agent reported Succeeded (normal happy path).
        // ReconciliationService should do nothing.
        var workItemId = Guid.NewGuid();
        var k8sJobName = $"caa-{workItemId.ToString("N")[..8]}";
        await InsertWorkItem(workItemId, "owner/repo#succeeded-ok", "kiro,dotnet", WorkItemStatus.Succeeded,
            k8sJobName: k8sJobName,
            completedAt: DateTimeOffset.UtcNow.AddSeconds(-10));

        var completeJob = CreateCompleteJob(k8sJobName, workItemId,
            completionTime: DateTime.UtcNow.AddSeconds(-60)); // Completed 60s ago

        var reconciliationService = CreateReconciliationService();

        // Act
        await InvokeHandleJobEventAsync(reconciliationService, WatchEventType.Modified, completeJob);

        // Assert: WorkItem stays Succeeded (no transition to Failed)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Succeeded);
        item.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task HandleJobCompletion_CompleteJob_DispatchedWorkItem_WithinGracePeriod_NoTransition()
    {
        // Scenario: K8s Job completed just 10s ago — still within the 30s grace period.
        // Agent's POST may still be in flight. Should NOT transition yet.
        var workItemId = Guid.NewGuid();
        var k8sJobName = $"caa-{workItemId.ToString("N")[..8]}";
        await InsertWorkItem(workItemId, "owner/repo#within-grace", "kiro,dotnet", WorkItemStatus.Dispatched,
            k8sJobName: k8sJobName);

        var completeJob = CreateCompleteJob(k8sJobName, workItemId,
            completionTime: DateTime.UtcNow.AddSeconds(-10)); // Completed only 10s ago

        var reconciliationService = CreateReconciliationService();

        // Act
        await InvokeHandleJobEventAsync(reconciliationService, WatchEventType.Modified, completeJob);

        // Assert: WorkItem stays Dispatched (grace period not elapsed)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "WorkItem should not be failed during the 30s grace period after Job completion");
    }

    [Fact]
    public async Task HandleJobCompletion_CompleteJob_RunningWorkItem_PastGracePeriod_TransitionsToFailed()
    {
        // Scenario: Agent POSTed Running but then died — K8s Job completed, WorkItem stuck in Running.
        var workItemId = Guid.NewGuid();
        var k8sJobName = $"caa-{workItemId.ToString("N")[..8]}";
        await InsertWorkItem(workItemId, "owner/repo#stuck-running", "kiro,dotnet", WorkItemStatus.Running,
            k8sJobName: k8sJobName,
            assignedAgentId: "caa-agent-pod");

        var completeJob = CreateCompleteJob(k8sJobName, workItemId,
            completionTime: DateTime.UtcNow.AddSeconds(-45)); // Completed 45s ago (past 30s grace)

        var reconciliationService = CreateReconciliationService();

        // Act
        await InvokeHandleJobEventAsync(reconciliationService, WatchEventType.Modified, completeJob);

        // Assert
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
        item.ErrorMessage.Should().Contain("agent never reported terminal status");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Poll-loop safety net: DetectCompletedJobsWithStuckWorkItemsAsync
    // ═══════════════════════════════════════════════════════════════════════

    // TODO: Add a poll-loop test with WorkItemStatus.Running (not just Dispatched) to prevent regression if the || Running condition is accidentally removed from DetectCompletedJobsWithStuckWorkItemsAsync.

    [Fact]
    public async Task DetectCompletedJobsWithStuckWorkItems_PastGracePeriod_TransitionsToFailed()
    {
        // Scenario: Watch event was missed (API disconnect). Poll loop detects the stuck item.
        var workItemId = Guid.NewGuid();
        var k8sJobName = $"caa-{workItemId.ToString("N")[..8]}";
        await InsertWorkItem(workItemId, "owner/repo#poll-detect", "kiro,dotnet", WorkItemStatus.Dispatched,
            k8sJobName: k8sJobName);

        // Setup K8s mock: ListNamespacedJobAsync returns a Complete Job
        SetupListJobsReturning(new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = k8sJobName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["caa/work-item-id"] = workItemId.ToString()
                }
            },
            Status = new V1JobStatus
            {
                CompletionTime = DateTime.UtcNow.AddSeconds(-60), // Completed 60s ago
                Conditions =
                [
                    new V1JobCondition { Type = "Complete", Status = "True" }
                ]
            }
        });

        var reconciliationService = CreateReconciliationService();

        // Act
        await InvokeDetectCompletedJobsWithStuckWorkItemsAsync(reconciliationService);

        // Assert
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
        item.ErrorMessage.Should().Contain("agent never reported terminal status");
    }

    [Fact]
    public async Task DetectCompletedJobsWithStuckWorkItems_WithinGracePeriod_NoTransition()
    {
        // Scenario: Job just completed (<30s ago) — poll should not transition yet.
        var workItemId = Guid.NewGuid();
        var k8sJobName = $"caa-{workItemId.ToString("N")[..8]}";
        await InsertWorkItem(workItemId, "owner/repo#poll-grace", "kiro,dotnet", WorkItemStatus.Dispatched,
            k8sJobName: k8sJobName);

        SetupListJobsReturning(new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = k8sJobName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["caa/work-item-id"] = workItemId.ToString()
                }
            },
            Status = new V1JobStatus
            {
                CompletionTime = DateTime.UtcNow.AddSeconds(-10), // Completed only 10s ago
                Conditions =
                [
                    new V1JobCondition { Type = "Complete", Status = "True" }
                ]
            }
        });

        var reconciliationService = CreateReconciliationService();

        // Act
        await InvokeDetectCompletedJobsWithStuckWorkItemsAsync(reconciliationService);

        // Assert: WorkItem stays Dispatched
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    [Fact]
    public void CompleteJobGracePeriod_Is30Seconds()
    {
        // Verify the constant matches the documented 30-second grace period
        ReconciliationService.CompleteJobGracePeriodSeconds.Should().Be(30);
    }

    [Fact]
    public async Task DetectCompletedJobsWithStuckWorkItems_DuplicateJobNames_DoesNotCrash()
    {
        // Scenario: K8s API returns duplicate job names (pagination edge case).
        // Before the fix, this would throw ArgumentException from ToDictionary.
        var workItemId = Guid.NewGuid();
        var k8sJobName = $"caa-{workItemId.ToString("N")[..8]}";
        await InsertWorkItem(workItemId, "owner/repo#poll-dup", "kiro,dotnet", WorkItemStatus.Dispatched,
            k8sJobName: k8sJobName);

        var completionTime = DateTime.UtcNow.AddSeconds(-60); // Past grace period

        // Setup K8s mock: two jobs with the SAME name (duplicate)
        SetupListJobsReturning(
            new V1Job
            {
                Metadata = new V1ObjectMeta
                {
                    Name = k8sJobName,
                    Labels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                        ["caa/work-item-id"] = workItemId.ToString()
                    }
                },
                Status = new V1JobStatus
                {
                    CompletionTime = completionTime,
                    Conditions =
                    [
                        new V1JobCondition { Type = "Complete", Status = "True" }
                    ]
                }
            },
            new V1Job
            {
                Metadata = new V1ObjectMeta
                {
                    Name = k8sJobName, // Same name — duplicate
                    Labels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                        ["caa/work-item-id"] = workItemId.ToString()
                    }
                },
                Status = new V1JobStatus
                {
                    CompletionTime = completionTime,
                    Conditions =
                    [
                        new V1JobCondition { Type = "Complete", Status = "True" }
                    ]
                }
            });

        var reconciliationService = CreateReconciliationService();

        // Act — should NOT throw ArgumentException
        // TODO: Use `Func<Task> act = () => Invoke...; await act.Should().NotThrowAsync()` to explicitly
        // assert no-crash behavior. Currently, a revert would surface as ArgumentException propagating
        // through the test runner rather than a deliberate assertion failure.
        await InvokeDetectCompletedJobsWithStuckWorkItemsAsync(reconciliationService);

        // Assert: first occurrence is used, WorkItem transitions to Failed
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Non-kiro items dispatch even when PVC pool is exhausted
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PollAndDispatch_AllPvcsClaimed_NonKiroItemStillDispatches()
    {
        // Arrange: all PVCs claimed by kiro items
        await InsertWorkItem(Guid.NewGuid(), "owner/repo#kiro1", "kiro,dotnet", WorkItemStatus.Running,
            claimedPvc: "pvc-test-1");
        await InsertWorkItem(Guid.NewGuid(), "owner/repo#kiro2", "kiro,dotnet", WorkItemStatus.Running,
            claimedPvc: "pvc-test-2");

        // Insert a non-kiro Pending item (opencode agent — doesn't need PVC)
        var pendingId = Guid.NewGuid();
        await InsertWorkItem(pendingId, "owner/repo#opencode-item", "opencode,dotnet", WorkItemStatus.Pending);

        // Template for opencode agents (ProviderType != "kiro" → no PVC needed)
        var configData = new Dictionary<string, string?>
        {
            ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "10",
            ["WorkDistribution:Dispatch:RateLimitPerSecond"] = "100",
            ["WorkDistribution:Namespace"] = "default",
            ["WorkDistribution:OrchestratorUrl"] = "http://orchestrator:8080",
            ["WorkDistribution:AgentApiKeySecretName"] = "agent-api-key",
            ["WorkDistribution:AgentServiceAccountName"] = "caa-agent",
            ["WorkDistribution:CredentialPools:Kiro:0"] = "pvc-test-1",
            ["WorkDistribution:CredentialPools:Kiro:1"] = "pvc-test-2"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        // Two templates: kiro (needs PVC) + opencode (no PVC)
        var templates = new[]
        {
            new JobTemplate { Labels = "dotnet,kiro", Image = "ghcr.io/kiro:latest", ProviderType = "kiro", MaxConcurrent = 10 },
            new JobTemplate { Labels = "dotnet,opencode", Image = "ghcr.io/opencode:latest", ProviderType = "opencode", MaxConcurrent = 10 }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(templates);
        var templateProvider = JobTemplateProvider.LoadFromJson(json);

        var service = new DispatchService(
            _dbFactory, CreateAlwaysLeaderElection(), _mockKubeClient.Object,
            _transitionService, config, templateProvider);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await InvokePollAndDispatch(service);

        // Assert: opencode item dispatched (no PVC needed)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(pendingId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched,
            "Non-kiro items should dispatch regardless of PVC pool state");
        item.ClaimedPvcName.Should().BeNull("opencode agents don't use PVCs");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PVC claim leak on K8s API failure — claim reverted when Job creation fails
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PollAndDispatch_K8sApiFailsAfterPvcClaim_PvcReleased()
    {
        // Arrange: 1 PVC available
        var pendingId = Guid.NewGuid();
        await InsertWorkItem(pendingId, "owner/repo#will-fail", "kiro,dotnet", WorkItemStatus.Pending);

        // K8s API will throw on Job creation (simulating API server error)
        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new k8s.Autorest.HttpOperationException("K8s API error")
            {
                Response = new k8s.Autorest.HttpResponseMessageWrapper(
                    new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError), "")
            });

        var service = CreateDispatchService(pvcPool: new[] { "pvc-leak-test" });

        // Act
        await InvokePollAndDispatch(service);

        // Assert: WorkItem failed (K8s creation error)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(pendingId);
        item!.Status.Should().Be(WorkItemStatus.Failed);
        item.ErrorMessage.Should().Contain("K8s Job creation failed");

        // Assert: PVC claim is reverted in DB after K8s API failure
        item.ClaimedPvcName.Should().BeNull("PVC claim must be reverted on K8s API failure");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PVC claim leak on prepareVariant exception — PVC returned when delegate throws (#1609)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteDispatchLifecycle_PrepareVariantThrows_PvcReturnedToPool()
    {
        // Arrange: insert a Pending work item so ExecuteDispatchLifecycleAsync can load it
        var pendingId = Guid.NewGuid();
        await InsertWorkItem(pendingId, "owner/repo#prepare-fail", "kiro,dotnet", WorkItemStatus.Pending);

        var service = CreateDispatchService(pvcPool: new[] { "pvc-leak-test" });
        await using var db = await _dbFactory.CreateDbContextAsync();

        var projection = new DispatchService.PendingWorkItemProjection
        {
            Id = pendingId,
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600
        };

        var template = new JobTemplate
        {
            Labels = "dotnet,kiro",
            Image = "ghcr.io/agent:kiro-latest",
            ProviderType = "kiro",
            MaxConcurrent = 10
        };

        var availablePvcs = new List<string> { "pvc-leak-test" };
        var concurrency = new Dictionary<string, int>();

        // Act: prepareVariant throws — PVC must still be returned to pool
        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            db, projection, template,
            isKiroAgent: true,
            availablePvcs,
            concurrency,
            logPrefix: "",
            prepareVariant: _ => throw new InvalidOperationException("simulated prepareVariant failure"),
            onDispatchSuccess: null,
            ct: CancellationToken.None);

        // Assert: exception propagates
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated prepareVariant failure");

        // Assert: PVC was returned to pool (the critical fix)
        availablePvcs.Should().ContainSingle()
            .Which.Should().Be("pvc-leak-test");

        // Assert: work item stays Pending (no FailWorkItem called — exception propagates to caller)
        var workItem = await db.WorkItems.FindAsync(pendingId);
        workItem!.Status.Should().Be(WorkItemStatus.Pending);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Race condition: WorkItem no longer Pending after Job creation
    // PVC must be released and orphaned Job must be deleted (#1488)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PollAndDispatch_WorkItemTransitionedAfterJobCreation_PvcReleasedAndJobDeleted()
    {
        // Arrange: 1 PVC in pool, two Pending items (second item proves PVC was released)
        var racedId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await InsertWorkItem(racedId, "owner/repo#raced", "kiro,dotnet", WorkItemStatus.Pending,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        await InsertWorkItem(secondId, "owner/repo#second", "kiro,dotnet", WorkItemStatus.Pending,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var createCallCount = 0;
        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            // TODO: async lambda in Moq .Callback creates an async void delegate (fire-and-forget).
            // Works reliably only because the InMemory/SQLite provider completes synchronously.
            // If the test DB is ever changed to a truly async provider, replace with .Returns(async () => { ... })
            // or TaskCompletionSource-based synchronization to make ordering guarantees explicit.
            .Callback<V1Job, string, CancellationToken>(async (job, ns, ct) =>
            {
                createCallCount++;
                if (createCallCount == 1)
                {
                    // Simulate race: another process transitions the first work item to Dispatched
                    // during the K8s API call window. Use a separate DbContext to avoid tracking conflicts.
                    await using var raceDb = await _dbFactory.CreateDbContextAsync();
                    var item = await raceDb.WorkItems.FindAsync(racedId);
                    item!.Status = WorkItemStatus.Dispatched;
                    item.DispatchedAt = DateTimeOffset.UtcNow;
                    await raceDb.SaveChangesAsync();
                }
            })
            .Returns(Task.CompletedTask);

        _mockKubeClient
            .Setup(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateDispatchService(pvcPool: new[] { "pvc-race-test" });

        // Act
        await InvokePollAndDispatch(service);

        // Assert: DeleteJobAsync was called for the raced item's orphaned Job
        var expectedJobName = $"caa-{racedId.ToString("N")[..8]}";
        _mockKubeClient.Verify(
            k => k.DeleteJobAsync(expectedJobName, "default", It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: The second item was dispatched (proving PVC was released back to pool)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var secondItem = await db.WorkItems.FindAsync(secondId);
        secondItem!.Status.Should().Be(WorkItemStatus.Dispatched,
            "Second item should dispatch because PVC was released after race condition on first item");

        // Assert: First item's DB state was NOT mutated by our code (ClaimedPvcName stays set)
        var racedItem = await db.WorkItems.FindAsync(racedId);
        racedItem!.ClaimedPvcName.Should().NotBeNull(
            "Race condition path should not mutate the work item — ReconciliationService handles DB cleanup");
    }

    [Fact]
    public async Task PollAndDispatch_WorkItemNullAfterJobCreation_PvcReleasedAndJobDeleted()
    {
        // Arrange: WorkItem is hard-deleted between Job creation and re-fetch (defensive edge case)
        var deletedId = Guid.NewGuid();
        await InsertWorkItem(deletedId, "owner/repo#deleted", "kiro,dotnet", WorkItemStatus.Pending);

        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            // TODO: async lambda in Moq .Callback creates an async void delegate (fire-and-forget).
            // Works reliably only because the InMemory/SQLite provider completes synchronously.
            // If the test DB is ever changed to a truly async provider, replace with .Returns(async () => { ... })
            // or TaskCompletionSource-based synchronization to make ordering guarantees explicit.
            .Callback<V1Job, string, CancellationToken>(async (job, ns, ct) =>
            {
                // Simulate race: hard-delete the work item during K8s API call
                await using var raceDb = await _dbFactory.CreateDbContextAsync();
                var item = await raceDb.WorkItems.FindAsync(deletedId);
                if (item is not null)
                {
                    raceDb.WorkItems.Remove(item);
                    await raceDb.SaveChangesAsync();
                }
            })
            .Returns(Task.CompletedTask);

        _mockKubeClient
            .Setup(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateDispatchService(pvcPool: new[] { "pvc-null-test" });

        // Act
        await InvokePollAndDispatch(service);

        // Assert: DeleteJobAsync was called for the orphaned Job
        var expectedJobName = $"caa-{deletedId.ToString("N")[..8]}";
        _mockKubeClient.Verify(
            k => k.DeleteJobAsync(expectedJobName, "default", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PollAndDispatch_RaceCondition_DeleteJobFails_PvcStillReleased()
    {
        // Arrange: Race condition occurs AND DeleteJobAsync fails — PVC must still be released
        var racedId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await InsertWorkItem(racedId, "owner/repo#raced-fail", "kiro,dotnet", WorkItemStatus.Pending,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        await InsertWorkItem(secondId, "owner/repo#second-fail", "kiro,dotnet", WorkItemStatus.Pending,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var createCallCount = 0;
        _mockKubeClient
            .Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            // TODO: async lambda in Moq .Callback creates an async void delegate (fire-and-forget).
            // Works reliably only because the InMemory/SQLite provider completes synchronously.
            // If the test DB is ever changed to a truly async provider, replace with .Returns(async () => { ... })
            // or TaskCompletionSource-based synchronization to make ordering guarantees explicit.
            .Callback<V1Job, string, CancellationToken>(async (job, ns, ct) =>
            {
                createCallCount++;
                if (createCallCount == 1)
                {
                    // Simulate race on first item
                    await using var raceDb = await _dbFactory.CreateDbContextAsync();
                    var item = await raceDb.WorkItems.FindAsync(racedId);
                    item!.Status = WorkItemStatus.Dispatched;
                    item.DispatchedAt = DateTimeOffset.UtcNow;
                    await raceDb.SaveChangesAsync();
                }
            })
            .Returns(Task.CompletedTask);

        // DeleteJobAsync throws (simulating K8s API failure or 404)
        _mockKubeClient
            .Setup(k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new k8s.Autorest.HttpOperationException("Not Found")
            {
                Response = new k8s.Autorest.HttpResponseMessageWrapper(
                    new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound), "")
            });

        var service = CreateDispatchService(pvcPool: new[] { "pvc-fail-test" });

        // Act
        await InvokePollAndDispatch(service);

        // Assert: Second item still dispatched (PVC released despite DeleteJob failure)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var secondItem = await db.WorkItems.FindAsync(secondId);
        secondItem!.Status.Should().Be(WorkItemStatus.Dispatched,
            "PVC must be released even when DeleteJobAsync fails — compensating action is best-effort");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private ReconciliationService CreateReconciliationService()
    {
        var configData = new Dictionary<string, string?>
        {
            ["WorkDistribution:Reconciliation:PollIntervalSeconds"] = "30",
            ["WorkDistribution:Reconciliation:RetentionDays"] = "7",
            ["WorkDistribution:Namespace"] = "default"
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        return new ReconciliationService(
            _dbFactory, CreateAlwaysLeaderElection(), _mockKube.Object,
            _transitionService, config);
    }

    private static V1Job CreateCompleteJob(string jobName, Guid workItemId, DateTime completionTime)
    {
        return new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
                Labels = new Dictionary<string, string>
                {
                    ["caa/work-item-id"] = workItemId.ToString()
                },
                ResourceVersion = "456"
            },
            Status = new V1JobStatus
            {
                Succeeded = 1,
                CompletionTime = completionTime,
                Conditions =
                [
                    new V1JobCondition
                    {
                        Type = "Complete",
                        Status = "True"
                    }
                ]
            }
        };
    }

    private async Task InvokeHandleJobEventAsync(ReconciliationService service, WatchEventType type, V1Job job)
    {
        var method = typeof(ReconciliationService).GetMethod("HandleJobEventAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [type, job, CancellationToken.None])!;
        await task;
    }

    // TODO: DetectCompletedJobsWithStuckWorkItemsAsync is internal (InternalsVisibleTo is configured) — call it directly instead of via reflection for compile-time safety and rename resilience.
    private async Task InvokeDetectCompletedJobsWithStuckWorkItemsAsync(ReconciliationService service)
    {
        var method = typeof(ReconciliationService).GetMethod("DetectCompletedJobsWithStuckWorkItemsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [CancellationToken.None])!;
        await task;
    }

    private void SetupListJobsReturning(params V1Job[] jobs)
    {
        _mockBatchV1
            .Setup(b => b.ListNamespacedJobWithHttpMessagesAsync(
                It.IsAny<string>(),       // namespaceParameter
                It.IsAny<bool?>(),        // allowWatchBookmarks
                It.IsAny<string>(),       // continueParameter
                It.IsAny<string>(),       // fieldSelector
                It.IsAny<string>(),       // labelSelector
                It.IsAny<int?>(),         // limit
                It.IsAny<string>(),       // resourceVersion
                It.IsAny<string>(),       // resourceVersionMatch
                It.IsAny<bool?>(),        // sendInitialEvents
                It.IsAny<int?>(),         // timeoutSeconds
                It.IsAny<bool?>(),        // watch
                It.IsAny<bool?>(),        // pretty
                It.IsAny<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), // customHeaders
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new k8s.Autorest.HttpOperationResponse<V1JobList>
            {
                Body = new V1JobList { Items = jobs.ToList() }
            });
    }

    private DispatchService CreateDispatchService(string[]? pvcPool = null)
    {
        pvcPool ??= new[] { "pvc-test-1", "pvc-test-2" };

        var configData = new Dictionary<string, string?>
        {
            ["WorkDistribution:Dispatch:PollIntervalSeconds"] = "10",
            ["WorkDistribution:Dispatch:RateLimitPerSecond"] = "100",
            ["WorkDistribution:Namespace"] = "default",
            ["WorkDistribution:OrchestratorUrl"] = "http://orchestrator:8080",
            ["WorkDistribution:AgentApiKeySecretName"] = "agent-api-key",
            ["WorkDistribution:AgentServiceAccountName"] = "caa-agent"
        };
        for (var i = 0; i < pvcPool.Length; i++)
            configData[$"WorkDistribution:CredentialPools:Kiro:{i}"] = pvcPool[i];

        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var templateProvider = BuildTemplateProvider(
            new Dictionary<string, string> { ["dotnet,kiro"] = "ghcr.io/agent:kiro-latest" });

        return new DispatchService(
            _dbFactory, CreateAlwaysLeaderElection(), _mockKubeClient.Object,
            _transitionService, config, templateProvider);
    }

    private static JobTemplateProvider BuildTemplateProvider(Dictionary<string, string> imageMapping)
    {
        var templates = imageMapping.Select(kv => new JobTemplate
        {
            Labels = kv.Key,
            Image = kv.Value,
            ProviderType = "kiro",
            MaxConcurrent = 10
        }).ToList();

        var json = System.Text.Json.JsonSerializer.Serialize(templates);
        return JobTemplateProvider.LoadFromJson(json);
    }

    private async Task InvokePollAndDispatch(DispatchService service)
    {
        var method = typeof(DispatchService).GetMethod("PollAndDispatchAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task)method!.Invoke(service, [CancellationToken.None])!;
        await task;
    }

    private static LeaderElectionService CreateAlwaysLeaderElection()
    {
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        var isLeaderField = typeof(LeaderElectionService).GetField("_isLeader",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        isLeaderField?.SetValue(les, true);
        var leaderCtsField = typeof(LeaderElectionService).GetField("_leaderCts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        leaderCtsField?.SetValue(les, new CancellationTokenSource());
        return les;
    }

    private async Task InsertWorkItem(Guid id, string issueId, string selector, WorkItemStatus status,
        DateTimeOffset? createdAt = null, int timeoutSeconds = 3600,
        string? k8sJobName = null, string? claimedPvc = null, string? assignedAgentId = null,
        DateTimeOffset? dispatchedAt = null, DateTimeOffset? completedAt = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var effectiveCreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = issueId,
            IssueProviderConfigId = "provider-1",
            Status = status,
            AgentSelector = selector,
            CreatedAt = effectiveCreatedAt,
            TimeoutSeconds = timeoutSeconds,
            Payload = "{}",
            K8sJobName = k8sJobName,
            ClaimedPvcName = claimedPvc,
            AssignedAgentId = assignedAgentId,
            DispatchedAt = dispatchedAt ?? (status >= WorkItemStatus.Dispatched ? effectiveCreatedAt : null),
            CompletedAt = completedAt
        });
        await db.SaveChangesAsync();
    }

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never; }
            }
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
