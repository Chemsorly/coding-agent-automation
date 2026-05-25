namespace CodingAgentWebUI.Infrastructure.Git;

/// <summary>
/// Abstracts local git repository operations (stage, commit, status, diff, reset)
/// to decouple BrainUpdateService from LibGit2Sharp native binaries.
/// Enables unit testing without requiring platform-specific native libraries.
/// </summary>
public interface IGitOperations
{
    /// <summary>
    /// Returns the list of changed file paths (relative) in the working directory.
    /// Excludes ignored and unaltered files.
    /// </summary>
    IReadOnlyList<string> GetChangedFiles(string repoPath);

    /// <summary>
    /// Returns true if the repository index has unresolved conflicts.
    /// </summary>
    bool HasConflicts(string repoPath);

    /// <summary>
    /// Returns the list of conflicting file entries with their ours/theirs content.
    /// </summary>
    IReadOnlyList<ConflictEntry> GetConflicts(string repoPath);

    /// <summary>
    /// Stages all changes and commits with the given message.
    /// Throws <see cref="EmptyCommitException"/> if there are no changes to commit.
    /// </summary>
    void StageAllAndCommit(string repoPath, string message);

    /// <summary>
    /// Stages a specific file path.
    /// </summary>
    void StageFile(string repoPath, string relativePath);

    /// <summary>
    /// Returns the number of files changed in the HEAD commit compared to its parent.
    /// Returns 0 if HEAD has no parent.
    /// </summary>
    int GetHeadCommitFileCount(string repoPath);

    /// <summary>
    /// Returns the list of file changes between the HEAD commit and its parent.
    /// Used during rebase to determine which files "our" commit modified.
    /// </summary>
    IReadOnlyList<FileChange> GetHeadCommitChanges(string repoPath);

    /// <summary>
    /// Gets the content of a file from the HEAD commit's tree.
    /// Returns null if the file doesn't exist in the tree.
    /// </summary>
    string? GetFileContentFromHead(string repoPath, string relativePath);

    /// <summary>
    /// Gets the content of a file from the HEAD commit's parent tree.
    /// Returns null if the file doesn't exist in the parent tree or HEAD has no parent.
    /// </summary>
    string? GetFileContentFromHeadParent(string repoPath, string relativePath);

    /// <summary>
    /// Resets the repository to the tip of the specified remote tracking branch (hard reset).
    /// </summary>
    void ResetHardToRemote(string repoPath, string remoteBranch);

    /// <summary>
    /// Returns true if the specified remote tracking branch exists.
    /// </summary>
    bool RemoteBranchExists(string repoPath, string remoteBranch);

    /// <summary>
    /// Returns true if the file exists at the given absolute path.
    /// </summary>
    bool FileExists(string fullPath);

    /// <summary>
    /// Reads all text from the file at the given absolute path.
    /// </summary>
    string ReadAllText(string fullPath);

    /// <summary>
    /// Writes text to the file at the given absolute path, creating directories as needed.
    /// </summary>
    void WriteAllText(string fullPath, string content);

    /// <summary>
    /// Deletes the file at the given absolute path if it exists.
    /// </summary>
    void DeleteFile(string fullPath);
}

/// <summary>
/// Represents a merge conflict entry with content from both sides.
/// </summary>
public sealed record ConflictEntry(string? FilePath, string OursContent, string TheirsContent);

/// <summary>
/// Represents a file change in a commit diff.
/// </summary>
public sealed record FileChange(string Path, FileChangeStatus Status);

/// <summary>
/// Status of a file change in a diff.
/// </summary>
public enum FileChangeStatus
{
    Added,
    Modified,
    Deleted,
    Renamed
}

/// <summary>
/// Thrown when a commit would be empty (no staged changes).
/// Mirrors LibGit2Sharp.EmptyCommitException for abstraction purposes.
/// </summary>
public class EmptyCommitException : Exception
{
    public EmptyCommitException() : base("Nothing to commit — working tree clean.") { }
    public EmptyCommitException(string message) : base(message) { }
    public EmptyCommitException(string message, Exception inner) : base(message, inner) { }
}
