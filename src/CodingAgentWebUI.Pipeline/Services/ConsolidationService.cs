using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Manages consolidation loop execution: triggering runs, tracking history,
/// persisting run records, and managing harness suggestions.
/// </summary>
public sealed class ConsolidationService : IConsolidationService, IConsolidationRunTracker
{
    private readonly ILogger _logger;
    private readonly IConsolidationDispatchService? _dispatcher;
    private readonly IConsolidationRunStore _runStore;
    private readonly IHarnessSuggestionStore _harnessSuggestionStore;
    private readonly IConsolidationWorkspaceManager _workspaceManager;
    private readonly IConsolidationFeedbackCache _feedbackCache;
    private readonly ConsolidationTemplateResolver _templateResolver;

    private readonly ConcurrentDictionary<(ConsolidationRunType, string?), ConsolidationRun> _runningRuns = new();
    private IReadOnlyList<ConsolidationRun>? _runHistoryCache;

    /// <inheritdoc />
    public event Action? OnChange;

    /// <inheritdoc />
    public bool IsRunActive(string runId) => _runningRuns.Values.Any(r => r.RunId == runId);

    /// <inheritdoc />
    public DateTimeOffset? GetActiveRunStartedAt(string runId)
    {
        var run = _runningRuns.Values.FirstOrDefault(r => r.RunId == runId);
        return run?.StartedAtUtc;
    }

    public ConsolidationService(
        ILogger logger,
        PipelineConfiguration config,
        IProjectStore projectStore,
        IPipelineRunHistoryService runHistoryService,
        IConsolidationRunStore runStore,
        IHarnessSuggestionStore harnessSuggestionStore,
        IConsolidationDispatchService? dispatcher = null,
        IConsolidationWorkspaceManager? workspaceManager = null,
        IConsolidationFeedbackCache? feedbackCache = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(runHistoryService);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(harnessSuggestionStore);

        _logger = logger;
        _runStore = runStore;
        _harnessSuggestionStore = harnessSuggestionStore;
        _dispatcher = dispatcher;
        _workspaceManager = workspaceManager ?? new ConsolidationWorkspaceManager(logger, config);
        _feedbackCache = feedbackCache ?? new ConsolidationFeedbackCache(logger, runStore, runHistoryService);
        _templateResolver = new ConsolidationTemplateResolver(projectStore);
    }

    /// <inheritdoc />
    public async Task CleanupOrphanedRunsAsync(CancellationToken ct)
    {
        var allRuns = await _runStore.LoadAllRunsAsync(ct);
        foreach (var run in allRuns.Where(r => r.Status == ConsolidationRunStatus.Running))
        {
            run.Status = ConsolidationRunStatus.Failed;
            run.Summary = "Orphaned: application restarted before completion";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            await _runStore.SaveRunAsync(run, ct);
            _logger.Information("Marked orphaned consolidation run {RunId} ({Type}) as Failed", run.RunId, run.Type);
        }
        _runHistoryCache = null;
    }

    /// <inheritdoc />
    // TODO: CancellationToken should be the last parameter per .NET convention. Changing this
    // signature is a breaking change across all callers — defer to a separate cleanup pass.
    public async Task<ConsolidationRun?> TriggerAsync(
        ConsolidationRunType type,
        string? templateId,
        CancellationToken ct,
        bool autoDispatch = false)
    {
        var key = (type, templateId);

        // Resolve template name for display
        string? templateName;
        string? projectName = null;
        if (templateId is not null)
        {
            var (template, resolvedProjectName) = await _templateResolver.ResolveTemplateWithProjectAsync(templateId, ct);
            if (template is null)
            {
                _logger.Warning("Consolidation run rejected: template {TemplateId} not found", templateId);
                return null;
            }
            templateName = template.Name;
            projectName = resolvedProjectName;
        }
        else
        {
            templateName = "Global";
        }

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = type,
            TemplateId = templateId,
            TemplateName = templateName,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Running,
            AutoDispatch = autoDispatch,
            ProjectName = projectName
        };

        if (!_runningRuns.TryAdd(key, run))
        {
            _logger.Warning(
                "Consolidation run rejected: {Type} for template {TemplateId} is already running or queued",
                type, templateId ?? "Global");
            return null;
        }

        if (type == ConsolidationRunType.HarnessSuggestions)
            await _feedbackCache.PrepareFeedbackDataAsync(run, ct);

