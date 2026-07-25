using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Manages consolidation workspace directories: path computation, creation, and cleanup.
/// Consolidation workspaces are isolated from regular pipeline workspaces
/// under <c>{WorkspaceBaseDirectory}/consolidation/{runId}/</c>.
/// </summary>
public sealed class ConsolidationWorkspaceManager : IConsolidationWorkspaceManager
{
    private readonly ILogger _logger;
    private readonly PipelineConfiguration _config;

    public ConsolidationWorkspaceManager(ILogger logger, PipelineConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(config);

        _logger = logger;
        _config = config;
    }

    /// <inheritdoc />
    public string GetWorkspacePath(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        if (!Guid.TryParse(runId, out _))
            throw new ArgumentException($"RunId must be a valid GUID, got: '{runId}'", nameof(runId));
        return Path.Combine(_config.WorkspaceBaseDirectory, "consolidation", runId);
    }

    /// <inheritdoc />
    public string CreateWorkspace(string runId)
    {
        var workspacePath = GetWorkspacePath(runId);

        if (!Directory.Exists(workspacePath))
            Directory.CreateDirectory(workspacePath);

        _logger.Information("Created consolidation workspace at {Path}", workspacePath);
        return workspacePath;
    }

    /// <inheritdoc />
    public void CleanupWorkspaceIfSucceeded(string runId, ConsolidationRunStatus status)
    {
        if (status != ConsolidationRunStatus.Succeeded)
        {
            _logger.Debug("Retaining consolidation workspace for failed run {RunId}", runId);
            return;
        }

        var workspacePath = GetWorkspacePath(runId);
        if (!Directory.Exists(workspacePath))
            return;

        try
        {
            Directory.Delete(workspacePath, recursive: true);
            _logger.Information("Cleaned up consolidation workspace for successful run {RunId}", runId);
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
