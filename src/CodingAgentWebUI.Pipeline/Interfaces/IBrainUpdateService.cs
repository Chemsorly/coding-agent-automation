using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Handles brain repository change detection, validation, conflict resolution,
/// and commit/push operations.
/// </summary>
public interface IBrainUpdateService
{
    Task<IReadOnlyList<string>> DetectChangesAsync(string brainPath, CancellationToken ct);
    BrainValidationResult Validate(string brainPath, string runId, IReadOnlyList<string> changedFiles);
    Task AppendFallbackLogEntryAsync(string brainPath, string runId, IReadOnlyList<string> modifiedFiles, CancellationToken ct);
    Task<BrainSyncResult> CommitAndPushAsync(string brainPath, string runId, string issueIdentifier, IRepositoryProvider brainProvider, CancellationToken ct, int maxPushRetries = 3);

    /// <summary>
    /// Ensures a .gitignore entry exists in the given content. Pure string manipulation.
    /// </summary>
    static string EnsureGitignoreEntry(string gitignoreContent, string entry)
    {
        ArgumentNullException.ThrowIfNull(gitignoreContent);
        ArgumentNullException.ThrowIfNull(entry);

        var lines = gitignoreContent.Split('\n');
        var trimmedEntry = entry.Trim();

        foreach (var line in lines)
        {
            if (line.Trim() == trimmedEntry)
                return gitignoreContent;
        }

        var sb = new System.Text.StringBuilder(gitignoreContent);
        if (gitignoreContent.Length > 0 && !gitignoreContent.EndsWith('\n'))
            sb.Append('\n');
        sb.Append(trimmedEntry);
        sb.Append('\n');
        return sb.ToString();
    }
}
