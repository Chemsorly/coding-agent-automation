namespace KiroCliLib.Models;

/// <summary>
/// Represents a file system change detected during Kiro CLI execution.
/// </summary>
public class FileChange
{
    public required string Path { get; init; }
    public required FileChangeType Type { get; init; }
}

/// <summary>
/// Specifies the type of file system change.
/// </summary>
public enum FileChangeType
{
    Created,
    Modified,
    Deleted
}
