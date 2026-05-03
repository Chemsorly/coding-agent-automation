using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using LibGit2Sharp;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Infrastructure.Git;

/// <summary>
/// Handles brain repository change detection, validation, conflict resolution,
/// and commit/push operations. Receives the brain IRepositoryProvider at method
/// call time (not via constructor), matching the per-run provider pattern.
/// </summary>
public partial class BrainUpdateService : IBrainUpdateService
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
    /// (7) Push with retry-rebase on non-fast-forward failure.
    ///
    /// NOTE: This retry-rebase approach is a local, self-healing solution that works across
    /// separate Docker containers without orchestrator involvement. If the system later requires
    /// coordination of additional shared resources beyond the brain repo, this could be superseded
    /// by a centralized semaphore managed by the orchestrator via SignalR (acquire-push-release protocol).
    /// </summary>
    public async Task<BrainSyncResult> CommitAndPushAsync(
        string brainPath, string runId, string issueIdentifier,
        IRepositoryProvider brainProvider, CancellationToken ct, int maxPushRetries = 3)
    {
        ArgumentNullException.ThrowIfNull(brainPath);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(brainProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxPushRetries, 0);

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

            // Step 2-6: Resolve conflicts, stage, and commit
            var commitMessage = BuildCommitMessage(runId, issueIdentifier);
            try
            {
                await ResolveConflictsAndCommitAsync(brainPath, commitMessage, ct);
            }
            catch (EmptyCommitException)
            {
                _logger.Warning(
                    "Brain repo has no changes to commit for run {RunId}, skipping push", runId);
                return new BrainSyncResult
                {
                    Success = true,
                    FilesCommitted = 0
                };
            }

            // Step 7: Push with retry-rebase loop on non-fast-forward failure
            await PushWithRetryRebaseAsync(brainPath, brainProvider, commitMessage, maxPushRetries, ct);

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
        // NOTE: Use `when (ex is not OperationCanceledException)` to propagate cancellation per project convention
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

    /// <summary>
    /// Resolves any index conflicts and creates a commit with all staged changes.
    /// </summary>
    private async Task ResolveConflictsAndCommitAsync(string brainPath, string commitMessage, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var repo = new Repository(brainPath);

            if (repo.Index.Conflicts.Any())
            {
                var conflictCount = repo.Index.Conflicts.Count();
                _logger.Information("Resolving {ConflictCount} brain repo conflicts", conflictCount);

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
            var signature = new Signature("CodingAgentWebUI Pipeline", "pipeline@kiro.dev", DateTimeOffset.UtcNow);
            repo.Commit(commitMessage, signature, signature);
        }, ct);
    }

    /// <summary>
    /// Pushes the current branch with retry-rebase on non-fast-forward failure.
    /// On conflict: fetches remote, resets to remote HEAD, re-applies local changes,
    /// resolves conflicts, recommits, and retries push.
    /// </summary>
    private async Task PushWithRetryRebaseAsync(
        string brainPath, IRepositoryProvider brainProvider, string commitMessage,
        int maxRetries, CancellationToken ct)
    {
        var remoteBranch = brainProvider.BaseBranch;
        ArgumentException.ThrowIfNullOrEmpty(remoteBranch, nameof(remoteBranch));

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await brainProvider.PushBranchAsync(brainPath, brainProvider.BaseBranch, ct);
                if (attempt > 1)
                {
                    _logger.Information(
                        "Brain push succeeded on attempt {Attempt}/{MaxRetries}",
                        attempt, maxRetries);
                }
                return; // success
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) &&
                attempt < maxRetries)
            {
                _logger.Warning(
                    "Brain push attempt {Attempt}/{MaxRetries} failed (non-fast-forward), rebasing...",
                    attempt, maxRetries);

                // Random jitter to reduce collision probability
                await Task.Delay(Random.Shared.Next(200, 501), ct);

                // Rebase: fetch, reset to remote, re-apply our changes
                await RebaseOntoRemoteAsync(brainPath, brainProvider, commitMessage, ct);
            }
        }
    }

    /// <summary>
    /// Fetches remote, resets local branch to remote HEAD, re-applies local changes
    /// from the saved commit, resolves conflicts, and recommits.
    /// </summary>
    private async Task RebaseOntoRemoteAsync(
        string brainPath, IRepositoryProvider brainProvider, string commitMessage, CancellationToken ct)
    {
        // Fetch latest via PullAsync (which does fetch internally).
        // We catch the NonFastForwardException since we'll handle the merge ourselves.
        try
        {
            await brainProvider.PullAsync(brainPath, ct);
        }
        catch (NonFastForwardException)
        {
            // Expected — we have local commits that diverge from remote
        }
        catch (CheckoutConflictException)
        {
            // Expected — local uncommitted changes conflict with fetched remote
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Brain repo fetch during rebase failed, attempting manual rebase");
        }

        await Task.Run(() =>
        {
            using var repo = new Repository(brainPath);

            // Save our commit's tree (the changes we want to keep)
            var ourCommit = repo.Head.Tip;
            var ourTree = ourCommit.Tree;
            var parentTree = ourCommit.Parents.FirstOrDefault()?.Tree;

            // Determine which files we changed
            if (parentTree is null)
            {
                throw new InvalidOperationException(
                    "Cannot rebase brain changes: parent tree could not be resolved. " +
                    "This may indicate the repository has no parent commit to diff against.");
            }

            var ourChanges = repo.Diff.Compare<TreeChanges>(parentTree, ourTree);

            // Reset to remote branch tip
            var remoteBranchRef = repo.Branches[$"origin/{brainProvider.BaseBranch}"];
            if (remoteBranchRef != null)
            {
                repo.Reset(ResetMode.Hard, remoteBranchRef.Tip);
            }

            // Re-apply our changes from the saved commit tree
            var conflictCount = 0;
            foreach (var change in ourChanges)
            {
                ct.ThrowIfCancellationRequested();

                var filePath = change.Path;
                var fullPath = Path.Combine(brainPath, filePath);

                if (change.Status == ChangeKind.Deleted)
                {
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);
                    continue;
                }

                // Get our version from the saved tree
                var ourBlob = ourTree[filePath]?.Target as Blob;
                var ourContent = ourBlob?.GetContentText() ?? "";

                // Check if remote also modified this file (conflict)
                var remoteContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
                var baseContent = parentTree?[filePath]?.Target is Blob baseBlob
                    ? baseBlob.GetContentText() : "";

                if (remoteContent != baseContent && ourContent != baseContent)
                {
                    // Both sides modified — resolve with accept-both
                    conflictCount++;
                    var resolved = ResolveConflictAcceptBoth(remoteContent, ourContent);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    File.WriteAllText(fullPath, resolved);
                }
                else
                {
                    // Only we modified — apply our version
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    File.WriteAllText(fullPath, ourContent);
                }
            }

            if (conflictCount > 0)
            {
                _logger.Information(
                    "Brain rebase resolved {ConflictCount} conflicts using accept-both strategy",
                    conflictCount);
            }

            // Stage and recommit
            Commands.Stage(repo, "*");
            var signature = new Signature("CodingAgentWebUI Pipeline", "pipeline@kiro.dev", DateTimeOffset.UtcNow);
            try
            {
                repo.Commit(commitMessage, signature, signature);
            }
            catch (EmptyCommitException)
            {
                // Remote already has identical changes — nothing to recommit after rebase
                _logger.Warning("Brain rebase produced empty commit (remote already has identical changes), skipping");
            }
        }, ct);
    }

    [GeneratedRegex(@"###\s+\d{4}-\d{2}-\d{2}")]
    private static partial Regex DateHeaderRegex();
}
