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
    /// Pushes a committed brain consolidation to the brain's base branch. When other runs pushed to the
    /// brain since the consolidation cloned it, the push is retried on top of their changes: the
    /// consolidated files are kept, and the lines those runs added to them are appended, so nothing they
    /// learned is lost; the next consolidation folds those lines in. Throws when the push still fails.
    /// Returns the attempt on which the push succeeded: 1 when no other run pushed in the meantime, 2 or more when the consolidation was merged onto newer brain commits first.
    /// </summary>
    Task<int> PushConsolidationAsync(string brainPath, string commitMessage, IRepositoryProvider brainProvider, CancellationToken ct, int maxPushRetries = 3);

    /// <summary>
    /// Ensures a .gitignore entry exists in the given content. Pure string manipulation.
    /// </summary>
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
