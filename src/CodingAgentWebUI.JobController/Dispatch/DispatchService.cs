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

    protected override string ServiceName => "DispatchService";
    protected override int PollIntervalSeconds => _options.PollIntervalSeconds;

    public DispatchService(
        ILeaderElectionService leaderElection,
        DispatchLoop loop,
        DispatchServiceOptions options)
        : base(leaderElection, options.RateLimitPerSecond)
    {
        _loop = loop;
        _options = options;
    }

    protected override async Task RunLeadershipTermAsync(CancellationToken ct)
    {
        Log.Debug("DispatchService config: AgentJobTimeoutSeconds={Timeout}s, ChatPodConnectTimeoutSeconds={ConnectTimeout}s, " +
                  "ChatTerminationGracePeriodSeconds={TerminationGrace}s, PollIntervalSeconds={PollInterval}s, " +
                  "KiroPvcPool=[{PvcPool}]",
            _options.AgentJobTimeoutSeconds,
            _options.ChatPodConnectTimeoutSeconds,
            _options.ChatTerminationGracePeriodSeconds,
            _options.PollIntervalSeconds,
            string.Join(", ", _options.KiroPvcPool));
        await base.RunLeadershipTermAsync(ct);
    }

    protected override async Task OnPollCycleAsync(CancellationToken ct)
    {
        Log.Debug("DispatchService: starting poll cycle");
        WorkDistributionTelemetry.RecordLastPollEpoch();
        // NOTE: claimed count is hardcoded to 0. The old PvcPool.AvailableCount-based calculation
        // was removed with the PvcPool and has not been replaced. Dashboards and alerts consuming the
        // credential pool claimed gauge will permanently show zero, masking PVC starvation events.
        // To fix: derive the claimed count from a live ListJobsAsync query (counting distinct PVC
        // volume claims in active Jobs) or remove the metric until it can be computed accurately.
        WorkDistributionTelemetry.UpdateCredentialPoolMetrics(_options.KiroPvcPool.Count, 0);
        await _loop.RunOneCycleAsync(ct);
    }
}
