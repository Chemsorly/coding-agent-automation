using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Manages pipeline run history: persistence, retrieval, and workspace cleanup.
/// Extracted from PipelineOrchestrationService to reduce file size.
/// </summary>
public class PipelineRunHistoryService
{
    private readonly List<PipelineRunSummary> _runHistory = new();
    private readonly string _runsDirectory;
    private readonly Serilog.ILogger _logger;

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public PipelineRunHistoryService(Serilog.ILogger logger, string runsDirectory = "config/pipeline/runs")
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _runsDirectory = runsDirectory;
        LoadRunHistory();
    }

    /// <summary>Returns the in-memory run history.</summary>
    public IReadOnlyList<PipelineRunSummary> GetRunHistory() => _runHistory.AsReadOnly();

    /// <summary>Adds a completed run to history and persists the summary to disk.</summary>
    // TODO: [ARC-10] Add ArgumentNullException.ThrowIfNull for public method parameters
    public void AddRunToHistory(PipelineRun run)
    {
        var summary = run.ToSummary();
        _runHistory.Insert(0, summary);
        PersistRunSummary(summary);
    }

    private void PersistRunSummary(PipelineRunSummary summary)
    {
        try
        {
            if (!Directory.Exists(_runsDirectory))
                Directory.CreateDirectory(_runsDirectory);

            var path = Path.Combine(_runsDirectory, $"{summary.RunId}.json");
            var json = System.Text.Json.JsonSerializer.Serialize(summary, _jsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist run summary {RunId}", summary.RunId);
        }
    }

    private void LoadRunHistory()
    {
        try
        {
            if (!Directory.Exists(_runsDirectory))
                return;

            var summaries = new List<PipelineRunSummary>();
            foreach (var file in Directory.GetFiles(_runsDirectory, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var summary = System.Text.Json.JsonSerializer.Deserialize<PipelineRunSummary>(json, _jsonOptions);
                    if (summary != null)
                        summaries.Add(summary);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to load run summary from {File}", file);
                }
            }

            _runHistory.AddRange(summaries.OrderByDescending(s => s.StartedAt));
            _logger.Information("Loaded {Count} pipeline run(s) from history", _runHistory.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load run history");
        }
    }

    /// <summary>
    /// Attempts to delete a workspace directory. Logs but does not throw on failure.
    /// Validates the path is a subdirectory of the workspace base and not a symlink.
    /// </summary>
    public void TryDeleteWorkspace(string? workspacePath, string runId, string workspaceBaseDirectory)
    {
        if (string.IsNullOrEmpty(workspacePath) || !Directory.Exists(workspacePath))
            return;

        var dirInfo = new DirectoryInfo(workspacePath);
        if (dirInfo.LinkTarget != null)
        {
            _logger.Warning("Pipeline {RunId} workspace {Path} is a symlink, skipping cleanup",
                runId, workspacePath);
            return;
        }

        var fullPath = Path.GetFullPath(workspacePath);
        var fullBase = Path.GetFullPath(workspaceBaseDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullBase, StringComparison.Ordinal) || fullPath.TrimEnd(Path.DirectorySeparatorChar) == fullBase.TrimEnd(Path.DirectorySeparatorChar))
        {
            _logger.Warning("Pipeline {RunId} workspace path {Path} is not inside base {Base}, skipping cleanup",
                runId, workspacePath, workspaceBaseDirectory);
            return;
        }

        try
        {
            Directory.Delete(workspacePath, recursive: true);
            _logger.Information("Pipeline {RunId} workspace deleted: {Path}", runId, workspacePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} failed to delete workspace: {Path}", runId, workspacePath);
        }
    }

    /// <summary>
    /// Cleans up expired workspace folders for failed/cancelled runs based on retention policy.
    /// </summary>
    // TODO: [ARC-10] Add ArgumentNullException.ThrowIfNull for public method parameters
    public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null)
    {
        if (config.FailedWorkspaceRetentionDays < 0)
            return;

        var cutoff = DateTime.UtcNow.AddDays(-config.FailedWorkspaceRetentionDays);

        foreach (var summary in _runHistory)
        {
            if (summary.FinalStep == PipelineStep.Completed)
                continue;

            if (summary.CompletedAt == null || summary.CompletedAt > cutoff)
                continue;

            if (activeRunId != null && activeRunId == summary.RunId)
                continue;

            var workspacePath = Path.Combine(config.WorkspaceBaseDirectory, summary.RunId);
            TryDeleteWorkspace(workspacePath, summary.RunId, config.WorkspaceBaseDirectory);
        }
    }
}
