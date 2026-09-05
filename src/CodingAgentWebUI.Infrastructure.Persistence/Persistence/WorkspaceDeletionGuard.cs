namespace CodingAgentWebUI.Infrastructure.Persistence;

/// <summary>
/// Shared guard for safely deleting a pipeline run's workspace directory.
/// Prevents recursive deletion from escaping the workspace base (path traversal)
/// and refuses to delete symlinks. Logs but never throws on failure.
/// </summary>
internal static class WorkspaceDeletionGuard
{
    /// <summary>
    /// Attempts to delete <paramref name="workspacePath"/>. No-ops if the path is null/empty,
    /// does not exist, is a symlink, or is not a strict subdirectory of
    /// <paramref name="workspaceBaseDirectory"/>. Logs a warning on skip; logs information
    /// on success; logs a warning (with exception) on delete failure without rethrowing.
    /// </summary>
    internal static void TryDelete(
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

        // TODO: workspaceBaseDirectory is not guarded for null/empty before being passed to
        // Path.GetFullPath. If a caller passes null, Path.GetFullPath throws ArgumentNullException
        // which is NOT caught by the try/catch below (that only wraps Directory.Delete), breaking
        // the documented "logs but never throws" contract. Consider returning-and-logging or using
        // ArgumentException.ThrowIfNullOrEmpty depending on whether null should be a caller error
        // or a silent no-op. [review-findings.md WARNING]
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
