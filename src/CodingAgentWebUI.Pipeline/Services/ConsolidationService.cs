using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Manages consolidation loop execution: triggering runs, tracking history,
/// persisting run records, and managing harness suggestions.
/// </summary>
public sealed class ConsolidationService : IConsolidationService
{
    private readonly ILogger _logger;
    private readonly PipelineConfiguration _config;
    private readonly IPipelineRunHistoryService _runHistoryService;
    // TODO: _dispatcher is written from SetDispatcher (startup) and read from TriggerAsync (request threads).
    // Mark as volatile or use Volatile.Write/Read for cross-thread visibility correctness.
    private IConsolidationDispatcher? _dispatcher;
    private readonly string _consolidationRunsDirectory;
    private readonly string _harnessSuggestionsPath;

    /// <summary>
    /// Callback invoked to enqueue a job into the consolidation queue service.
    /// Set via <see cref="SetQueueCallbacks"/> after construction to avoid circular DI.
    /// </summary>
    private Func<PendingConsolidationJob, bool>? _enqueueCallback;
    private Action<string>? _markCancelledCallback;
    private Func<string, bool>? _removeFromQueueCallback;

    /// <summary>
    /// Tracks currently running/queued consolidation runs by (type, templateId) to enforce concurrency guard.
    /// </summary>
    private readonly ConcurrentDictionary<(ConsolidationRunType, string?), ConsolidationRun> _runningRuns = new();

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <inheritdoc />
    public event Action? OnChange;

    public ConsolidationService(
        ILogger logger,
        PipelineConfiguration config,
        IPipelineRunHistoryService runHistoryService,
        IConsolidationDispatcher? dispatcher = null,
        string consolidationRunsDirectory = PipelineConstants.ConsolidationRunsDirectory,
        string harnessSuggestionsPath = PipelineConstants.HarnessSuggestionsPath)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(runHistoryService);

