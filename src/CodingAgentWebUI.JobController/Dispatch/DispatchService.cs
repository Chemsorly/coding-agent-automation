using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Telemetry;
using Serilog;

namespace CodingAgentWebUI.JobController.Dispatch;

/// <summary>
/// Leader-elected BackgroundService that runs the dispatch poll cycle.
/// Polls the Pipeline API for pending WorkItems and creates K8s Jobs for them.
/// Only the pod that holds the dispatch leader lease runs the loop.
/// </summary>
public sealed class DispatchService : LeaderElectedPollingService
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<DispatchService>();

    private readonly DispatchLoop _loop;
    private readonly DispatchServiceOptions _options;
    private readonly PvcPool _pvcPool;
    private readonly IKubernetesJobClient _k8sClient;

    protected override string ServiceName => "DispatchService";
    protected override int PollIntervalSeconds => _options.PollIntervalSeconds;

    public DispatchService(
        ILeaderElectionService leaderElection,
        DispatchLoop loop,
        DispatchServiceOptions options,
        PvcPool pvcPool,
        IKubernetesJobClient k8sClient)
        : base(leaderElection, options.RateLimitPerSecond)
    {
        _loop = loop;
        _options = options;
        _pvcPool = pvcPool;
        _k8sClient = k8sClient;
    }

    /// <summary>
    /// Rebuilds the PVC pool's claimed-set from live K8s Jobs each time leadership is acquired.
    /// This prevents re-claiming PVCs that were already assigned before a controller restart.
    /// </summary>
    protected override async Task RunLeadershipTermAsync(CancellationToken ct)
    {
        Log.Information("DispatchService: rebuilding PVC pool from live K8s Jobs");
        await _pvcPool.RebuildFromLiveJobsAsync(_k8sClient, _options, ct);
        await base.RunLeadershipTermAsync(ct);
    }

    protected override async Task OnPollCycleAsync(CancellationToken ct)
    {
        Log.Debug("DispatchService: starting poll cycle");
        WorkDistributionTelemetry.RecordLastPollEpoch();
        WorkDistributionTelemetry.UpdateCredentialPoolMetrics(_pvcPool.AvailableCount, _pvcPool.TotalCount - _pvcPool.AvailableCount);
        await _loop.RunOneCycleAsync(ct);
    }
}
