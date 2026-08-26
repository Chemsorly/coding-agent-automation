using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.LeaderElection;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.JobController.Dispatch;

/// <summary>
/// Leader-elected BackgroundService that runs the consolidation dispatch poll cycle.
/// Polls the Pipeline API for pending consolidation WorkItems and creates K8s Jobs.
/// Shares the same <see cref="ILeaderElectionService"/> lease as <see cref="DispatchService"/>
/// so only the leader replica dispatches both regular and consolidation work items.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "Thin BackgroundService shell — no logic beyond delegating to ConsolidationDispatchLoop. Covered by ConsolidationDispatchLoopTests.")]
public sealed class ConsolidationDispatchService : LeaderElectedPollingService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ConsolidationDispatchService>();

    private readonly ConsolidationDispatchLoop _loop;
    private readonly DispatchServiceOptions _options;

    protected override string ServiceName => "ConsolidationDispatchService";
    protected override int PollIntervalSeconds => _options.PollIntervalSeconds;

    public ConsolidationDispatchService(
        ILeaderElectionService leaderElection,
        ConsolidationDispatchLoop loop,
        DispatchServiceOptions options)
        : base(leaderElection, options.RateLimitPerSecond)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(options);
        _loop = loop;
        _options = options;
    }

    protected override async Task OnPollCycleAsync(CancellationToken ct)
    {
        Log.Debug("ConsolidationDispatchService: starting poll cycle");
        await _loop.RunOneCycleAsync(ct);
    }
}
