using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Handles brain repository change detection, validation, conflict resolution,
/// and commit/push operations.
/// </summary>
public interface IBrainUpdateService
{
    Task<IReadOnlyList<string>> DetectChangesAsync(string brainPath, CancellationToken ct);
    BrainValidationResult Validate(string brainPath, RunId runId, IReadOnlyList<string> changedFiles);
    Task AppendFallbackLogEntryAsync(string brainPath, RunId runId, IReadOnlyList<string> modifiedFiles, CancellationToken ct);
    Task<BrainSyncResult> CommitAndPushAsync(string brainPath, RunId runId, string issueIdentifier, IRepositoryProvider brainProvider, CancellationToken ct, int maxPushRetries = 3);

    /// <summary>
    /// Discards changes outside the inbox file, stages and commits it, then pushes with retry/rebase (spec 054, req 3.7).
    /// Non-fatal (T6). Returns a <see cref="BrainSyncResult"/> with the discarded paths.
    /// </summary>
    Task<BrainSyncResult> CommitInboxAndPushAsync(string brainPath, string relativePath, string message, IRepositoryProvider brainProvider, CancellationToken ct, int maxPushRetries = 3);

    /// <summary>
    /// Pushes a committed brain consolidation to the brain's base branch. Retries on non-fast-forward.
    /// Returns the attempt number on success. Throws on failure.
    /// </summary>
    Task<int> PushConsolidationAsync(string brainPath, string commitMessage, IRepositoryProvider brainProvider, CancellationToken ct, int maxPushRetries = 3);

    // ── Review git operations (spec 054 Task 7A.1) ─────────────────────────

    Task<IReadOnlyList<string>> ListTrackedFilesAsync(string brainPath, CancellationToken ct);
    string? ReadFileAtCommit(string brainPath, string commitSha, string relativePath);
    string? ReadFileAtHead(string brainPath, string relativePath);
    IReadOnlyList<BrainPathChange> GetWorkingTreeChanges(string brainPath);
    string GetReviewPatch(string brainPath);
    IReadOnlyList<BrainCommitInfo> GetSkillCommitsSinceLastReview(string brainPath);

    /// <summary>Ensures a .gitignore entry exists in the given content. Pure string manipulation.</summary>
    static string EnsureGitignoreEntry(string gitignoreContent, string entry)
    {
        ArgumentNullException.ThrowIfNull(gitignoreContent);
        ArgumentNullException.ThrowIfNull(entry);

        var lines = gitignoreContent.Split('\n');
        var trimmedEntry = entry.Trim();

        if (lines.Any(l => l.Trim() == trimmedEntry))
            return gitignoreContent;

        var sb = new System.Text.StringBuilder(gitignoreContent);
        if (gitignoreContent.Length > 0 && !gitignoreContent.EndsWith('\n'))
            sb.Append('\n');
        sb.Append(trimmedEntry);
        sb.Append('\n');
        return sb.ToString();
    }
}
