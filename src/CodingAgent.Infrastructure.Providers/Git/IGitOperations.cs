using CodingAgent.Pipeline.Models;

namespace CodingAgent.Infrastructure.Git;

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

    /// <summary>Returns true if the repository index has unresolved conflicts.</summary>
    bool HasConflicts(string repoPath);

    /// <summary>Returns the list of conflicting file entries with their ours/theirs content.</summary>
    IReadOnlyList<ConflictEntry> GetConflicts(string repoPath);

    /// <summary>
    /// Stages all changes and commits with the given message.
    /// Throws <see cref="EmptyCommitException"/> if there are no changes to commit.
    /// </summary>
    void StageAllAndCommit(string repoPath, string message);

    /// <summary>Stages a specific file path.</summary>
    void StageFile(string repoPath, string relativePath);

    /// <summary>Returns the number of files changed in the HEAD commit compared to its parent.</summary>
    int GetHeadCommitFileCount(string repoPath);

    /// <summary>Returns the list of file changes between the HEAD commit and its parent.</summary>
    IReadOnlyList<FileChange> GetHeadCommitChanges(string repoPath);

    /// <summary>Gets the content of a file from the HEAD commit's tree. Returns null if absent.</summary>
    string? GetFileContentFromHead(string repoPath, string relativePath);

    /// <summary>Gets the content of a file from the HEAD commit's parent tree. Returns null if absent.</summary>
    string? GetFileContentFromHeadParent(string repoPath, string relativePath);

    /// <summary>Resets the repository to the tip of the specified remote tracking branch (hard reset).</summary>
    void ResetHardToRemote(string repoPath, string remoteBranch);

    /// <summary>Returns true if the specified remote tracking branch exists.</summary>
    bool RemoteBranchExists(string repoPath, string remoteBranch);

    bool FileExists(string fullPath);
    string ReadAllText(string fullPath);
    void WriteAllText(string fullPath, string content);
    /// <summary>Writes all text to a file asynchronously with the given encoding.</summary>
    Task WriteAllTextAsync(string fullPath, string content, System.Text.Encoding encoding, CancellationToken ct);
    void DeleteFile(string fullPath);

    // ── Inbox push operations (spec 054 Task 6.1) ─────────────────────────

    /// <summary>
    /// Checks out HEAD for every modified/deleted tracked file, deletes untracked files and then-empty
    /// folders, EXCEPT <paramref name="keepRelativePath"/>. Returns the discarded paths.
    /// </summary>
    IReadOnlyList<string> DiscardChangesExcept(string repoPath, string keepRelativePath);

    /// <summary>
    /// Stages each path (a missing file stages its deletion) and commits.
    /// Throws <see cref="EmptyCommitException"/> when nothing is staged.
    /// </summary>
    void StageAndCommit(string repoPath, IReadOnlyList<string> relativePaths, string message);

    /// <summary>
    /// Returns commits reachable from HEAD and not from <c>origin/<paramref name="remoteBranch"/></c>,
    /// oldest first, with file contents at each commit and its parent.
    /// </summary>
    IReadOnlyList<LocalCommit> GetCommitsAheadOfRemote(string repoPath, string remoteBranch);

    /// <summary>Returns the content of the file at the tip of <c>origin/<paramref name="remoteBranch"/></c>, or null.</summary>
    string? GetFileContentAtRemote(string repoPath, string remoteBranch, string relativePath);

    // ── Review git operations (spec 054 Task 7A.1) ─────────────────────────

    /// <summary>Returns all paths tracked in the index (/ separators).</summary>
    IReadOnlyList<string> ListTrackedFiles(string repoPath);

    /// <summary>Returns the content of the file at the given commit SHA, or null when absent. Short SHAs accepted.</summary>
    string? ReadFileAtCommit(string repoPath, string commitSha, string relativePath);

    /// <summary>Returns the content of the file at HEAD, or null when absent.</summary>
    string? ReadFileAtHead(string repoPath, string relativePath);

    /// <summary>Returns all working-tree changes against HEAD, including untracked files.</summary>
    IReadOnlyList<BrainPathChange> GetWorkingTreeChanges(string repoPath);

    /// <summary>
    /// Stages skill directories and README, then returns the patch of HEAD vs the index.
    /// </summary>
    string GetReviewPatch(string repoPath);

    /// <summary>
    /// Returns commits newer than the newest `brain: review run` commit that touch skill directories,
    /// whose messages do not start with `brain: review run` or `brain: archive v1 content`. Oldest first, capped at 50.
    /// </summary>
    IReadOnlyList<BrainCommitInfo> GetSkillCommitsSinceLastReview(string repoPath);
}

/// <summary>Represents a merge conflict entry with content from both sides.</summary>
public sealed record ConflictEntry(string? FilePath, string OursContent, string TheirsContent);

/// <summary>Represents a file change in a commit diff.</summary>
public sealed record FileChange(string Path, FileChangeStatus Status);

/// <summary>Status of a file change in a diff.</summary>
public enum FileChangeStatus
{
    Added,
    Modified,
    Deleted,
    Renamed
}

/// <summary>A commit ahead of the remote, with the content of each changed file (spec 054, Task 6.1).</summary>
public sealed record LocalCommit(string Sha, string Message, IReadOnlyList<CommitFileContent> Files);

/// <summary>Content of a file in a <see cref="LocalCommit"/> and its parent (<c>null</c> = absent).</summary>
public sealed record CommitFileContent(string Path, string? Content, string? ParentContent);

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
