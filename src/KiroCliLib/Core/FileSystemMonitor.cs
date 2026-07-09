using KiroCliLib.Models;

namespace KiroCliLib.Core;

/// <summary>
/// Monitors file system changes via before/after snapshot comparison.
/// </summary>
public class FileSystemMonitor : IFileSystemMonitor
{
    public IReadOnlyList<FileSnapshot> ScanWorkspace(string workspaceDirectory)
    {
        ArgumentNullException.ThrowIfNull(workspaceDirectory);
        if (!Directory.Exists(workspaceDirectory))
        {
            Serilog.Log.Error("Workspace directory not found: {WorkspaceDirectory}", workspaceDirectory);
            throw new DirectoryNotFoundException($"Workspace directory not found: {workspaceDirectory}");
        }

        var snapshots = new List<FileSnapshot>();
        try
        {
            foreach (var filePath in Directory.GetFiles(workspaceDirectory, "*", SearchOption.AllDirectories))
            {
                var fileInfo = new FileInfo(filePath);
                snapshots.Add(new FileSnapshot { Path = filePath, LastModified = fileInfo.LastWriteTimeUtc });
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Serilog.Log.Error(ex, "Access denied while scanning workspace: {WorkspaceDirectory}", workspaceDirectory);
            throw new InvalidOperationException($"Access denied while scanning workspace: {workspaceDirectory}", ex);
        }
        return snapshots;
    }

    public IReadOnlyList<FileChange> CompareSnapshots(IReadOnlyList<FileSnapshot> before, IReadOnlyList<FileSnapshot> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<FileChange>();
        var beforeDict = before.ToDictionary(f => f.Path, f => f.LastModified);
        var afterDict = after.ToDictionary(f => f.Path, f => f.LastModified);

        foreach (var afterFile in after)
        {
            if (!beforeDict.TryGetValue(afterFile.Path, out var beforeTime))
                changes.Add(new FileChange { Path = afterFile.Path, Type = FileChangeType.Created });
            else if (afterFile.LastModified > beforeTime)
                changes.Add(new FileChange { Path = afterFile.Path, Type = FileChangeType.Modified });
        }

        foreach (var beforeFile in before)
        {
            if (!afterDict.ContainsKey(beforeFile.Path))
                changes.Add(new FileChange { Path = beforeFile.Path, Type = FileChangeType.Deleted });
        }

        return changes;
    }
}

public class FileSnapshot
{
    public required string Path { get; init; }
    public required DateTime LastModified { get; init; }
}
