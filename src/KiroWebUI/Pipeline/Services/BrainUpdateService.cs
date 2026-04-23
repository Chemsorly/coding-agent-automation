using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KiroWebUI.Pipeline;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using LibGit2Sharp;
using Serilog;
using ILogger = Serilog.ILogger;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Handles brain repository change detection, validation, conflict resolution,
/// and commit/push operations. Receives the brain IRepositoryProvider at method
/// call time (not via constructor), matching the per-run provider pattern.
/// </summary>
public partial class BrainUpdateService
{
    private readonly ILogger _logger;

    public BrainUpdateService(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Detects whether the agent made changes to files in the .brain/ directory.
    /// Returns the list of changed file paths (relative to brainPath).
    /// Uses LibGit2Sharp directly (pragmatic PoC shortcut).
    /// </summary>
    public Task<IReadOnlyList<string>> DetectChangesAsync(string brainPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(brainPath);

        return Task.Run(() =>
        {
            using var repo = new Repository(brainPath);
            var status = repo.RetrieveStatus(new StatusOptions());
            var changedFiles = new List<string>();

            foreach (var entry in status)
            {
                if (entry.State != FileStatus.Ignored && entry.State != FileStatus.Unaltered)
                {
                    changedFiles.Add(entry.FilePath);
                }
            }

            return (IReadOnlyList<string>)changedFiles;
        }, ct);
    }

    /// <summary>
    /// Pure function that returns the gitignore content with the entry added if not already present.
    /// Idempotent — applying it twice produces identical content.
    /// </summary>
    public static string EnsureGitignoreEntry(string gitignoreContent, string entry)
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

        // Append entry, ensuring there's a newline before it if the file doesn't end with one
        var sb = new StringBuilder(gitignoreContent);
        if (gitignoreContent.Length > 0 && !gitignoreContent.EndsWith('\n'))
            sb.Append('\n');
        sb.Append(trimmedEntry);
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Validates brain updates: checks for session log, log.md update,
    /// and proper entry format. Returns validation result with warnings.
    /// </summary>
    public BrainValidationResult Validate(string brainPath, string runId, IReadOnlyList<string> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(brainPath);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var warnings = new List<string>();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Check for session log file at sessions/{date}_{runId}.md
        var sessionLogPattern = $"sessions/{today}_{runId}.md";
        var sessionLogCreated = changedFiles.Any(f =>
            f.Replace('\\', '/').Contains($"sessions/", StringComparison.OrdinalIgnoreCase) &&
            f.Contains(runId, StringComparison.OrdinalIgnoreCase));

        if (!sessionLogCreated)
        {
            warnings.Add("session log");
            _logger.Warning("Brain update validation: session log not created for run {RunId}", runId);
        }

        // Check whether log.md was modified
        var operationLogUpdated = changedFiles.Any(f =>
            f.Replace('\\', '/').Equals("log.md", StringComparison.OrdinalIgnoreCase) ||
            f.Replace('\\', '/').EndsWith("/log.md", StringComparison.OrdinalIgnoreCase));

        if (!operationLogUpdated)
        {
            warnings.Add("log.md entry");
            _logger.Warning("Brain update validation: log.md not updated for run {RunId}", runId);
        }

        // Check whether new entries in knowledge files contain ### YYYY-MM-DD header format
        var entryFormatValid = true;
        var knowledgeFiles = changedFiles
            .Where(f => !f.Replace('\\', '/').StartsWith("sessions/", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Replace('\\', '/').Equals("log.md", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

        foreach (var file in knowledgeFiles)
        {
            var fullPath = Path.Combine(brainPath, file);
            if (!File.Exists(fullPath)) continue;

            var content = File.ReadAllText(fullPath);
            // Check if any new entries have the ### YYYY-MM-DD header format
            if (content.Contains("###") && !DateHeaderRegex().IsMatch(content))
            {
                entryFormatValid = false;
                break;
            }
        }

        if (!entryFormatValid)
        {
            warnings.Add("proper entry format");
            _logger.Warning("Brain update validation: entry format invalid for run {RunId}", runId);
        }

        return new BrainValidationResult
        {
            SessionLogCreated = sessionLogCreated,
            OperationLogUpdated = operationLogUpdated,
            EntryFormatValid = entryFormatValid,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Appends a basic operation log entry to log.md when the agent didn't update it.
    /// Contains run ID and list of modified files.
    /// </summary>
    public async Task AppendFallbackLogEntryAsync(
        string brainPath, string runId,
        IReadOnlyList<string> modifiedFiles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(brainPath);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(modifiedFiles);

        var logPath = Path.Combine(brainPath, "log.md");
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var entry = BuildFallbackLogEntry(runId, today, modifiedFiles);

        await File.AppendAllTextAsync(logPath, entry, ct);
        _logger.Information("Appended fallback log entry for run {RunId} to {LogPath}", runId, logPath);
    }

    /// <summary>
    /// Builds the fallback log entry string. Extracted as internal static for testability.
    /// </summary>
    internal static string BuildFallbackLogEntry(string runId, string date, IReadOnlyList<string> modifiedFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- {date} | {runId} | auto-fallback | Files: {string.Join(", ", modifiedFiles)}");
        return sb.ToString();
    }

    /// <summary>
    /// Resolves merge conflicts by concatenating both sides with a separator.
    /// Knowledge files are append-mostly, so both contributions are valuable.
    /// </summary>
    internal static string ResolveConflictAcceptBoth(string oursContent, string theirsContent)
    {
        ArgumentNullException.ThrowIfNull(oursContent);
        ArgumentNullException.ThrowIfNull(theirsContent);

        var sb = new StringBuilder();
        sb.Append(oursContent);
        if (!oursContent.EndsWith('\n'))
            sb.AppendLine();
        sb.AppendLine("<!-- === merge: accepted both sides === -->");
        sb.Append(theirsContent);
        if (!theirsContent.EndsWith('\n'))
            sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Builds the commit message for brain repository changes.
    /// Extracted as internal static for testability (Property 4).
    /// </summary>
    internal static string BuildCommitMessage(string runId, string issueIdentifier)
    {
        return $"brain: update from run {runId} ({issueIdentifier})";
    }

    /// <summary>
    /// Commits brain changes using the provided repository provider.
    /// Owns the full conflict resolution cycle:
    /// (1) Pull via PullAsync (fetch + fast-forward attempt).
    /// (2) If conflicts detected via repo.Index.Conflicts, extract both sides.
    /// (3) Concatenate via ResolveConflictAcceptBoth.
    /// (4) Write resolved content to disk.
    /// (5) Stage resolved files.
    /// (6) Commit with message referencing runId and issueIdentifier.
    /// (7) Push.
    /// </summary>
    public async Task<BrainSyncResult> CommitAndPushAsync(
        string brainPath, string runId, string issueIdentifier,
        IRepositoryProvider brainProvider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(brainPath);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(brainProvider);

        try
        {
            // Step 1: Pull latest changes
            try
            {
                await brainProvider.PullAsync(brainPath, ct);
            }
            catch (NonFastForwardException)
            {
                _logger.Warning("Brain repo pull was not fast-forward, attempting conflict resolution");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Brain repo pull failed, attempting to commit anyway");
            }

            // Step 2-5: Detect and resolve conflicts
            await Task.Run(() =>
            {
                using var repo = new Repository(brainPath);

                if (repo.Index.Conflicts.Any())
                {
                    _logger.Information("Resolving {ConflictCount} brain repo conflicts", repo.Index.Conflicts.Count());

                    foreach (var conflict in repo.Index.Conflicts)
                    {
                        try
                        {
                            var oursContent = conflict.Ours != null
                                ? repo.Lookup<Blob>(conflict.Ours.Id)?.GetContentText() ?? ""
                                : "";
                            var theirsContent = conflict.Theirs != null
                                ? repo.Lookup<Blob>(conflict.Theirs.Id)?.GetContentText() ?? ""
                                : "";

                            var resolved = ResolveConflictAcceptBoth(oursContent, theirsContent);
                            var filePath = conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor?.Path;

                            if (filePath != null)
                            {
                                var fullPath = Path.Combine(brainPath, filePath);
                                File.WriteAllText(fullPath, resolved);
                                Commands.Stage(repo, filePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning(ex, "Failed to resolve conflict for {Path}",
                                conflict.Ours?.Path ?? conflict.Theirs?.Path ?? "unknown");
                        }
                    }
                }

                // Stage all changes
                Commands.Stage(repo, "*");

                // Commit
                var signature = GitOperationsHelper.CreatePipelineSignature();
                var message = BuildCommitMessage(runId, issueIdentifier);
                repo.Commit(message, signature, signature);
            }, ct);

            // Step 7: Push
            await brainProvider.PushBranchAsync(brainPath, brainProvider.BaseBranch, ct);

            // Count committed files
            var filesCommitted = await Task.Run(() =>
            {
                using var repo = new Repository(brainPath);
                var headCommit = repo.Head.Tip;
                if (headCommit?.Parents.Any() == true)
                {
                    var diff = repo.Diff.Compare<TreeChanges>(
                        headCommit.Parents.First().Tree, headCommit.Tree);
                    return diff.Count;
                }
                return 0;
            }, ct);

            _logger.Information("Brain repo committed and pushed {FileCount} files for run {RunId}",
                filesCommitted, runId);

            return new BrainSyncResult
            {
                Success = true,
                FilesCommitted = filesCommitted
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Brain repo commit/push failed for run {RunId}", runId);
            return new BrainSyncResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    [GeneratedRegex(@"###\s+\d{4}-\d{2}-\d{2}")]
    private static partial Regex DateHeaderRegex();
}
