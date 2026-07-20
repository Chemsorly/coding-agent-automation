using CodingAgentWebUI.Pipeline;
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
public class PipelineOrchestrationService : IDisposable, IAsyncDisposable, IOrchestrationShutdownAction, IChangeNotifier
{
    private readonly PipelineRunLifecycleService _lifecycle;
    private readonly IPipelineConfigStore _pipelineConfigStore;
    private readonly ILabelService _labelSwapper;
    private readonly IssueDescriptionParser _issueParser;
    private readonly IPipelineExecutionFacade _executionFacade;
    private readonly IPipelineCompletionFacade _completionFacade;
    private readonly IPipelineCancellationFacade _cancellationFacade;
    private readonly Serilog.ILogger _logger;

    protected readonly PipelineProviderManager _providerManager;
    protected PipelineConfiguration? _activeConfig;

    protected IssueDetail? _activeIssue;
    protected ParsedIssue? _activeParsedIssue;
    protected IReadOnlyList<IssueComment>? _activeIssueComments;

    // ── Delegating properties (backward compatibility) ───────────────────

    /// <summary>Fired after each state transition for UI binding. Delegates to lifecycle service.</summary>
    public event Action? OnChange
    {
        add => _lifecycle.OnChange += value;
        remove => _lifecycle.OnChange -= value;
    }

    /// <summary>Fired for each agent output line for real-time display. Delegates to lifecycle service.</summary>
    public event Action<string>? OnOutputLine
    {
        add => _lifecycle.OnOutputLine += value;
        remove => _lifecycle.OnOutputLine -= value;
    }

    /// <summary>Fired when chat response lines are received from an agent. Delegates to lifecycle service.</summary>
    public event Action<string, IReadOnlyList<string>>? OnChatResponse
    {
        add => _lifecycle.OnChatResponse += value;
        remove => _lifecycle.OnChatResponse -= value;
    }

    /// <summary>Fired when a chat session completes on an agent. Delegates to lifecycle service.</summary>
    public event Action<string, int, string?>? OnChatCompleted
    {
        add => _lifecycle.OnChatCompleted += value;
        remove => _lifecycle.OnChatCompleted -= value;
    }

    /// <summary>The currently active pipeline run (used by test infrastructure), or null if idle. Delegates to lifecycle service.</summary>
    public PipelineRun? ActiveRun
    {
        get => _lifecycle.ActiveRun;
        protected set => _lifecycle.ActiveRun = value;
    }

    /// <summary>Whether a pipeline run is currently in progress (test infrastructure only in production). Delegates to lifecycle service.</summary>
    public bool IsRunning => _lifecycle.IsRunning;

    /// <summary>Whether any pipeline run is active (in-process or agent-dispatched). Delegates to lifecycle service.</summary>
    public bool HasAnyActiveRuns => _lifecycle.HasAnyActiveRuns;

    /// <summary>
    /// Returns all active runs — both the in-process run (if any) and all agent-dispatched runs.
    /// Delegates to lifecycle service.
    /// </summary>
    public IReadOnlyList<PipelineRun> GetAllActiveRuns() => _lifecycle.GetAllActiveRuns();

    /// <summary>
    /// Checks whether the given issue is being processed by any active run (in-process or agent-dispatched).
    /// Delegates to lifecycle service.
    /// </summary>
    public bool IsIssueBeingProcessed(string issueIdentifier, ProviderConfigId issueProviderConfigId) => _lifecycle.IsIssueBeingProcessed(issueIdentifier, issueProviderConfigId.Value);

