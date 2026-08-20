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

    /// <inheritdoc />
    public event Action? OnChange;

    /// <inheritdoc />
    public bool IsRunActive(RunId runId) => _runningRuns.Values.Any(r => r.RunId == runId.Value);

    /// <inheritdoc />
    public DateTimeOffset? GetActiveRunStartedAt(RunId runId)
    {
        var run = _runningRuns.Values.FirstOrDefault(r => r.RunId == runId.Value);
        return run?.StartedAtUtc;
    }

    public ConsolidationService(
        ConsolidationServiceDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Logger);
        ArgumentNullException.ThrowIfNull(deps.Config);
        ArgumentNullException.ThrowIfNull(deps.ProjectStore);
        ArgumentNullException.ThrowIfNull(deps.RunHistoryService);
        ArgumentNullException.ThrowIfNull(deps.RunStore);
        ArgumentNullException.ThrowIfNull(deps.HarnessSuggestionStore);

        _logger = deps.Logger;
        _runStore = deps.RunStore;
        _harnessSuggestionStore = deps.HarnessSuggestionStore;
        _dispatcher = deps.Dispatcher;
        _workspaceManager = deps.WorkspaceManager ?? new ConsolidationWorkspaceManager(deps.Logger, deps.Config);
        _feedbackCache = deps.FeedbackCache ?? new ConsolidationFeedbackCache(deps.Logger, deps.RunStore, deps.RunHistoryService);
        _templateResolver = new ConsolidationTemplateResolver(deps.ProjectStore);
    }

    /// <inheritdoc />
    public async Task CleanupOrphanedRunsAsync(IReadOnlyCollection<string> activeAgentJobIds, CancellationToken ct)
    {
        var allRuns = await _runStore.LoadAllRunsAsync(ct);
        // Only consider runs that are in Running state — already-terminal runs (Succeeded, Failed,
        // Cancelled) must never be overwritten, even if they happened to be Running at the last
        // shutdown. The API may have updated them to a terminal state after the orchestrator went down.
        foreach (var run in allRuns.Where(r => r.Status == ConsolidationRunStatus.Running))
        {
            // Skip runs where an agent is still actively working — the agent connects to
            // the API hub which survives orchestrator restarts, so the run is not orphaned.
            if (activeAgentJobIds.Contains(run.RunId))
            {
                _logger.Information("Consolidation run {RunId} ({Type}) has active agent — skipping orphan cleanup", run.RunId, run.Type);
                // Restore to _runningRuns so TriggerAsync dedup guard works correctly and
                // prevents a duplicate dispatch for the same (type, templateId) key.
                var key = (run.Type, run.TemplateId);
                _runningRuns.TryAdd(key, run);
                continue;
            }

            run.Status = ConsolidationRunStatus.Failed;
            run.Summary = "Orphaned: application restarted before completion";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            await _runStore.SaveRunAsync(run, ct);
            _logger.Information("Marked orphaned consolidation run {RunId} ({Type}) as Failed", run.RunId, run.Type);
        }
        
    }

    /// <inheritdoc />
    public async Task<ConsolidationRun?> TriggerAsync(
        ConsolidationRunType type,
        TemplateId? templateId,
        CancellationToken ct,
        bool autoDispatch = false)
    {
        var templateIdValue = templateId?.Value;
        var key = (type, templateIdValue);

        // Resolve template name for display
        string? templateName;
        string? projectName = null;
        if (templateId is not null)
        {
            var (template, resolvedProjectName) = await _templateResolver.ResolveTemplateWithProjectAsync(templateIdValue!, ct);
            if (template is null)
            {
                _logger.Warning("Consolidation run rejected: template {TemplateId} not found", templateIdValue);
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
            TemplateId = templateIdValue,
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
            if (type == ConsolidationRunType.HarnessSuggestions)
                _feedbackCache.ClearFeedbackDataForRun(run.RunId);
            return null;
        }

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
                run, type, string.IsNullOrEmpty(run.TemplateId) ? (TemplateId?)null : (TemplateId)run.TemplateId, feedbackDataJson, workspacePath, ct);

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
                await DeletePersistedRunAsync(run.RunId);
                _feedbackCache.ClearFeedbackDataForRun(run.RunId);
                return DispatchOutcome.Failed;
            }

            _feedbackCache.ClearFeedbackDataForRun(run.RunId);
            return DispatchOutcome.Success;
        }
        catch (Exception)
        {
            // Exception is propagated to the caller (TriggerAsync); logging here would cause
            // duplicate log entries. Cleanup is done before rethrowing.
            _runningRuns.TryRemove(key, out _);
            await DeletePersistedRunAsync(run.RunId);
            _feedbackCache.ClearFeedbackDataForRun(run.RunId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConsolidationRun>> GetRunHistoryAsync(CancellationToken ct)
    {
        // Always read from store — the store is the authoritative source.
        // In the multi-process architecture (Spec 041+), the API updates ConsolidationRun
        // status directly in the DB. An in-memory cache here would lag behind those updates
        // and cause the monitoring page to show stale Queued status after dispatch.
        var runs = await _runStore.LoadAllRunsAsync(ct);
        return runs.OrderByDescending(r => r.StartedAtUtc).ToList();
    }

    /// <inheritdoc />
    public async Task<ConsolidationRun?> GetLastRunAsync(
        ConsolidationRunType type, TemplateId? templateId, CancellationToken ct)
    {
        var templateIdValue = templateId?.Value;
        var allRuns = await GetRunHistoryAsync(ct);
        return allRuns
            .Where(r => r.Type == type && r.TemplateId == templateIdValue)
            .OrderByDescending(r => r.StartedAtUtc)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task UpdateRunAsync(
        RunId runId, ConsolidationRunStatus status, string? summary,
        CancellationToken ct, long totalTokens = 0)
    {
        if (!Guid.TryParse(runId.Value, out _))
        {
            _logger.Warning("Invalid runId format: {RunId}", runId.Value);
            return;
        }

        try
        {
            var run = await _runStore.GetByIdAsync(runId, ct);
            if (run is null)
            {
                _logger.Warning("Cannot update consolidation run {RunId}: not found", runId.Value);
                return;
            }

            if (run.Status is ConsolidationRunStatus.Succeeded
                or ConsolidationRunStatus.Failed
                or ConsolidationRunStatus.Cancelled)
            {
                _logger.Debug(
                    "Skipping update for consolidation run {RunId}: already in terminal status {CurrentStatus} (requested: {RequestedStatus})",
                    runId.Value, run.Status, status);
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
            _logger.Information("Consolidation run {RunId} updated: {Status} — {Summary}", runId.Value, status, summary ?? "(no summary)");
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update consolidation run {RunId}", runId.Value);
        }
    }

    /// <inheritdoc />
    public async Task<bool> CancelQueuedRunAsync(RunId runId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId.Value, out _))
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

            _logger.Information("Consolidation run {RunId} cancelled", runId.Value);
            OnChange?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to cancel consolidation run {RunId}", runId.Value);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task TransitionToRunningAsync(RunId runId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId.Value, out _))
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

            _logger.Information("Consolidation run {RunId} transitioned from Queued to Running (StartedAtUtc reset)", runId.Value);
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to transition consolidation run {RunId} to Running", runId.Value);
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
        
    }

    /// <inheritdoc />
    public async Task DeleteRunAsync(RunId runId, CancellationToken ct)
    {
        try
        {
            await _runStore.DeleteRunAsync(runId, ct);
            
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete consolidation run {RunId}", runId.Value);
        }
    }

    /// <summary>Deletes a persisted run (used when dispatch fails and the run must be rolled back).</summary>
    internal async Task DeletePersistedRunAsync(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        try
        {
            await _runStore.DeleteRunAsync(runId, CancellationToken.None);
            
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete persisted consolidation run {RunId}", runId);
        }
    }

    private enum DispatchOutcome { Success, Queued, Failed, NoDispatcher }
}
