using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Persistence;

namespace CodingAgentWebUI.Infrastructure.Persistence;

/// <summary>
/// Manages pipeline run history: persistence, retrieval, and workspace cleanup.
/// Extracted from PipelineOrchestrationService to reduce file size.
/// </summary>
public class PipelineRunHistoryService : IPipelineRunHistoryService
{
    private readonly List<PipelineRunSummary> _runHistory = [];
    private readonly Lock _lock = new();
    private readonly string _runsDirectory;
    private readonly Serilog.ILogger _logger;

    /// <summary>Maximum number of run summaries kept in memory. Older entries remain on disk.</summary>
    internal const int MaxHistorySize = 1000;

    private static System.Text.Json.JsonSerializerOptions JsonOptions => PipelineJsonOptions.Default;

    public PipelineRunHistoryService(Serilog.ILogger logger, string runsDirectory = PipelineConstants.RunsDirectory)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _runsDirectory = runsDirectory;
        LoadRunHistory();
    }

    /// <summary>Returns the in-memory run history.</summary>
    public IReadOnlyList<PipelineRunSummary> GetRunHistory()
    {
        lock (_lock) { return _runHistory.ToList().AsReadOnly(); }
    }

    /// <summary>Returns the most recent runs for a specific agent, limited to <paramref name="limit"/>.</summary>
    public IReadOnlyList<PipelineRunSummary> GetRunsByAgentId(string agentId, int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        lock (_lock)
        {
            return _runHistory
                .Where(r => string.Equals(r.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToList()
                .AsReadOnly();
        }
    }

    /// <summary>Adds a completed run to history and persists the summary to disk.</summary>
    public void AddRunToHistory(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Defense-in-depth: reject consolidation runs from being persisted to pipeline history.
        // Consolidation has its own history on the Consolidation page.
        if (run.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId)
        {
            _logger.Debug("AddRunToHistory: skipping consolidation run {RunId}", run.RunId);
            return;
        }

        var summary = run.ToSummary();
        lock (_lock)
        {
            _runHistory.Insert(0, summary);
            if (_runHistory.Count > MaxHistorySize)
                _runHistory.RemoveAt(_runHistory.Count - 1);
        }
        // Fire-and-forget: existing behavior is non-blocking persist
        _ = PersistRunSummaryAsync(summary);
    }

    private async Task PersistRunSummaryAsync(PipelineRunSummary summary)
    {
        try
        {
            var path = Path.Combine(_runsDirectory, $"{summary.RunId}.json");
            var json = System.Text.Json.JsonSerializer.Serialize(summary, JsonOptions);
            await AtomicFileWriter.WriteAsync(path, json, CancellationToken.None);
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

            // TODO: GetFiles materializes full FileInfo[] before LINQ filtering. For directories with thousands of files this is an unbounded allocation at startup. Consider EnumerateFiles or paginated loading.
            var files = new DirectoryInfo(_runsDirectory).GetFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxHistorySize);

            var summaries = new List<PipelineRunSummary>();
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file.FullName);
                    var summary = System.Text.Json.JsonSerializer.Deserialize<PipelineRunSummary>(json, JsonOptions);
                    if (summary != null && summary.InitiatedBy != ConsolidationConstants.InitiatedBy)
                        summaries.Add(summary);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to load run summary from {File}", file.FullName);
                }
            }

            #pragma warning disable CS0618 // Fallback to legacy StartedAt for older persisted summaries without StartedAtOffset
            _runHistory.AddRange(summaries.OrderByDescending(s =>
                s.StartedAtOffset != default ? s.StartedAtOffset : new DateTimeOffset(s.StartedAt, TimeSpan.Zero)));
            #pragma warning restore CS0618
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
    public void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.FailedWorkspaceRetentionDays < 0)
            return;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-config.FailedWorkspaceRetentionDays);

        List<PipelineRunSummary> snapshot;
        lock (_lock) { snapshot = _runHistory.ToList(); }

        foreach (var summary in snapshot)
        {
            if (summary.FinalStep == PipelineStep.Completed)
                continue;

            #pragma warning disable CS0618 // Fallback to legacy CompletedAt for older persisted summaries without CompletedAtOffset
            var completedOffset = summary.CompletedAtOffset
                ?? (summary.CompletedAt.HasValue ? new DateTimeOffset(summary.CompletedAt.Value, TimeSpan.Zero) : (DateTimeOffset?)null);
            #pragma warning restore CS0618

            if (completedOffset == null || completedOffset > cutoff)
                continue;

            if (activeRunId != null && activeRunId == summary.RunId)
                continue;

            var workspacePath = Path.Combine(config.WorkspaceBaseDirectory, summary.RunId);
            TryDeleteWorkspace(workspacePath, summary.RunId, config.WorkspaceBaseDirectory);
        }
    }
}