    public PipelineOrchestrationService(
        IPipelineConfigStore pipelineConfigStore,
        IConfigurationStore configurationStore,
        IProviderFactory providerFactory,
        IssueDescriptionParser issueParser,
        IPipelineExecutionFacade executionFacade,
        IPipelineCompletionFacade completionFacade,
        IPipelineCancellationFacade cancellationFacade,
        PipelineRunLifecycleService lifecycle,
        ILabelService labelSwapper,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(pipelineConfigStore);
        ArgumentNullException.ThrowIfNull(configurationStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(issueParser);
        ArgumentNullException.ThrowIfNull(executionFacade);
        ArgumentNullException.ThrowIfNull(completionFacade);
        ArgumentNullException.ThrowIfNull(cancellationFacade);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(labelSwapper);
        ArgumentNullException.ThrowIfNull(logger);

        _pipelineConfigStore = pipelineConfigStore;
        _labelSwapper = labelSwapper;
        _issueParser = issueParser;
        _logger = logger;
        _executionFacade = executionFacade;
        _completionFacade = completionFacade;
        _cancellationFacade = cancellationFacade;
        _providerManager = new PipelineProviderManager(configurationStore, providerFactory, logger);
        _lifecycle = lifecycle;
    }

    /// <summary>Cancels the active pipeline run. Delegates state transitions to lifecycle service.</summary>
    public async Task CancelPipelineAsync()
    {
        if (ActiveRun == null || !IsRunning) return;
        var run = ActiveRun;
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
    /// Best-effort label swap to <see cref="AgentLabels.Cancelled"/> for all agent-dispatched active runs.
    /// Called during graceful shutdown to mark interrupted runs.
    /// Delegates state changes to lifecycle service, retains label swap logic.
    /// </summary>
    public async Task CancelActiveAgentRunsAsync()
    {
        // Label swap logic (requires provider resolution)
        var allRuns = _lifecycle.GetAllActiveRuns()
            .Where(r => r.AgentId != null)
            .ToList();

        if (allRuns.Count == 0) return;

        // Send CancelJob to each agent in parallel (2s per-agent timeout)
        // Non-fatal — agent may already be disconnected
        if (_cancellationFacade.AgentCancellation is not null)
        {
            try
            {
                var cancelTasks = allRuns.Select(run =>
                    _cancellationFacade.AgentCancellation.SendCancelJobAsync(run.AgentId!, run.RunId, CancellationToken.None));
                await Task.WhenAll(cancelTasks);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "One or more CancelJob sends failed during shutdown — proceeding with cleanup");
            }
        }

        foreach (var run in allRuns)
        {
            // TODO: Behavioral change — original code called SwapLabelAsync directly (exceptions would abort loop).
            // TrySwapLabelAsync swallows non-cancellation exceptions, so all runs are now attempted regardless of
            // individual failures. More resilient but changes exception propagation behavior.
            await _labelSwapper.TrySwapLabelAsync(run, AgentLabels.Cancelled, _logger, "PipelineOrchestrationService.CancelActiveAgentRunsAsync", CancellationToken.None);
        }

        // Delegate state changes to lifecycle — returns cancelled issue identifiers
        var cancelledIssues = await _lifecycle.MarkAgentRunsCancelled();

        // Release dedup guards so issues become re-dispatchable after restart
        if (_cancellationFacade.DedupGuard is not null)
        {
            foreach (var (issueId, providerId) in cancelledIssues)
            {
                _cancellationFacade.DedupGuard.MarkIssueComplete(issueId, providerId);
            }
        }
    }

    /// <summary>Returns the run history.</summary>
    public Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default)
        => _completionFacade.HistoryService.GetRunHistoryAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _providerManager.DisposeAsync();
        GC.SuppressFinalize(this);
    }
    public void Dispose()
    {
        // Do not call DisposePreviousProvidersAsync synchronously — .GetAwaiter().GetResult()
        // deadlocks in Blazor Server's SynchronizationContext (review finding #13).
        // DisposeAsync() is the correct disposal path; sync Dispose handles only sync resources.
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Notifies subscribers that chat response lines were received for a session.
    /// Delegates to lifecycle service.
    /// </summary>
    public void NotifyChatResponse(string sessionId, IReadOnlyList<string> lines)
        => _lifecycle.NotifyChatResponse(sessionId, lines);

    /// <summary>
    /// Notifies subscribers that a chat session has completed.
    /// Delegates to lifecycle service.
    /// </summary>
    public void NotifyChatCompleted(string sessionId, int exitCode, string? error)
        => _lifecycle.NotifyChatCompleted(sessionId, exitCode, error);

    /// <summary>
    /// Notifies subscribers of a state change. Delegates to lifecycle service.
    /// Called by AgentHub for agent-dispatched run state updates.
    /// </summary>
    public void NotifyChange() => _lifecycle.NotifyChange();

}
