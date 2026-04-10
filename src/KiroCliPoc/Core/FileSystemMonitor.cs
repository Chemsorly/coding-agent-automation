using KiroCliPoc.Models;

namespace KiroCliPoc.Core;

/// <summary>
/// Monitors file system changes in a workspace directory.
/// Provides snapshot comparison to detect created, modified, and deleted files.
/// </summary>
/// <remarks>
/// Rule IDs: DOTNET_CONVENTIONS, DOTNET_PRINCIPLES, SECURITY_INPUT_VALIDATION
/// </remarks>
public class FileSystemMonitor
{
    /// <summary>
    /// Scans the workspace directory and captures a snapshot of all files with their metadata.
    /// </summary>
    /// <param name="workspaceDirectory">The directory to scan.</param>
    /// <returns>A list of file snapshots with paths and last modified times.</returns>
    /// <exception cref="ArgumentNullException">Thrown when workspaceDirectory is null.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the directory does not exist.</exception>
    public IReadOnlyList<FileSnapshot> ScanWorkspace(string workspaceDirectory)
    {
        ArgumentNullException.ThrowIfNull(workspaceDirectory);

        if (!Directory.Exists(workspaceDirectory))
        {
            throw new DirectoryNotFoundException($"Workspace directory not found: {workspaceDirectory}");
        }

        var snapshots = new List<FileSnapshot>();

        try
        {
            var files = Directory.GetFiles(workspaceDirectory, "*", SearchOption.AllDirectories);

            foreach (var filePath in files)
            {
                var fileInfo = new FileInfo(filePath);
                snapshots.Add(new FileSnapshot
                {
                    Path = filePath,
                    LastModified = fileInfo.LastWriteTimeUtc
                });
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // Log and skip inaccessible directories
            throw new InvalidOperationException($"Access denied while scanning workspace: {workspaceDirectory}", ex);
        }

        return snapshots;
    }

    /// <summary>
    /// Compares two workspace snapshots and identifies all file changes.
    /// </summary>
    /// <param name="before">The snapshot taken before execution.</param>
    /// <param name="after">The snapshot taken after execution.</param>
    /// <returns>A list of detected file changes (created, modified, deleted).</returns>
    /// <exception cref="ArgumentNullException">Thrown when before or after is null.</exception>
    public IReadOnlyList<FileChange> CompareSnapshots(
        IReadOnlyList<FileSnapshot> before,
        IReadOnlyList<FileSnapshot> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<FileChange>();
        var beforeDict = before.ToDictionary(f => f.Path, f => f.LastModified);
        var afterDict = after.ToDictionary(f => f.Path, f => f.LastModified);

        // Detect created and modified files
        foreach (var afterFile in after)
        {
            if (!beforeDict.TryGetValue(afterFile.Path, out var beforeTime))
            {
                // File exists in 'after' but not in 'before' → Created
                changes.Add(new FileChange
                {
                    Path = afterFile.Path,
                    Type = FileChangeType.Created,
                    Timestamp = afterFile.LastModified
                });
            }
            else if (afterFile.LastModified > beforeTime)
            {
                // File exists in both, but modified time changed → Modified
                changes.Add(new FileChange
                {
                    Path = afterFile.Path,
                    Type = FileChangeType.Modified,
                    Timestamp = afterFile.LastModified
                });
            }
        }

        // Detect deleted files
        foreach (var beforeFile in before)
        {
            if (!afterDict.ContainsKey(beforeFile.Path))
            {
                // File exists in 'before' but not in 'after' → Deleted
                changes.Add(new FileChange
                {
                    Path = beforeFile.Path,
                    Type = FileChangeType.Deleted,
                    Timestamp = DateTime.UtcNow
                });
            }
        }

        return changes;
    }
}

/// <summary>
/// Represents a snapshot of a file at a specific point in time.
/// Used internally by FileSystemMonitor for comparison.
/// </summary>
public class FileSnapshot
{
    public required string Path { get; init; }
    public required DateTime LastModified { get; init; }
}