        try
        {
            await PersistRunAsync(run, ct);
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Failed to persist consolidation run {RunId} for {Type}/{TemplateName} — rolling back in-memory state",
                run.RunId, type, templateName);
            _runningRuns.TryRemove(key, out _);
            // TODO: Clear feedback cache here for HarnessSuggestions runs — currently leaves orphaned data
            // in the ConcurrentDictionary until process restart when PersistRunAsync fails.
            return null;
        }

        // TODO: DispatchRunAsync re-throws exceptions, giving TriggerAsync two failure semantics:
        // returning null (concurrency/persistence/dispatch failure) vs throwing (dispatch exception).
        // Callers that only check null will get an unhandled exception on dispatch errors.
        var outcome = await DispatchRunAsync(run, key, type, templateName, ct);
        if (outcome == DispatchOutcome.Queued)
            return run;
        if (outcome == DispatchOutcome.Failed)
            return null;

        _logger.Information("Consolidation run {RunId} created: {Type} for {TemplateName}", run.RunId, type, templateName);
        OnChange?.Invoke();
        return run;
    }

    /// <summary>
    /// Dispatches a consolidation run to an idle agent. Handles queued/failed/exception outcomes.
    /// </summary>
    private async Task<DispatchOutcome> DispatchRunAsync(
        ConsolidationRun run,
        (ConsolidationRunType, string?) key,
        ConsolidationRunType type,
        string templateName,
        CancellationToken ct)
    {
        if (_dispatcher is null)
            return DispatchOutcome.NoDispatcher;

        try
        {
            var feedbackDataJson = type == ConsolidationRunType.HarnessSuggestions
                ? _feedbackCache.GetFeedbackDataForRun(run.RunId)
                : null;
            var workspacePath = _workspaceManager.GetWorkspacePath(run.RunId);

            var result = await _dispatcher.TryDispatchAsync(
                run, type, run.TemplateId, feedbackDataJson, workspacePath, ct);

            if (result == ConsolidationDispatchResult.Queued)
            {
                run.Status = ConsolidationRunStatus.Queued;
                await PersistRunAsync(run, ct);
                _feedbackCache.ClearFeedbackDataForRun(run.RunId);
                _logger.Information(
                    "Consolidation run {RunId} queued: {Type} for {TemplateName} — waiting for idle agent",
                    run.RunId, type, templateName);
                OnChange?.Invoke();
                return DispatchOutcome.Queued;
            }

            if (result == ConsolidationDispatchResult.Failed)
            {
                _logger.Warning("Consolidation run {RunId} dispatch failed for {Type}/{TemplateName}", run.RunId, type, templateName);
                _runningRuns.TryRemove(key, out _);
                DeletePersistedRun(run.RunId);
                _feedbackCache.ClearFeedbackDataForRun(run.RunId);
                return DispatchOutcome.Failed;
            }

            _feedbackCache.ClearFeedbackDataForRun(run.RunId);
            return DispatchOutcome.Success;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Consolidation run {RunId} dispatch failed with exception for {Type}/{TemplateName}", run.RunId, type, templateName);
            _runningRuns.TryRemove(key, out _);
            DeletePersistedRun(run.RunId);
            _feedbackCache.ClearFeedbackDataForRun(run.RunId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConsolidationRun>> GetRunHistoryAsync(CancellationToken ct)
    {
        if (_runHistoryCache is not null)
            return _runHistoryCache;

        var runs = await _runStore.LoadAllRunsAsync(ct);
        var result = runs.OrderByDescending(r => r.StartedAtUtc).ToList();
        _runHistoryCache = result;
        return result;
    }

    /// <inheritdoc />
    public async Task<ConsolidationRun?> GetLastRunAsync(
        ConsolidationRunType type, string? templateId, CancellationToken ct)
    {
        var allRuns = await GetRunHistoryAsync(ct);
        return allRuns
            .Where(r => r.Type == type && r.TemplateId == templateId)
            .OrderByDescending(r => r.StartedAtUtc)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task UpdateRunAsync(
        string runId, ConsolidationRunStatus status, string? summary,
        CancellationToken ct, long totalTokens = 0)
    {
        ArgumentNullException.ThrowIfNull(runId);

        if (!Guid.TryParse(runId, out _))
        {
            _logger.Warning("Invalid runId format: {RunId}", runId);
            return;
        }

        try
        {
            var run = await _runStore.GetByIdAsync(runId, ct);
            if (run is null)
            {
                _logger.Warning("Cannot update consolidation run {RunId}: not found", runId);
                return;
            }

            if (run.Status is ConsolidationRunStatus.Succeeded
                or ConsolidationRunStatus.Failed
                or ConsolidationRunStatus.Cancelled)
            {
                _logger.Debug(
                    "Skipping update for consolidation run {RunId}: already in terminal status {CurrentStatus} (requested: {RequestedStatus})",
                    runId, run.Status, status);
                return;
            }

            run.Status = status;
            run.Summary = summary;
            if (status is ConsolidationRunStatus.Succeeded or ConsolidationRunStatus.Failed or ConsolidationRunStatus.Cancelled)
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
            run.TotalTokens = totalTokens;

            await PersistRunAsync(run, ct);

            if (status != ConsolidationRunStatus.Running && status != ConsolidationRunStatus.Queued)
            {
                var key = (run.Type, run.TemplateId);
                _runningRuns.TryRemove(key, out _);
            }

            _workspaceManager.CleanupWorkspaceIfSucceeded(runId, status);
            _logger.Information("Consolidation run {RunId} updated: {Status} — {Summary}", runId, status, summary ?? "(no summary)");
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update consolidation run {RunId}", runId);
        }
    }

    /// <inheritdoc />
    public async Task<bool> CancelQueuedRunAsync(string runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);

        if (!Guid.TryParse(runId, out _))
            return false;

        try
        {
            var run = await _runStore.GetByIdAsync(runId, ct);
            if (run is null || run.Status != ConsolidationRunStatus.Queued)
                return false;

            run.Status = ConsolidationRunStatus.Cancelled;
            run.CompletedAtUtc = DateTime.UtcNow;
            run.Summary = "Cancelled by user";

            await PersistRunAsync(run, ct);

            var key = (run.Type, run.TemplateId);
            _runningRuns.TryRemove(key, out _);

            if (_dispatcher is not null)
                await _dispatcher.NotifyRunCancelledAsync(runId, ct);

            _logger.Information("Consolidation run {RunId} cancelled", runId);
            OnChange?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to cancel consolidation run {RunId}", runId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task TransitionToRunningAsync(string runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);

        if (!Guid.TryParse(runId, out _))
            return;

        try
        {
            var run = await _runStore.GetByIdAsync(runId, ct);
            if (run is null || run.Status != ConsolidationRunStatus.Queued)
                return;

            run.Status = ConsolidationRunStatus.Running;
            run.StartedAtUtc = DateTimeOffset.UtcNow;
            await PersistRunAsync(run, ct);

            var key = (run.Type, run.TemplateId);
            _runningRuns.AddOrUpdate(key, run, (_, _) => run);

            _logger.Information("Consolidation run {RunId} transitioned from Queued to Running (StartedAtUtc reset)", runId);
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to transition consolidation run {RunId} to Running", runId);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConsolidationRun>> RehydrateQueuedRunsAsync(CancellationToken ct)
    {
        var queuedRuns = new List<ConsolidationRun>();
        var allRuns = await _runStore.LoadAllRunsAsync(ct);

        foreach (var run in allRuns.Where(r => r.Status == ConsolidationRunStatus.Queued))
        {
            var key = (run.Type, run.TemplateId);
            _runningRuns.TryAdd(key, run);
            queuedRuns.Add(run);
            _logger.Information("Rehydrated queued consolidation run {RunId} ({Type}) for re-enqueuing", run.RunId, run.Type);
        }

        return queuedRuns;
    }

    /// <inheritdoc />
    public async Task<HarnessSuggestions?> GetHarnessSuggestionsAsync(CancellationToken ct)
    {
        try { return await _harnessSuggestionStore.GetAsync(ct); }
        catch (Exception ex) { _logger.Warning(ex, "Failed to read harness suggestions"); return null; }
    }

    /// <inheritdoc />
    public async Task SaveHarnessSuggestionsAsync(HarnessSuggestions suggestions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        try
        {
            await _harnessSuggestionStore.SaveAsync(suggestions, ct);
            _logger.Information("Harness suggestions saved");
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save harness suggestions");
        }
    }

    /// <summary>Clears in-memory concurrency state. Used by E2E tests for isolation.</summary>
    internal void Reset() => _runningRuns.Clear();

    private async Task PersistRunAsync(ConsolidationRun run, CancellationToken ct)
    {
        await _runStore.SaveRunAsync(run, ct);
        _runHistoryCache = null;
    }

    /// <summary>Deletes a persisted run (used when dispatch fails and the run must be rolled back).</summary>
    internal async Task DeletePersistedRunAsync(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        try
        {
            await _runStore.DeleteRunAsync(runId, CancellationToken.None);
            _runHistoryCache = null;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete persisted consolidation run {RunId}", runId);
        }
    }

    private void DeletePersistedRun(string runId)
    {
        _ = _runStore.DeleteRunAsync(runId, CancellationToken.None).ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.Warning(t.Exception?.InnerException, "Failed to delete persisted consolidation run {RunId}", runId);
        }, TaskScheduler.Default);
        _runHistoryCache = null;
    }

    private enum DispatchOutcome { Success, Queued, Failed, NoDispatcher }
}
