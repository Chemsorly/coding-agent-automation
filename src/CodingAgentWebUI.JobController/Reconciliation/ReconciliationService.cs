using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Serilog;

namespace CodingAgentWebUI.JobController.Reconciliation;

/// <summary>
/// Leader-elected BackgroundService that reconciles K8s Job state with WorkItem state.
/// Runs under the same dispatch leader lease. Runs three periodic loops concurrently:
/// - Reconciliation (completed/failed jobs → post status)
/// - Timeout enforcement (session max duration)
/// - Dispatched timeout sweep (short-circuit orphaned claims)
/// - Orphan cleanup (stale Jobs with no active WorkItem)
/// </summary>
public sealed class ReconciliationService : LeaderElectedPollingService
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<ReconciliationService>();

    private readonly ReconciliationLoop _loop;
    private readonly DispatchServiceOptions _options;

    protected override string ServiceName => "ReconciliationService";
    protected override int PollIntervalSeconds => 30; // fixed; reconciliation doesn't need to match dispatch cadence

    public ReconciliationService(
        ILeaderElectionService leaderElection,
        ReconciliationLoop loop,
        DispatchServiceOptions options)
        : base(leaderElection) // no rate limiter — reconciliation doesn't need throttling
    {
        ArgumentNullException.ThrowIfNull(leaderElection);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(options);
        _loop = loop;
        _options = options;
    }

    protected override async Task OnPollCycleAsync(CancellationToken ct)
    {
        Log.Debug("ReconciliationService: starting reconciliation cycle");

        // Run all reconciliation tasks concurrently within the same poll cycle
        await Task.WhenAll(
            RunSafe(_loop.ReconcileOnceAsync(ct), "ReconcileOnce", ct),
            RunSafe(_loop.EnforceTimeoutsAsync(ct), "EnforceTimeouts", ct),
            RunSafe(_loop.EnforceDispatchedTimeoutAsync(ct), "EnforceDispatchedTimeout", ct),
            RunSafe(_loop.CleanupOrphansAsync(ct), "CleanupOrphans", ct));
    }

    private static async Task RunSafe(Task task, string name, CancellationToken ct)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on leadership loss
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ReconciliationService: unhandled error in {Task}", name);
        }
    }
}
