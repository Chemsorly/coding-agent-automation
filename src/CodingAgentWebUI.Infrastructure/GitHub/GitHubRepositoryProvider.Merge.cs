using LibGit2Sharp;
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

            using var repo = new Repository(workspacePath);

            var headBranchName = repo.Head.FriendlyName;
            var headSha = repo.Head.Tip.Sha[..8];

            Log.Information(
                "Rebase: starting for branch {BranchName} (HEAD={HeadSha}) onto origin/{BaseBranch}",
                headBranchName, headSha, _baseBranch);

            // Fetch latest origin/main to ensure we rebase onto the absolute latest base branch.
            // Without this, the clone's origin/main may be stale if main advanced after clone.
            var remote = repo.Network.Remotes["origin"];
            var fetchOptions = new FetchOptions
            {
                CredentialsProvider = (_, _, _) =>
                    new UsernamePasswordCredentials { Username = GitConstants.TokenUsername, Password = token }
            };
            var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
            Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, null);

            var baseBranch = repo.Branches[$"origin/{_baseBranch}"];
            if (baseBranch == null)
                throw new InvalidOperationException(
                    $"Base branch 'origin/{_baseBranch}' not found after fetch.");

            var baseSha = baseBranch.Tip.Sha[..8];
            Log.Debug(
                "Rebase: fetched origin/{BaseBranch} at {BaseSha}, branch HEAD at {HeadSha}",
                _baseBranch, baseSha, headSha);

            // If the base branch tip is already an ancestor of HEAD, no rebase needed.
            var mergeBase = repo.ObjectDatabase.FindMergeBase(repo.Head.Tip, baseBranch.Tip);
            if (mergeBase?.Sha == baseBranch.Tip.Sha)
            {
                Log.Information(
                    "Rebase: branch {BranchName} is already up-to-date with origin/{BaseBranch} (base={BaseSha}), no rebase needed",
                    headBranchName, _baseBranch, baseSha);
                return new MergeResult
                {
                    Success = true,
                    HasConflicts = false,
                    ConflictFiles = Array.Empty<string>()
                };
            }

            if (mergeBase != null)
            {
                var mergeBaseSha = mergeBase.Sha[..8];
                var commitsAhead = repo.Commits.QueryBy(new CommitFilter
                {
                    IncludeReachableFrom = repo.Head.Tip,
                    ExcludeReachableFrom = mergeBase
                }).Take(500).Count();
                var commitsBehind = repo.Commits.QueryBy(new CommitFilter
                {
                    IncludeReachableFrom = baseBranch.Tip,
                    ExcludeReachableFrom = mergeBase
                }).Take(500).Count();

                Log.Information(
                    "Rebase: branch {BranchName} is {CommitsAhead} ahead, {CommitsBehind} behind origin/{BaseBranch} (merge-base={MergeBaseSha})",
                    headBranchName, commitsAhead, commitsBehind, _baseBranch, mergeBaseSha);
            }
            else
            {
                Log.Warning("Rebase: no common ancestor between {BranchName} and origin/{BaseBranch}, proceeding with rebase",
                    headBranchName, _baseBranch);
            }

            var identity = new Identity(GitConstants.CommitAuthorName, GitConstants.CommitAuthorEmail);

            // Perform interactive-less rebase: replay branch commits on top of origin/main.
            var rebaseOptions = new RebaseOptions();
            var rebaseResult = repo.Rebase.Start(repo.Head, baseBranch, baseBranch, identity, rebaseOptions);

            if (rebaseResult.Status == RebaseStatus.Conflicts)
            {
                // Collect conflicting files
                var conflictFiles = repo.Index.Conflicts
                    .Select(c => c.Ancestor?.Path ?? c.Ours?.Path ?? c.Theirs?.Path)
                    .Where(p => p != null)
                    .Distinct()
                    .ToList();

                Log.Warning(
                    "Rebase: branch {BranchName} onto origin/{BaseBranch} produced {ConflictCount} conflict(s) at step {CurrentStep}/{TotalSteps}. Force-resolving using incoming (main wins). Conflicts: {@ConflictFiles}",
                    headBranchName, _baseBranch, conflictFiles.Count,
                    rebaseResult.CompletedStepCount + 1, rebaseResult.TotalStepCount,
                    conflictFiles);

                // Force-resolve all conflicts by accepting "theirs" (incoming/base branch version).
                // Main always has priority — the code generation phase will re-implement branch changes.
                ForceResolveConflictsUsingTheirs(repo, workspacePath);

                // Continue the rebase after resolving conflicts
                var continueIdentity = new Identity(GitConstants.CommitAuthorName, GitConstants.CommitAuthorEmail);
                var continueResult = repo.Rebase.Continue(continueIdentity, new RebaseOptions());

                // Handle any further conflicts in subsequent rebase steps
                while (continueResult.Status == RebaseStatus.Conflicts)
                {
                    var additionalConflicts = repo.Index.Conflicts
                        .Select(c => c.Ancestor?.Path ?? c.Ours?.Path ?? c.Theirs?.Path)
                        .Where(p => p != null)
                        .Distinct()
                        .ToList();

                    foreach (var f in additionalConflicts.Where(f => !conflictFiles.Contains(f)))
                        conflictFiles.Add(f!);

                    Log.Warning(
                        "Rebase: additional conflict(s) at step {CurrentStep}/{TotalSteps}, force-resolving. New conflicts: {@AdditionalConflicts}",
                        continueResult.CompletedStepCount + 1, continueResult.TotalStepCount, additionalConflicts);

                    ForceResolveConflictsUsingTheirs(repo, workspacePath);
                    continueResult = repo.Rebase.Continue(continueIdentity, new RebaseOptions());
                }

                Log.Information(
                    "Rebase: force-resolved {ConflictCount} file(s) using incoming (main wins), rebase completed. New HEAD={NewHeadSha}",
                    conflictFiles.Count, repo.Head.Tip.Sha[..8]);

                return new MergeResult
                {
                    Success = true,
                    HasConflicts = true,
                    ForceResolved = true,
                    ConflictFiles = conflictFiles!
                };
            }

            Log.Information(
                "Rebase: successfully rebased {BranchName} onto origin/{BaseBranch} ({TotalSteps} commits replayed, new HEAD={NewHeadSha})",
                headBranchName, _baseBranch, rebaseResult.TotalStepCount, repo.Head.Tip.Sha[..8]);

            return new MergeResult
            {
                Success = true,
                HasConflicts = false,
                ConflictFiles = Array.Empty<string>()
            };
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

    /// <summary>
    /// Force-resolves all conflicts by accepting "theirs" (incoming/base branch version).
    /// Main always wins — the code generation phase will re-implement the branch's changes
    /// on top of the current main state.
    /// </summary>
    private static void ForceResolveConflictsUsingTheirs(Repository repo, string workspacePath)
    {
        var conflicts = repo.Index.Conflicts.ToList();
        var resolvedCount = 0;

        foreach (var conflict in conflicts)
        {
            var conflictPath = conflict.Ancestor?.Path ?? conflict.Ours?.Path ?? conflict.Theirs?.Path;
            if (conflictPath == null) continue;

            try
            {
                if (conflict.Theirs != null)
                {
                    // Accept the incoming (base/main) version of the file
                    var blob = repo.Lookup<LibGit2Sharp.Blob>(conflict.Theirs.Id);
                    if (blob != null)
                    {
                        var filePath = Path.Combine(workspacePath, conflict.Theirs.Path.Replace('/', Path.DirectorySeparatorChar));
                        var dir = Path.GetDirectoryName(filePath);
                        if (dir != null && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        using (var contentStream = blob.GetContentStream())
                        using (var fileStream = File.Create(filePath))
                        {
                            contentStream.CopyTo(fileStream);
                        }

                        Commands.Stage(repo, conflict.Theirs.Path);
                        resolvedCount++;
                    }
                }
                else
                {
                    // File was deleted on base (theirs is null) — accept the deletion
                    var pathToRemove = conflict.Ours?.Path ?? conflict.Ancestor?.Path;
                    if (pathToRemove != null)
                    {
                        var filePath = Path.Combine(workspacePath, pathToRemove.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(filePath))
                            File.Delete(filePath);
                        repo.Index.Remove(pathToRemove);
                        resolvedCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to force-resolve conflict for {Path}, skipping", conflictPath);
            }
        }

        repo.Index.Write();
        Log.Information("Force-resolved {Count}/{Total} conflict(s) using incoming (main wins)",
            resolvedCount, conflicts.Count);
    }
}
