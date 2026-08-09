using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Manages consolidation workspace directories: path computation, creation, and cleanup.
/// Consolidation workspaces are isolated from regular pipeline workspaces
/// under <c>{WorkspaceBaseDirectory}/consolidation/{runId}/</c>.
/// </summary>
public interface IConsolidationWorkspaceManager
{
    /// <summary>
    /// Returns the workspace directory path for a consolidation run.
    /// </summary>
    /// <param name="runId">A valid GUID identifying the run.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="runId"/> is not a valid GUID.</exception>
    string GetWorkspacePath(RunId runId);

    /// <summary>
    /// Creates the workspace directory for a consolidation run on disk.
    /// Idempotent — returns the path if the directory already exists.
    /// </summary>
    /// <param name="runId">A valid GUID identifying the run.</param>
    string CreateWorkspace(RunId runId);

    /// <summary>
    /// Cleans up the workspace directory after a successful run.
    /// Retains the workspace for failed runs to allow debugging.
    /// Cleanup failures are non-fatal (logged as warning).
    /// </summary>
    /// <param name="runId">A valid GUID identifying the run.</param>
    /// <param name="status">The terminal status of the run.</param>
    void CleanupWorkspaceIfSucceeded(RunId runId, ConsolidationRunStatus status);
}
