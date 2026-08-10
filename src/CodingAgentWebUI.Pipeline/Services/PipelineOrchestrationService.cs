using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog.Context;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Singleton service that coordinates the automated development pipeline.
/// Manages provider resolution, execution orchestration, label swaps, and PR creation.
/// Delegates run state, lifecycle transitions, events, and cancellation to <see cref="PipelineRunLifecycleService"/>.
/// Pipeline execution is handled by remote agents via <see cref="DispatchRunCreationService"/> and
/// <c>LocalPipelineExecutor</c>. Multi-agent dispatch uses concurrent runs tracked via <see cref="IOrchestratorRunService"/>.
/// </summary>
// Provider lifecycle management (resolution, disposal, active provider tracking) is delegated
// to PipelineProviderManager, extracted per spec 017 / MAINT-09.
//
// IProviderOperationsFacade evaluation: IPipelineCallbacks already covers SwapAgentLabel,
// RemoveAllAgentLabels, and CreatePullRequest. A separate facade would add indirection
// without meaningful simplification. Revisit if pipeline steps accumulate more
// provider-operation parameters beyond what IPipelineCallbacks covers.
public class PipelineOrchestrationService : IDisposable, IAsyncDisposable, IOrchestrationShutdownAction
{
    private readonly PipelineRunLifecycleService _lifecycle;
    private readonly ILabelService _labelSwapper;
    private readonly IPipelineCancellationFacade _cancellationFacade;
    private readonly Serilog.ILogger _logger;

    protected readonly PipelineProviderManager _providerManager;

    public PipelineOrchestrationService(
        IConfigurationStore configurationStore,
        IProviderFactory providerFactory,
        IPipelineCancellationFacade cancellationFacade,
        PipelineRunLifecycleService lifecycle,
        ILabelService labelSwapper,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configurationStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(cancellationFacade);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(labelSwapper);
        ArgumentNullException.ThrowIfNull(logger);

        _labelSwapper = labelSwapper;
        _logger = logger;
        _cancellationFacade = cancellationFacade;
        _providerManager = new PipelineProviderManager(configurationStore, providerFactory, logger);
        _lifecycle = lifecycle;
    }

    /// <summary>Cancels the active pipeline run. Delegates state transitions to lifecycle service.</summary>
    public async Task CancelPipelineAsync()
    {
        if (_lifecycle.ActiveRun == null || !_lifecycle.IsRunning) return;
        var run = _lifecycle.ActiveRun;
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);

        // Label swap requires the active issue provider (orchestration concern)
        if (_providerManager.ActiveIssueProvider != null || run.RunType == PipelineRunType.Review)
        {
            _logger.Information(
                "Pipeline {RunId} CancelPipelineAsync: {IssueIdentifier} → {Label} (runType={RunType}, step={CurrentStep})",
                run.RunId, run.IssueIdentifier, AgentLabels.Cancelled, run.RunType, run.CurrentStep);
            // TODO: Behavioral change — original SwapAgentLabelAsync caught ALL exceptions including
            // OperationCanceledException. TrySwapLabelAsync lets OCE propagate. Unlikely with CancellationToken.None
            // but possible if internal HttpClient times out.
            await _labelSwapper.TrySwapLabelAsync(run, AgentLabels.Cancelled, _logger, "PipelineOrchestrationService.CancelPipelineAsync", CancellationToken.None);
        }

        // Delegate state transitions to lifecycle
        await _lifecycle.CancelPipelineAsync();
    }

    /// <summary>
    /// Releases all agent-dispatched active runs from in-memory tracking during graceful shutdown.
    /// Does NOT send CancelJob to agents and does NOT write Cancelled history entries.
    /// During a rolling update the new pod has already rehydrated these runs; agents will
    /// reconnect to the new pod and complete normally. The dedup guard is released so the
    /// new pod is not blocked on re-adopting the issues.
    /// </summary>
    // synchronous — no async work after rolling-update handoff fix
    public Task ReleaseActiveAgentRunsAsync()
    {
        // Release all runs from in-memory tracking — includes sentinels (AgentId == null)
        // so their dedup guards are always freed, even if agents haven't called JobAccepted yet.
        var releasedIssues = _lifecycle.ReleaseAgentRunsForHandoff();

        // Release dedup guards so the new pod can adopt / re-dispatch the issues
        if (_cancellationFacade.DedupGuard is not null)
        {
            foreach (var (issueId, providerId) in releasedIssues)
            {
                _cancellationFacade.DedupGuard.MarkIssueComplete(issueId, providerId);
            }
        }

        return Task.CompletedTask;
    }

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            // Sync-only managed resources: none currently (provider manager is async-disposed).
            // DisposeAsync() handles _providerManager disposal.
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        // Do not call DisposeAsync synchronously — .GetAwaiter().GetResult()
        // deadlocks in Blazor Server's SynchronizationContext (review finding #13).
        // DisposeAsync() is the correct disposal path for async resources.
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _providerManager.DisposeAsync();
        GC.SuppressFinalize(this);
    }

}
