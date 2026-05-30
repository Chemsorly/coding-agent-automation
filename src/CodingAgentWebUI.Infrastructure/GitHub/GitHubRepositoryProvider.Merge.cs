using LibGit2Sharp;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using Repository = LibGit2Sharp.Repository;
using MergeResult = CodingAgentWebUI.Pipeline.Models.MergeResult;

namespace CodingAgentWebUI.Infrastructure.GitHub;

public partial class GitHubRepositoryProvider
{
    public Task<MergeResult> MergeFromBaseAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);
            return await RepositoryGitOperations.MergeFromBase(workspacePath, _baseBranch, GitConstants.TokenUsername, token, _gitPipeline, ct);
        }, ct);
    }

    /// <summary>
    /// Auto-resolves rename/delete conflicts where one side of the conflict is null.
    /// These occur when a file was renamed on one branch and modified on the other.
    /// Resolution strategy: accept whichever side has the file (prefer "theirs"/base for renames,
    /// since the base branch represents the canonical current state).
    /// </summary>
    private static void AutoResolveRenameDeleteConflicts(Repository repo, string workspacePath)
    {
        var conflictsToResolve = repo.Index.Conflicts
            .Where(c => c.Ours == null || c.Theirs == null)
            .ToList();

        var resolvedCount = 0;

        foreach (var conflict in conflictsToResolve)
        {
            var conflictPath = conflict.Ancestor?.Path ?? conflict.Ours?.Path ?? conflict.Theirs?.Path;
            if (conflictPath == null) continue;

            try
            {
                if (conflict.Ours == null && conflict.Theirs == null)
                {
                    // Both sides deleted the file — no conflict, just clear it.
                    repo.Index.Remove(conflict.Ancestor!.Path);
                    Log.Information("Auto-resolved conflict: both sides deleted {Path}", conflictPath);
                    resolvedCount++;
                }
                else if (conflict.Theirs != null && conflict.Ours == null)
                {
                    // File exists on base (theirs) but not on branch (ours) — accept theirs.
                    // This happens when the branch deleted a file that base still has.
                    var blob = repo.Lookup<LibGit2Sharp.Blob>(conflict.Theirs.Id);
                    if (blob != null)
                    {
                        var filePath = Path.Combine(workspacePath, conflict.Theirs.Path.Replace('/', Path.DirectorySeparatorChar));
                        var dir = Path.GetDirectoryName(filePath);
                        if (dir != null && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        // Use raw content stream to handle binary files correctly.
                        using (var contentStream = blob.GetContentStream())
                        using (var fileStream = File.Create(filePath))
                        {
                            contentStream.CopyTo(fileStream);
                        }

                        Commands.Stage(repo, conflict.Theirs.Path);
                        Log.Information("Auto-resolved rename/delete conflict: accepted theirs for {Path}", conflictPath);
                        resolvedCount++;
                    }
                }
                else if (conflict.Ours != null && conflict.Theirs == null)
                {
                    // File exists on branch (ours) but not on base (theirs).
                    // This means the file was renamed or deleted on base.
                    // Only auto-resolve if the file doesn't exist on disk (merge already handled it).
                    // If the file still exists, skip — the agent needs to handle this case
                    // (branch modifications may need to be applied to the renamed path).
                    var filePath = Path.Combine(workspacePath, conflict.Ours.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(filePath))
                    {
                        // File already doesn't exist on disk — safe to stage the removal.
                        repo.Index.Remove(conflict.Ours.Path);
                        Log.Information("Auto-resolved rename/delete conflict: staged removal of {Path} (file absent from disk)", conflict.Ours.Path);
                        resolvedCount++;
                    }
                    else
                    {
                        // File exists with potential branch modifications — skip auto-resolution.
                        // This will remain as a conflict for the agent to resolve.
                        Log.Information("Skipping auto-resolution for {Path}: file exists on branch but was renamed/deleted on base", conflict.Ours.Path);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to auto-resolve rename/delete conflict for {Path}", conflictPath);
            }
        }

        if (resolvedCount > 0)
        {
            repo.Index.Write();
            Log.Information("Auto-resolved {Count} rename/delete conflict(s)", resolvedCount);
        }
    }
}
