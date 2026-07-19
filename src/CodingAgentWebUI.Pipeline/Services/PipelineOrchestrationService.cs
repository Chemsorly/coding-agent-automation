﻿﻿using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog.Context;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Singleton service that coordinates the automated development pipeline.
/// Manages provider resolution, execution orchestration, label swaps, and PR creation.
/// Delegates run state, lifecycle transitions, events, and cancellation to <see cref="PipelineRunLifecycleService"/>.
/// Pipeline execution is handled by remote agents via <see cref="CreateDispatchedRunAsync"/> and
/// <c>LocalPipelineExecutor</c>. Multi-agent dispatch uses concurrent runs tracked via <see cref="IOrchestratorRunService"/>.
/// </summary>
// Provider lifecycle management (resolution, disposal, active provider tracking) is delegated
// to PipelineProviderManager, extracted per spec 017 / MAINT-09.
//
// IProviderOperationsFacade evaluation: IPipelineCallbacks already covers SwapAgentLabel,
// RemoveAllAgentLabels, and CreatePullRequest. A separate facade would add indirection
// without meaningful simplification. Revisit if pipeline steps accumulate more
// provider-operation parameters beyond what IPipelineCallbacks covers.
public class PipelineOrchestrationService : IDisposable, IAsyncDisposable, IOrchestrationShutdownAction, IDispatchRunCreator, IChangeNotifier
{
    private readonly PipelineRunLifecycleService _lifecycle;
    private readonly IPipelineConfigStore _pipelineConfigStore;
    private readonly IConfigurationStore _configurationStore;
    private readonly IProviderFactory _providerFactory;
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
        _configurationStore = configurationStore;
        _providerFactory = providerFactory;
        _labelSwapper = labelSwapper;
        _issueParser = issueParser;
        _logger = logger;
        _executionFacade = executionFacade;
        _completionFacade = completionFacade;
        _cancellationFacade = cancellationFacade;
        _providerManager = new PipelineProviderManager(configurationStore, providerFactory, logger);
        _lifecycle = lifecycle;
    }

    /// <summary>
    /// Creates a <see cref="PipelineRun"/> for dispatch to a remote agent.
    /// The run is tracked via <see cref="IOrchestratorRunService"/> (not the local <see cref="ActiveRun"/>).
    /// Does NOT execute the pipeline locally — the agent handles execution.
    /// </summary>
    /// <returns>The created <see cref="PipelineRun"/> ready for dispatch, or <c>null</c> if the issue is already being processed.</returns>
    public async Task<PipelineRun?> CreateDispatchedRunAsync(
        ProviderConfigId issueProviderId, ProviderConfigId repoProviderId, string issueIdentifier,
        ProviderConfigId agentProviderId, string? agentId, CancellationToken ct,
        string? brainProviderId = null, string? pipelineProviderId = null,
        string initiatedBy = "dispatch",
        PipelineRunType runType = PipelineRunType.Implementation)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        // TODO: Validate that ProviderConfigId.Value is not null/empty for issueProviderId,
        // repoProviderId, and agentProviderId. The previous string parameters had
        // ArgumentNullException.ThrowIfNull guards that are now lost because structs can't be null,
        // but default(ProviderConfigId) or implicit conversion from null still produces Value = null.

        if (_lifecycle.IsIssueBeingProcessed(issueIdentifier, issueProviderId.Value))
        {
            _logger.Warning("Issue {IssueIdentifier} is already being processed, skipping dispatch", issueIdentifier);
            return null;
        }

        var run = await ResolveAndCreateRunAsync(repoProviderId, agentProviderId, issueIdentifier,
            issueProviderId, agentId, brainProviderId, pipelineProviderId, initiatedBy, runType, ct);

        if (!_lifecycle.RegisterDispatchedRun(run))
            return null;

        _logger.Information(
            "Dispatched run {RunId} created for issue {IssueIdentifier} → agent {AgentId}",
            run.RunId, issueIdentifier, agentId);

        return run;
    }

    /// <summary>
    /// Reserves a run ID and registers a dedup guard (sentinel) without constructing a full
    /// <see cref="PipelineRun"/>. Returns metadata needed to construct the final run.
    /// The sentinel immediately makes <see cref="IsIssueBeingProcessed"/> return <c>true</c>.
    /// </summary>
    /// <returns>A <see cref="RunReservation"/> with the allocated RunId and resolved metadata,
    /// or <c>null</c> if the issue is already being processed.</returns>
    public async Task<RunReservation?> ReserveRunIdAsync(
        ProviderConfigId issueProviderId, ProviderConfigId repoProviderId, string issueIdentifier,
        ProviderConfigId agentProviderId, string? agentId, CancellationToken ct,
        string? brainProviderId = null, string? pipelineProviderId = null,
        string initiatedBy = "dispatch")
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);

        if (_lifecycle.IsIssueBeingProcessed(issueIdentifier, issueProviderId.Value))
        {
            _logger.Warning("Issue {IssueIdentifier} is already being processed, skipping reservation", issueIdentifier);
            return null;
        }

        // TODO: startedAt is captured before ResolveAndCreateRunAsync (provider resolution).
        // Original code captured it after provider resolution. If excluding provider resolution
        // latency from start time matters, move this assignment after the helper call.
        var startedAt = DateTimeOffset.UtcNow;

        var sentinel = await ResolveAndCreateRunAsync(repoProviderId, agentProviderId, issueIdentifier,
            issueProviderId, agentId, brainProviderId, pipelineProviderId, initiatedBy,
            PipelineRunType.Implementation, ct);

        if (!_lifecycle.RegisterDispatchedRun(sentinel))
            return null;

        _logger.Information(
            "Reserved run {RunId} for issue {IssueIdentifier}",
            sentinel.RunId, issueIdentifier);

        return new RunReservation(sentinel.RunId, sentinel.RepositoryName!, sentinel.ModelName!, startedAt);
    }

    /// <summary>
    /// Resolves provider configs and creates a fully-constructed <see cref="PipelineRun"/> with
    /// metadata (RepositoryName, ModelName, PipelineProviderConfigId) already set.
    /// Shared by <see cref="CreateDispatchedRunAsync"/> and <see cref="ReserveRunIdAsync"/>.
    /// </summary>
    private async Task<PipelineRun> ResolveAndCreateRunAsync(
        ProviderConfigId repoProviderId,
        ProviderConfigId agentProviderId,
        string issueIdentifier,
        ProviderConfigId issueProviderId,
        string? agentId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        PipelineRunType runType,
        CancellationToken ct)
    {
        var repoProviderConfig = await _providerManager.ResolveProviderConfigAsync(repoProviderId.Value, ProviderKind.Repository, ct);
        await using var tempRepoProvider = _providerFactory.CreateRepositoryProvider(repoProviderConfig);
        var agentProviderConfig = await _providerManager.ResolveProviderConfigAsync(agentProviderId.Value, ProviderKind.Agent, ct);
        var configuredModel = agentProviderConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Model, "auto");

        var run = PipelineRun.Create(
            runId: Guid.NewGuid().ToString(),
            issueIdentifier: issueIdentifier,
            issueTitle: string.Empty,
            issueProviderConfigId: issueProviderId.Value,
            repoProviderConfigId: repoProviderId.Value,
            runType: runType,
            initiatedBy: initiatedBy,
            agentId: agentId,
            agentProviderConfigId: agentProviderId.Value,
            brainProviderConfigId: brainProviderId);
        run.RepositoryName = tempRepoProvider.RepositoryFullName;
        run.ModelName = configuredModel;
        run.PipelineProviderConfigId = pipelineProviderId;

        return run;
    }

    /// <summary>
    /// Registers a fully-constructed <see cref="PipelineRun"/> by atomically replacing the
    /// sentinel created by <see cref="ReserveRunIdAsync"/>. The run must use the same RunId
    /// that was returned in the <see cref="RunReservation"/>.
    /// </summary>
    /// <param name="run">The fully-populated pipeline run to register.</param>
    public void RegisterDispatchedRun(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _lifecycle.ReplaceDispatchedRun(run);
    }

    /// <summary>Cancels the active pipeline run. Delegates state transitions to lifecycle service.</summary>
    public async Task CancelPipelineAsync()
    {
        if (ActiveRun == null || !IsRunning) return;
        var run = ActiveRun;
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);

        // Label swap requires the active issue provider (orchestration concern)
        if (_providerManager.ActiveIssueProvider != null || run.RunType == PipelineRunType.Review)
            await SwapAgentLabelAsync(run, run.IssueIdentifier, AgentLabels.Cancelled, CancellationToken.None);

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
            var targetKind = run.LabelTargetKind;

            await _labelSwapper.SwapLabelAsync(
                run.ProviderConfigIdForLabel, run.IssueIdentifier, AgentLabels.Cancelled, targetKind, CancellationToken.None);
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

    // --- Private helpers ---
    // TODO: Add unit test that exercises SwapAgentLabelAsync for Implementation runs during normal pipeline execution
    // (existing tests use TestPipelineRunner which bypasses ILabelService and doesn't validate this code path).
    private async Task SwapAgentLabelAsync(PipelineRun run, string issueId, string newLabel, CancellationToken ct)
    {
        _logger.Information(
            "Pipeline {RunId} SwapAgentLabelAsync: {IssueIdentifier} → {Label} (runType={RunType}, step={CurrentStep})",
            run.RunId, issueId, newLabel, run.RunType, run.CurrentStep);
        try
        {
            var targetKind = run.LabelTargetKind;

            await _labelSwapper.SwapLabelAsync(run.ProviderConfigIdForLabel, issueId, newLabel, targetKind, ct);
        }
        catch (Exception ex) { _logger.Warning(ex, "Failed to swap agent label to {Label} on {Identifier}", newLabel, issueId); }
    }

    // TODO: Add unit test that validates RemoveAllAgentLabelsAsync routes through ILabelService with string.Empty,
    // selecting the correct providerConfigId and targetKind for Implementation runs.
    internal async Task RemoveAllAgentLabelsAsync(PipelineRun run, string issueId, CancellationToken ct)
    {
        try
        {
            var targetKind = run.LabelTargetKind;

            await _labelSwapper.SwapLabelAsync(run.ProviderConfigIdForLabel, issueId, string.Empty, targetKind, ct);
        }
        catch (Exception ex) { _logger.Warning(ex, "Failed to remove agent labels from {Identifier}", issueId); }
    }

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
