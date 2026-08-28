using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.JobController.Reconciliation;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodingAgentWebUI.JobController.UnitTests.Reconciliation;

/// <summary>
/// Unit tests for <see cref="ReconciliationService"/> — the leader-elected wrapper around
/// <see cref="ReconciliationLoop"/>. Validates that the leader gate is respected:
/// none of the four inner reconciliation tasks must be called when this instance
/// is not the leader.
///
/// The leader-wait / linked-CTS / re-entry pattern is already tested exhaustively
/// in <c>LeaderElectedPollingServiceTests</c>; these tests focus on the integration
/// between the service shell and the inner loop.
/// </summary>
public sealed class ReconciliationServiceTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _workItemClient = new();
    private readonly Mock<IKubernetesJobClient>        _k8sClient      = new();
    private readonly DispatchServiceOptions            _options;

    public ReconciliationServiceTests()
    {
        _options = new DispatchServiceOptions
        {
            Namespace                     = "test-ns",
            AgentJobTimeoutSeconds = 7200,
            ChatPodConnectTimeoutSeconds  = 120
        };

        // Default: no active jobs, no active work items
        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });
        _workItemClient
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static LeaderElectionService MakeLeaderElection(bool isLeader, CancellationTokenSource? leaderCts = null)
    {
        var les = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        SetLeaderState(les, isLeader, leaderCts ?? new CancellationTokenSource());
        return les;
    }

    private static void SetLeaderState(LeaderElectionService les, bool isLeader, CancellationTokenSource cts)
    {
        typeof(LeaderElectionService)
            .GetField("_isLeader", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(les, isLeader);
        typeof(LeaderElectionService)
            .GetField("_leaderCts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(les, cts);
    }

    private ReconciliationService MakeService(ILeaderElectionService leaderElection)
    {
        var loop = new ReconciliationLoop(_workItemClient.Object, _k8sClient.Object, new PvcPool([]), _options);
        return new ReconciliationService(leaderElection, loop);
    }

    private static async Task RunExecuteForDuration(BackgroundService svc, CancellationToken stopToken)
    {
        var method = typeof(BackgroundService)
            .GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(svc, [stopToken])!;
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on stop */ }
    }

    // ── non-leader: inner loop never called ───────────────────────────────────

    [Fact]
    public async Task WhenNotLeader_ReconciliationLoop_IsNeverCalled()
    {
        // Arrange: never becomes leader during the test
        var leaderElection = MakeLeaderElection(isLeader: false);
        var svc = MakeService(leaderElection);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // Act: run ExecuteAsync; it spends the entire time in the 2s leader-wait loop
        await RunExecuteForDuration(svc, cts.Token);

        // Assert: no K8s or API call was made — none of the four inner tasks were entered
        _k8sClient.Verify(
            c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "ReconcileOnceAsync must not be called when this instance is not the leader");
        _workItemClient.Verify(
            c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "EnforceTimeoutsAsync / EnforceDispatchedTimeoutAsync must not be called when not the leader");
    }

    // ── leader: inner loop is called ──────────────────────────────────────────

    [Fact]
    public async Task WhenLeader_ReconciliationLoop_IsCalled()
    {
        // Arrange: starts as leader
        var leaderCts      = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: true, leaderCts);
        var svc = MakeService(leaderElection);

        using var stopCts = new CancellationTokenSource();

        // Act: run until at least one reconciliation cycle fires, then stop
        var executeTask = RunExecuteForDuration(svc, stopCts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var calls = _k8sClient.Invocations.Count(i => i.Method.Name == nameof(IKubernetesJobClient.ListJobsAsync));
            if (calls > 0) break;
            await Task.Delay(50);
        }

        stopCts.Cancel();
        await executeTask;

        // Assert: inner loop was entered at least once
        _k8sClient.Verify(
            c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "ReconcileOnceAsync must be called when this instance is the leader");
    }

    // ── leadership acquired mid-run ────────────────────────────────────────────

    [Fact]
    public async Task WhenLeadershipAcquiredAfterWaiting_ReconciliationLoop_IsCalled()
    {
        // Arrange: start as non-leader
        var leaderCts      = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: false, leaderCts);
        var svc = MakeService(leaderElection);

        using var stopCts = new CancellationTokenSource();
        var executeTask   = RunExecuteForDuration(svc, stopCts.Token);

        // Confirm no calls while waiting for leadership
        await Task.Delay(150);
        _k8sClient.Verify(
            c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Act: grant leadership
        SetLeaderState(leaderElection, isLeader: true, leaderCts);

        // Wait for at least one reconciliation cycle
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var calls = _k8sClient.Invocations.Count(i => i.Method.Name == nameof(IKubernetesJobClient.ListJobsAsync));
            if (calls > 0) break;
            await Task.Delay(50);
        }

        stopCts.Cancel();
        await executeTask;

        _k8sClient.Verify(
            c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
}
