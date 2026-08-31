using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.JobController.Dispatch;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="DispatchService"/> — the leader-elected wrapper around
/// <see cref="DispatchLoop"/>. Validates that the leader gate is respected:
/// <see cref="DispatchLoop.RunOneCycleAsync"/> must not be called when this instance
/// is not the leader.
///
/// The leader-wait / linked-CTS / re-entry pattern is already tested exhaustively
/// in <c>LeaderElectedPollingServiceTests</c>; these tests focus on the integration
/// between the service shell and the inner loop.
/// </summary>
public sealed class DispatchServiceTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _workItemClient = new();
    private readonly Mock<IPipelineApiConfigClient> _configClient = new();
    private readonly Mock<IKubernetesJobClient> _k8sClient = new();
    private readonly JobTemplateStore _templateStore;
    private readonly DispatchServiceOptions _options;

    public DispatchServiceTests()
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
            """;
        _templateStore = JobTemplateStore.LoadFromYaml(yaml);

        _k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });
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

    private DispatchService MakeService(ILeaderElectionService leaderElection)
    {
        var loop = new DispatchLoop(
            _workItemClient.Object, _configClient.Object, _k8sClient.Object,
            _templateStore, _options, new PvcSelectLock());

        return new DispatchService(leaderElection, loop, _options);
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
    public async Task WhenNotLeader_DispatchLoop_IsNeverCalled()
    {
        // Arrange: never becomes leader during the test
        var leaderElection = MakeLeaderElection(isLeader: false);
        var svc = MakeService(leaderElection);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // Act: run ExecuteAsync; it spends the entire time in the 2s leader-wait loop
        await RunExecuteForDuration(svc, cts.Token);

        // Assert: no API call was made — the inner loop was never entered
        _workItemClient.Verify(
            c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "DispatchLoop.RunOneCycleAsync must not be called when this instance is not the leader");
    }

    // ── leader: inner loop is called ──────────────────────────────────────────

    [Fact]
    public async Task WhenLeader_DispatchLoop_IsCalled()
    {
        // Arrange: starts as leader, nothing pending
        var leaderCts = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: true, leaderCts);
        var svc = MakeService(leaderElection);

        _workItemClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        using var stopCts = new CancellationTokenSource();

        // Act: run until at least one poll cycle fires, then stop
        var executeTask = RunExecuteForDuration(svc, stopCts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var calls = _workItemClient.Invocations.Count(i => i.Method.Name == nameof(IPipelineApiWorkItemClient.GetPendingAsync));
            if (calls > 0) break;
            await Task.Delay(50);
        }

        stopCts.Cancel();
        await executeTask;

        // Assert: inner loop was entered at least once
        _workItemClient.Verify(
            c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "DispatchLoop.RunOneCycleAsync must be called when this instance is the leader");
    }

    // ── leadership acquired mid-run ────────────────────────────────────────────

    [Fact]
    public async Task WhenLeadershipAcquiredAfterWaiting_DispatchLoop_IsCalled()
    {
        // Arrange: start as non-leader
        var leaderCts = new CancellationTokenSource();
        var leaderElection = MakeLeaderElection(isLeader: false, leaderCts);
        var svc = MakeService(leaderElection);

        _workItemClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        using var stopCts = new CancellationTokenSource();
        var executeTask = RunExecuteForDuration(svc, stopCts.Token);

        // Confirm no polling while waiting for leadership
        await Task.Delay(150);
        _workItemClient.Verify(
            c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Act: grant leadership
        SetLeaderState(leaderElection, isLeader: true, leaderCts);

        // Wait for at least one poll cycle
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var calls = _workItemClient.Invocations.Count(i => i.Method.Name == nameof(IPipelineApiWorkItemClient.GetPendingAsync));
            if (calls > 0) break;
            await Task.Delay(50);
        }

        stopCts.Cancel();
        await executeTask;

        _workItemClient.Verify(
            c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
}
