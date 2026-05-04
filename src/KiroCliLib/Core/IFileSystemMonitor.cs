using KiroCliLib.Models;

namespace KiroCliLib.Core;

/// <summary>
/// Defines the contract for monitoring file system changes via before/after snapshot comparison.
/// </summary>
public interface IFileSystemMonitor
{
    /// <summary>
    /// Scans the workspace directory and returns a snapshot of all files with their timestamps.
    /// </summary>
    /// <param name="workspaceDirectory">The directory to scan.</param>
    /// <returns>A list of file snapshots representing the current state.</returns>
    IReadOnlyList<FileSnapshot> ScanWorkspace(string workspaceDirectory);

    /// <summary>
    /// Compares two snapshots to detect file changes (created, modified, deleted).
    /// </summary>
    /// <param name="before">The snapshot taken before the operation.</param>
    /// <param name="after">The snapshot taken after the operation.</param>
    /// <returns>A list of detected file changes.</returns>
    IReadOnlyList<FileChange> CompareSnapshots(IReadOnlyList<FileSnapshot> before, IReadOnlyList<FileSnapshot> after);
}
