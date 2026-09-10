namespace CodingAgent.Infrastructure.Persistence;

/// <summary>
/// Shared helper that guards recursive workspace deletion from path-traversal and symlink attacks.
/// Both <see cref="PipelineRunHistoryService"/> and
/// <see cref="Services.PostgresPipelineRunHistoryService"/> delegate their TryDeleteWorkspace
/// body to this single implementation.
/// </summary>
internal static class WorkspaceDeletionGuard
{
    /// <summary>
    /// Attempts to delete <paramref name="workspacePath"/> recursively.
    /// Returns silently (logs a warning) when:
    /// <list type="bullet">
    ///   <item><description>the path is null, empty, or does not exist;</description></item>
    ///   <item><description>the directory is a symlink;</description></item>
    ///   <item><description>the resolved path is not strictly inside <paramref name="workspaceBaseDirectory"/>;</description></item>
    ///   <item><description>the resolved path equals <paramref name="workspaceBaseDirectory"/> itself.</description></item>
    /// </list>
    /// Exceptions thrown by <see cref="Directory.Delete"/> are caught and logged rather than
    /// propagated, preserving the fire-and-forget semantics used by callers.
    /// </summary>
    /// <param name="workspacePath">The workspace directory to delete.</param>
    /// <param name="runId">The run identifier used in log messages.</param>
    /// <param name="workspaceBaseDirectory">
    ///   The root directory that all workspace paths must be contained within.
    /// </param>
    /// <param name="logger">Logger for warnings and informational messages.</param>
    public static void TryDelete(
        string? workspacePath,
        string runId,
        string workspaceBaseDirectory,
        Serilog.ILogger logger)
    {
        if (string.IsNullOrEmpty(workspacePath) || !Directory.Exists(workspacePath))
            return;

        var dirInfo = new DirectoryInfo(workspacePath);
        if (dirInfo.LinkTarget != null)
        {
            logger.Warning("Pipeline {RunId} workspace {Path} is a symlink, skipping cleanup",
                runId, workspacePath);
            return;
        }

        var fullPath = Path.GetFullPath(workspacePath);
        var fullBase = Path.GetFullPath(workspaceBaseDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullBase, StringComparison.Ordinal) ||
            fullPath.TrimEnd(Path.DirectorySeparatorChar) == fullBase.TrimEnd(Path.DirectorySeparatorChar))
        {
            logger.Warning("Pipeline {RunId} workspace path {Path} is not inside base {Base}, skipping cleanup",
                runId, workspacePath, workspaceBaseDirectory);
            return;
        }

        try
        {
            Directory.Delete(workspacePath, recursive: true);
            logger.Information("Pipeline {RunId} workspace deleted: {Path}", runId, workspacePath);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Pipeline {RunId} failed to delete workspace: {Path}", runId, workspacePath);
        }
    }
}
