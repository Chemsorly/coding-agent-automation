// DEAD CODE (Spec 043) — this class is no longer registered as a hosted service in any host.
// Source retained because test projects in CodingAgentWebUI.Pipeline.UnitTests directly
// instantiate it via 'new ReconciliationService(...)'. Spec 045 task: migrate those tests and delete.
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// K8s mode only: watches K8s Jobs for completions/failures (label selector),
/// performs periodic safety-net poll for orphan detection, timeout enforcement,
/// stale work item cleanup, PVC release, and PipelineRuns retention.
/// Runs under leader election (same Lease as DispatchService).
/// </summary>
public sealed partial class ReconciliationService : LeaderElectedPollingService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ReconciliationService>();

    private const string ManagedByLabel = "app.kubernetes.io/managed-by";
    private const string ManagedByValue = "caa-orchestrator";
    private const string WorkItemIdLabel = "caa/work-item-id";

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly IKubernetes _kubeClient;
    private readonly WorkItemTransitionService _transitionService;
    private readonly ILabelService? _labelService;
    private readonly IRunLifecycleManager? _lifecycleManager;
    private readonly IConsolidationService? _consolidationService;
    private readonly IConfigurationStore? _configStore;
    private readonly IJobDeduplicationGuard? _dedupGuard;
    private readonly ReconciliationServiceOptions _options;

    /// <summary>
    /// Tracks the K8s resourceVersion from Watch events. Declared here (Core) so all partial files
    /// share the same instance field. Mutated exclusively in Watch partial (RunWatchLoopAsync,
    /// WatchJobsAsync, RelistJobsAsync).
    /// </summary>
    private string? _lastResourceVersion;

    protected override string ServiceName => "ReconciliationService";
    protected override int PollIntervalSeconds => _options.PollIntervalSeconds;

    public ReconciliationService(
        ReconciliationServiceDependencies deps)
        : base(deps.LeaderElection)
    {
        _dbFactory = deps.DbFactory;
        _kubeClient = deps.KubeClient;
        _transitionService = deps.TransitionService;
        _labelService = deps.LabelService;
        _lifecycleManager = deps.LifecycleManager;
        _consolidationService = deps.ConsolidationService;
        _configStore = deps.ConfigStore;
        _dedupGuard = deps.DedupGuard;
        _options = new ReconciliationServiceOptions();
        deps.Configuration.GetSection("WorkDistribution:Reconciliation").Bind(_options);

        _options.Namespace = deps.Configuration.GetValue<string>("WorkDistribution:Namespace")
            ?? Environment.GetEnvironmentVariable("POD_NAMESPACE")
            ?? "default";
    }

    /// <summary>
    /// Overrides the default poll loop to run startup reconciliation followed by
    /// concurrent Watch + Poll loops. The linked CancellationToken (from the base class)
    /// fires on leadership loss or host stop.
    /// </summary>
    protected override async Task RunLeadershipTermAsync(CancellationToken ct)
    {
        Log.Information("ReconciliationService: leader acquired, running startup reconciliation");

        // Reset watch state for new leadership term (avoids 410 Gone with stale resourceVersion)
        _lastResourceVersion = null;

        await RunStartupReconciliationAsync(ct);

        // Run Watch and Poll concurrently
        var watchTask = RunWatchLoopAsync(ct);
        var pollTask = RunPollLoopAsync(ct);

        // Exit when leadership lost or stopping
        await Task.WhenAny(watchTask, pollTask);

        // TODO: Stale comment — no local CTS is created here. The original code had explicit
        // cancellation (`await linked.CancelAsync()`) between WhenAny and WhenAll to stop the
        // surviving loop when the other exited. That path was removed in the refactoring.
        // Both loops only exit via ct cancellation (leadership loss or host stop), so this is
        // safe in practice, but if loop logic changes in the future, consider re-adding explicit
        // cancellation to stop the surviving loop immediately rather than waiting for its delay.
        // Note: The base class owns the linked CTS, so cancellation propagates from LeaderToken.
        // If one loop faults, we need to propagate exception after cleanup.
        try { await Task.WhenAll(watchTask, pollTask); }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            // Catch non-OCE exceptions from WhenAll to prevent BackgroundService termination.
            // Log and let the base class re-enter the leader wait loop.
            Log.Error(ex, "ReconciliationService: watch/poll loop faulted unexpectedly — will re-enter leader wait loop");
        }
    }

    /// <summary>
    /// Not used directly — ReconciliationService overrides <see cref="RunLeadershipTermAsync"/>
    /// instead of using the default poll loop. This is never called by the base class when
    /// <see cref="RunLeadershipTermAsync"/> is overridden.
    /// </summary>
    protected override Task OnPollCycleAsync(CancellationToken ct) => RunReconciliationCycleAsync(ct);

    // ── Safety-Net Poll Loop ─────────────────────────────────────────────

    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && LeaderElection.IsLeader)
        {
            try
            {
                await RunReconciliationCycleAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ReconciliationService: error in reconciliation cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunReconciliationCycleAsync(CancellationToken ct)
    {
        await DetectOrphansAsync(ct);
        await DetectCompletedJobsWithStuckWorkItemsAsync(ct);
        await EnforceTimeoutsAsync(ct);
        await EnforceConsolidationTimeoutsAsync(ct);
        await DetectPodStartupFailuresAsync(ct);
        await CleanupStaleWorkItemsAsync(ct);
        await CleanupStalePipelineRunsAsync(ct);
        await CleanupStaleConsolidationRunsAsync(ct);
        await ReconcilePvcsFromPollAsync(ct);
    }
}