        _logger = logger;
        _config = config;
        _runHistoryService = runHistoryService;
        _dispatcher = dispatcher;
        _consolidationRunsDirectory = consolidationRunsDirectory;
        _harnessSuggestionsPath = harnessSuggestionsPath;
    }

    /// <summary>
    /// Sets queue service callbacks. Called after construction to break circular DI dependency.
    /// </summary>
    public void SetQueueCallbacks(
        Func<PendingConsolidationJob, bool> enqueueCallback,
        Action<string> markCancelledCallback,
        Func<string, bool> removeFromQueueCallback)
    {
        // TODO: Add ArgumentNullException.ThrowIfNull for all three parameters
        _enqueueCallback = enqueueCallback;
        _markCancelledCallback = markCancelledCallback;
        _removeFromQueueCallback = removeFromQueueCallback;
    }

    /// <summary>
    /// Sets the dispatcher after construction to break circular DI dependency.
    /// </summary>
    public void SetDispatcher(IConsolidationDispatcher dispatcher)
    {
        // TODO: Add ArgumentNullException.ThrowIfNull(dispatcher)
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public async Task CleanupOrphanedRunsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_consolidationRunsDirectory))
            return;

        foreach (var file in Directory.GetFiles(_consolidationRunsDirectory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var run = JsonSerializer.Deserialize<ConsolidationRun>(json, s_jsonOptions);
                if (run is null) continue;

                if (run.Status == ConsolidationRunStatus.Running)
                {
                    run.Status = ConsolidationRunStatus.Failed;
                    run.Summary = "Orphaned: application restarted before completion";
                    run.CompletedAtUtc = DateTime.UtcNow;

                    var updatedJson = JsonSerializer.Serialize(run, s_jsonOptions);
                    await File.WriteAllTextAsync(file, updatedJson, ct);

                    _logger.Information(
                        "Marked orphaned consolidation run {RunId} ({Type}) as Failed",
                        run.RunId, run.Type);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to process consolidation run file {File} during orphan cleanup", file);
            }
        }
    }

    /// <inheritdoc />
    public async Task RehydrateQueuedRunsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_consolidationRunsDirectory))
            return;

        foreach (var file in Directory.GetFiles(_consolidationRunsDirectory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var run = JsonSerializer.Deserialize<ConsolidationRun>(json, s_jsonOptions);
                if (run is null || run.Status != ConsolidationRunStatus.Queued)
                    continue;

                var key = (run.Type, run.TemplateId);
                _runningRuns.TryAdd(key, run);

                var job = new PendingConsolidationJob
                {
                    RunId = run.RunId,
                    Type = run.Type,
                    TemplateId = run.TemplateId,
                    WorkspacePath = GetWorkspacePath(run.RunId),
                    RequiredLabels = run.QueuedRequiredLabels ?? [],
                    EnqueuedAt = new DateTimeOffset(run.StartedAtUtc, TimeSpan.Zero),
                    FeedbackSinceUtc = run.Type == ConsolidationRunType.HarnessSuggestions
                        ? await GetLastSuccessfulHarnessRunTimestampAsync(ct)
                        : null
                };

                _enqueueCallback?.Invoke(job);

                _logger.Information(
                    "Rehydrated queued consolidation run {RunId} ({Type}) from disk",
                    run.RunId, run.Type);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to rehydrate consolidation run from {File}", file);
            }
        }
    }

    /// <inheritdoc />
    public async Task<ConsolidationRun?> TriggerAsync(
        ConsolidationRunType type,
        string? templateId,
        CancellationToken ct)
    {
        var key = (type, templateId);

        // Resolve template name for display
        string? templateName = null;
        if (templateId is not null)
        {
            var template = _config.PipelineJobTemplates
                .FirstOrDefault(t => t.Id == templateId);
            if (template is null)
            {
                _logger.Warning("Consolidation run rejected: template {TemplateId} not found", templateId);
                return null;
            }
            templateName = template.Name;
        }
        else
        {
            templateName = "Global";
        }

        // Create the consolidation run record
        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = type,
            TemplateId = templateId,
            TemplateName = templateName,
            StartedAtUtc = DateTime.UtcNow,
            Status = ConsolidationRunStatus.Queued
        };

        // Register in concurrency tracker — TryAdd is the sole guard (no separate ContainsKey check)
        if (!_runningRuns.TryAdd(key, run))
        {
            _logger.Warning(
                "Consolidation run rejected: {Type} for template {TemplateId} is already running or queued",
                type, templateId ?? "Global");
            return null;
        }

        // For harness suggestions: prepare feedback data (filtered by last successful run)
        if (type == ConsolidationRunType.HarnessSuggestions)
        {
            await PrepareFeedbackDataAsync(run, ct);
        }

        // Persist the run record (initially Queued)
        await PersistRunAsync(run, ct);

        // Dispatch the job to an idle agent (wrapped in try-catch to prevent concurrency state leak)
        if (_dispatcher is not null)
        {
            try
            {
                var feedbackDataJson = type == ConsolidationRunType.HarnessSuggestions
                    ? GetFeedbackDataForRun(run.RunId)
                    : null;
                var workspacePath = GetWorkspacePath(run.RunId);

                var result = await _dispatcher.TryDispatchAsync(
                    run, type, templateId, feedbackDataJson, workspacePath, ct);

                switch (result)
                {
                    case ConsolidationDispatchResult.Dispatched:
                        run.Status = ConsolidationRunStatus.Running;
                        await PersistRunAsync(run, ct);
                        ClearFeedbackDataForRun(run.RunId);
                        break;

                    case ConsolidationDispatchResult.Queued:
                        // Re-persist so QueuedRequiredLabels (set by dispatcher) is saved for restart rehydration
                        await PersistRunAsync(run, ct);
                        _logger.Information(
                            "Consolidation run {RunId} queued: waiting for idle agent for {Type}/{TemplateName}",
                            run.RunId, type, templateName);
                        ClearFeedbackDataForRun(run.RunId);
                        break;

                    case ConsolidationDispatchResult.Failed:
                        _logger.Warning(
                            "Consolidation run {RunId} dispatch failed for {Type}/{TemplateName}",
                            run.RunId, type, templateName);
                        _runningRuns.TryRemove(key, out _);
                        DeletePersistedRun(run.RunId);
                        ClearFeedbackDataForRun(run.RunId);
                        return null;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex,
                    "Consolidation run {RunId} dispatch failed with exception for {Type}/{TemplateName}",
                    run.RunId, type, templateName);

                _runningRuns.TryRemove(key, out _);
                DeletePersistedRun(run.RunId);
                ClearFeedbackDataForRun(run.RunId);
                return null;
            }
        }

        _logger.Information(
            "Consolidation run {RunId} created: {Type} for {TemplateName} (status={Status})",
            run.RunId, type, templateName, run.Status);

        OnChange?.Invoke();
        return run;
    }

    /// <inheritdoc />
    public async Task TransitionToRunningAsync(string runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);

        if (!Guid.TryParse(runId, out _))
            return;

        var filePath = Path.Combine(_consolidationRunsDirectory, $"{runId}.json");
        if (!File.Exists(filePath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            var run = JsonSerializer.Deserialize<ConsolidationRun>(json, s_jsonOptions);
            if (run is null || run.Status != ConsolidationRunStatus.Queued)
                return;

            run.Status = ConsolidationRunStatus.Running;
            run.QueuedRequiredLabels = null;
            await PersistRunAsync(run, ct);

            _logger.Information("Consolidation run {RunId} transitioned to Running", runId);
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to transition consolidation run {RunId} to Running", runId);
        }
    }

    /// <inheritdoc />
    public async Task CancelQueuedRunAsync(string runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);

        if (!Guid.TryParse(runId, out _))
            return;

        // Mark cancelled in queue service (handles cancel-during-dispatch race)
        _markCancelledCallback?.Invoke(runId);
        _removeFromQueueCallback?.Invoke(runId);

        var filePath = Path.Combine(_consolidationRunsDirectory, $"{runId}.json");
        if (!File.Exists(filePath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            var run = JsonSerializer.Deserialize<ConsolidationRun>(json, s_jsonOptions);
            if (run is null)
                return;

            // Only cancel if still queued
            if (run.Status != ConsolidationRunStatus.Queued)
                return;

            run.Status = ConsolidationRunStatus.Cancelled;
            run.CompletedAtUtc = DateTime.UtcNow;
            run.Summary = "Cancelled by user";
            run.QueuedRequiredLabels = null;
            await PersistRunAsync(run, ct);

            // Remove from concurrency guard
            var key = (run.Type, run.TemplateId);
            _runningRuns.TryRemove(key, out _);

            _logger.Information("Consolidation run {RunId} cancelled", runId);
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to cancel consolidation run {RunId}", runId);
        }
    }

    /// <inheritdoc />
    public Task<string?> GenerateFeedbackDataJsonAsync(DateTime sinceUtc, CancellationToken ct)
    {
        try
        {
            var allRuns = _runHistoryService.GetRunHistory();
            var feedbackEntries = allRuns
                .Where(r => r.Feedback is not null && r.StartedAt > sinceUtc)
                .Select(r => r.Feedback!)
                .ToList();

            if (feedbackEntries.Count == 0)
                return Task.FromResult<string?>(null);

            var feedbackJson = JsonSerializer.Serialize(feedbackEntries, s_jsonOptions);
            return Task.FromResult<string?>(feedbackJson);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to generate feedback data for harness suggestions");
            return Task.FromResult<string?>(null);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConsolidationRun>> GetRunHistoryAsync(CancellationToken ct)
    {
        var runs = new List<ConsolidationRun>();

        if (!Directory.Exists(_consolidationRunsDirectory))
            return runs;

        foreach (var file in Directory.GetFiles(_consolidationRunsDirectory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var run = JsonSerializer.Deserialize<ConsolidationRun>(json, s_jsonOptions);
                if (run is not null)
                    runs.Add(run);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to load consolidation run from {File}", file);
            }
        }

        return runs
            .OrderByDescending(r => r.StartedAtUtc)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ConsolidationRun?> GetLastRunAsync(
        ConsolidationRunType type,
        string? templateId,
        CancellationToken ct)
    {
        var allRuns = await GetRunHistoryAsync(ct);

        return allRuns
            .Where(r => r.Type == type && r.TemplateId == templateId)
            .OrderByDescending(r => r.StartedAtUtc)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task UpdateRunAsync(
        string runId,
        ConsolidationRunStatus status,
        string? summary,
        CancellationToken ct,
        long totalTokens = 0)
    {
        ArgumentNullException.ThrowIfNull(runId);

        if (!Guid.TryParse(runId, out _))
        {
            _logger.Warning("Invalid runId format: {RunId}", runId);
            return;
        }

        var filePath = Path.Combine(_consolidationRunsDirectory, $"{runId}.json");
        if (!File.Exists(filePath))
        {
            _logger.Warning("Cannot update consolidation run {RunId}: file not found", runId);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            var run = JsonSerializer.Deserialize<ConsolidationRun>(json, s_jsonOptions);
            if (run is null)
            {
                _logger.Warning("Cannot update consolidation run {RunId}: deserialization returned null", runId);
                return;
            }

            // Update mutable fields
            run.Status = status;
            run.Summary = summary;
            run.CompletedAtUtc = DateTime.UtcNow;
            run.TotalTokens = totalTokens;

            // Persist updated run
            await PersistRunAsync(run, ct);

            // Remove from running tracker if no longer running/queued
            if (status != ConsolidationRunStatus.Running && status != ConsolidationRunStatus.Queued)
            {
                var key = (run.Type, run.TemplateId);
                _runningRuns.TryRemove(key, out _);
            }

            // Clean up workspace on success, retain on failure
            CleanupWorkspaceIfSucceeded(runId, status);

            _logger.Information(
                "Consolidation run {RunId} updated: {Status} — {Summary}",
                runId, status, summary ?? "(no summary)");

            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update consolidation run {RunId}", runId);
        }
    }

    /// <inheritdoc />
    public async Task<HarnessSuggestions?> GetHarnessSuggestionsAsync(CancellationToken ct)
    {
        if (!File.Exists(_harnessSuggestionsPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_harnessSuggestionsPath, ct);
            return JsonSerializer.Deserialize<HarnessSuggestions>(json, s_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read harness suggestions from {Path}", _harnessSuggestionsPath);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveHarnessSuggestionsAsync(HarnessSuggestions suggestions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(suggestions);

        try
        {
            var directory = Path.GetDirectoryName(_harnessSuggestionsPath);
            if (directory is not null && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(suggestions, s_jsonOptions);
            await File.WriteAllTextAsync(_harnessSuggestionsPath, json, ct);

            _logger.Information("Harness suggestions saved to {Path}", _harnessSuggestionsPath);
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save harness suggestions to {Path}", _harnessSuggestionsPath);
        }
    }

    /// <summary>
    /// Prepares RunFeedback data from pipeline run history for harness suggestion analysis.
    /// </summary>
    private async Task PrepareFeedbackDataAsync(ConsolidationRun run, CancellationToken ct)
    {
        try
        {
            var sinceUtc = await GetLastSuccessfulHarnessRunTimestampAsync(ct);

            var allRuns = _runHistoryService.GetRunHistory();
            var feedbackEntries = allRuns
                .Where(r => r.Feedback is not null && r.StartedAt > sinceUtc)
                .Select(r => r.Feedback!)
                .ToList();

            if (feedbackEntries.Count == 0)
            {
                _logger.Information("No new RunFeedback entries found since {SinceUtc} for harness suggestions", sinceUtc);
                return;
            }

            var feedbackJson = JsonSerializer.Serialize(feedbackEntries, s_jsonOptions);
            _logger.Information(
                "Prepared {Count} RunFeedback entries (since {SinceUtc}) for harness suggestion analysis",
                feedbackEntries.Count, sinceUtc);

            _feedbackDataCache[run.RunId] = feedbackJson;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to prepare feedback data for harness suggestions");
        }
    }

    /// <summary>
    /// Determines the timestamp of the last successful harness suggestion run.
    /// </summary>
    internal async Task<DateTime> GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_consolidationRunsDirectory))
            return DateTime.MinValue;

        var latestCompletedUtc = DateTime.MinValue;

        foreach (var file in Directory.GetFiles(_consolidationRunsDirectory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var historicRun = JsonSerializer.Deserialize<ConsolidationRun>(json, s_jsonOptions);
                if (historicRun is not null
                    && historicRun.Type == ConsolidationRunType.HarnessSuggestions
                    && historicRun.Status == ConsolidationRunStatus.Succeeded
                    && historicRun.CompletedAtUtc.HasValue
                    && historicRun.CompletedAtUtc.Value > latestCompletedUtc)
                {
                    latestCompletedUtc = historicRun.CompletedAtUtc.Value;
                }
            }
            catch
            {
                // Skip malformed files
            }
        }

        return latestCompletedUtc;
    }

    /// <summary>
    /// Gets the cached feedback data JSON for a given run ID (used during dispatch).
    /// </summary>
    public string? GetFeedbackDataForRun(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        _feedbackDataCache.TryGetValue(runId, out var data);
        return data;
    }

    /// <summary>
    /// Removes cached feedback data after dispatch (cleanup).
    /// </summary>
    public void ClearFeedbackDataForRun(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        _feedbackDataCache.TryRemove(runId, out _);
    }

    private readonly ConcurrentDictionary<string, string> _feedbackDataCache = new();

    private async Task PersistRunAsync(ConsolidationRun run, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(_consolidationRunsDirectory))
                Directory.CreateDirectory(_consolidationRunsDirectory);

            var filePath = Path.Combine(_consolidationRunsDirectory, $"{run.RunId}.json");
            var json = JsonSerializer.Serialize(run, s_jsonOptions);
            await File.WriteAllTextAsync(filePath, json, ct);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to persist consolidation run {RunId}", run.RunId);
        }
    }

    /// <summary>
    /// Deletes a persisted run file (used when dispatch fails and the run must be rolled back).
    /// </summary>
    internal async Task DeletePersistedRunAsync(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);

        try
        {
            var filePath = Path.Combine(_consolidationRunsDirectory, $"{runId}.json");
            if (File.Exists(filePath))
            {
                await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Delete,
                    bufferSize: 1, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete persisted consolidation run {RunId}", runId);
        }
    }

    private void DeletePersistedRun(string runId)
    {
        try
        {
            var filePath = Path.Combine(_consolidationRunsDirectory, $"{runId}.json");
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete persisted consolidation run {RunId}", runId);
        }
    }

    // ── Workspace management ────────────────────────────────────────────

    /// <summary>
    /// Returns the workspace directory path for a consolidation run.
    /// </summary>
    public string GetWorkspacePath(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        if (!Guid.TryParse(runId, out _))
            throw new ArgumentException($"RunId must be a valid GUID, got: '{runId}'", nameof(runId));
        return Path.Combine(_config.WorkspaceBaseDirectory, "consolidation", runId);
    }

    /// <summary>
    /// Creates the workspace directory for a consolidation run.
    /// </summary>
    public string CreateWorkspace(string runId)
    {
        var workspacePath = GetWorkspacePath(runId);

        if (!Directory.Exists(workspacePath))
            Directory.CreateDirectory(workspacePath);

        _logger.Information("Created consolidation workspace at {Path}", workspacePath);
        return workspacePath;
    }

    private void CleanupWorkspaceIfSucceeded(string runId, ConsolidationRunStatus status)
    {
        if (status != ConsolidationRunStatus.Succeeded)
        {
            _logger.Debug(
                "Retaining consolidation workspace for failed run {RunId}", runId);
            return;
        }

        var workspacePath = GetWorkspacePath(runId);
        if (!Directory.Exists(workspacePath))
            return;

        try
        {
            Directory.Delete(workspacePath, recursive: true);
            _logger.Information(
                "Cleaned up consolidation workspace for successful run {RunId}", runId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "Failed to clean up consolidation workspace for run {RunId} at {Path}. " +
                "This is non-fatal and the workspace can be manually removed.",
                runId, workspacePath);
        }
    }
}
