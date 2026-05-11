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
    private readonly IConsolidationDispatcher? _dispatcher;
    private readonly string _consolidationRunsDirectory;
    private readonly string _harnessSuggestionsPath;

    /// <summary>
    /// Tracks currently running consolidation runs by (type, templateId) to enforce concurrency guard.
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
        string consolidationRunsDirectory = "config/pipeline/consolidation-runs",
        string harnessSuggestionsPath = "config/pipeline/harness-suggestions.json")
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

    /// <inheritdoc />
    public async Task<ConsolidationRun?> TriggerAsync(
        ConsolidationRunType type,
        string? templateId,
        CancellationToken ct)
    {
        // WARNING 4 fix: Use TryAdd as the sole concurrency guard (eliminates TOCTOU race)
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
            Status = ConsolidationRunStatus.Running
        };

        // Register in concurrency tracker — TryAdd is the sole guard (no separate ContainsKey check)
        if (!_runningRuns.TryAdd(key, run))
        {
            _logger.Warning(
                "Consolidation run rejected: {Type} for template {TemplateId} is already running",
                type, templateId ?? "Global");
            return null;
        }

        // For harness suggestions: prepare feedback data (filtered by last successful run)
        if (type == ConsolidationRunType.HarnessSuggestions)
        {
            await PrepareFeedbackDataAsync(run, ct);
        }

        // Persist the run record
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

                var dispatched = await _dispatcher.TryDispatchAsync(
                    run, type, templateId, feedbackDataJson, workspacePath, ct);

                if (!dispatched)
                {
                    _logger.Warning(
                        "Consolidation run {RunId} rejected: no idle agent available for {Type}/{TemplateName}",
                        run.RunId, type, templateName);

                    _runningRuns.TryRemove(key, out _);
                    DeletePersistedRun(run.RunId);
                    ClearFeedbackDataForRun(run.RunId);
                    return null;
                }

                // Clear feedback data cache after successful dispatch
                ClearFeedbackDataForRun(run.RunId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex,
                    "Consolidation run {RunId} dispatch failed with exception for {Type}/{TemplateName}",
                    run.RunId, type, templateName);

                // Clean up concurrency state and persisted run to prevent permanent blocking
                _runningRuns.TryRemove(key, out _);
                DeletePersistedRun(run.RunId);
                ClearFeedbackDataForRun(run.RunId);
                return null;
            }
        }

        _logger.Information(
            "Consolidation run {RunId} created: {Type} for {TemplateName}",
            run.RunId, type, templateName);

        // Fire OnChange event after state mutation
        OnChange?.Invoke();

        return run;
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
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);

        // WARNING 5 fix: Validate runId is a valid GUID to prevent path traversal
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

            // Persist updated run
            await PersistRunAsync(run, ct);

            // Remove from running tracker if no longer running
            if (status != ConsolidationRunStatus.Running)
            {
                var key = (run.Type, run.TemplateId);
                _runningRuns.TryRemove(key, out _);
            }

            // Clean up workspace on success, retain on failure
            CleanupWorkspaceIfSucceeded(runId, status);

            _logger.Information(
                "Consolidation run {RunId} updated: {Status} — {Summary}",
                runId, status, summary ?? "(no summary)");

            // Fire OnChange event after state mutation
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

            // Fire OnChange event after state mutation
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save harness suggestions to {Path}", _harnessSuggestionsPath);
        }
    }

    /// <summary>
    /// Prepares RunFeedback data from pipeline run history for harness suggestion analysis.
    /// Filters to only feedback collected since the last successful harness suggestion run.
    /// The feedback data is stored on the run record for later use during agent dispatch.
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

            // Store feedback data — will be used when building ConsolidationJobMessage for dispatch
            _feedbackDataCache[run.RunId] = feedbackJson;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to prepare feedback data for harness suggestions");
        }
    }

    /// <summary>
    /// Determines the timestamp of the last successful harness suggestion run by scanning
    /// persisted run files. Returns <see cref="DateTime.MinValue"/> if no prior run exists.
    /// </summary>
    private async Task<DateTime> GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken ct = default)
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
    private async Task DeletePersistedRunAsync(string runId)
    {
        try
        {
            var filePath = Path.Combine(_consolidationRunsDirectory, $"{runId}.json");
            if (File.Exists(filePath))
            {
                // Use async file deletion pattern: open with DeleteOnClose
                await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Delete,
                    bufferSize: 1, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete persisted consolidation run {RunId}", runId);
        }
    }

    /// <summary>
    /// Deletes a persisted run file synchronously (used in fire-and-forget cleanup paths).
    /// </summary>
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
    /// Consolidation workspaces are isolated from regular pipeline workspaces
    /// under <c>{WorkspaceBaseDirectory}/consolidation/{runId}/</c>.
    /// </summary>
    /// <param name="runId">The consolidation run ID (must be a valid GUID).</param>
    /// <returns>The absolute path to the consolidation workspace directory.</returns>
    /// <exception cref="ArgumentException">Thrown when runId is not a valid GUID format.</exception>
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
    /// <param name="runId">The consolidation run ID (must be a valid GUID).</param>
    /// <returns>The absolute path to the created workspace directory.</returns>
    /// <exception cref="ArgumentException">Thrown when runId is not a valid GUID format.</exception>
    public string CreateWorkspace(string runId)
    {
        var workspacePath = GetWorkspacePath(runId); // validates GUID

        if (!Directory.Exists(workspacePath))
            Directory.CreateDirectory(workspacePath);

        _logger.Information("Created consolidation workspace at {Path}", workspacePath);
        return workspacePath;
    }

    /// <summary>
    /// Cleans up the workspace directory after a successful run.
    /// Retains the workspace for failed runs to allow debugging.
    /// Cleanup failure is non-fatal (logged as warning).
    /// </summary>
    /// <param name="runId">The consolidation run ID.</param>
    /// <param name="status">The final status of the run.</param>
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
