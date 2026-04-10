namespace KiroCliPoc.Models;

/// <summary>
/// Represents a file system change detected during Kiro CLI execution.
/// </summary>
public class FileChange
{
    /// <summary>
    /// Gets or initializes the file path.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets or initializes the type of change (Created, Modified, Deleted).
    /// </summary>
    public required FileChangeType Type { get; init; }

    /// <summary>
    /// Gets or initializes the timestamp when the change was detected.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Specifies the type of file system change.
/// </summary>
public enum FileChangeType
{
    /// <summary>
    /// A new file was created.
    /// </summary>
    Created,

    /// <summary>
    /// An existing file was modified.
    /// </summary>
    Modified,

    /// <summary>
    /// A file was deleted.
    /// </summary>
    Deleted
}
