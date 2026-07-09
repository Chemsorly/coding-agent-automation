using LibGit2Sharp;
using Polly;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using Signature = LibGit2Sharp.Signature;
using Repository = LibGit2Sharp.Repository;
using MergeResult = CodingAgentWebUI.Pipeline.Models.MergeResult;

namespace CodingAgentWebUI.Infrastructure.Git;

/// <summary>
/// Shared LibGit2Sharp-based git operations used by both GitHub and GitLab repository providers.
/// Extracted to eliminate ~600 lines of duplication between the two providers.
/// </summary>
internal static class RepositoryGitOperations
{
    static RepositoryGitOperations()
    {
        // Disable libgit2's directory ownership validation. In Docker containers,
        // cloned workspace directories often have ownership mismatches (CVE-2022-24765
        // mitigation). Without this, Commands.Stage() silently fails.
        GlobalSettings.SetOwnerValidation(false);
    }

    public static async Task Clone(
        string workspacePath, string cloneUrl, string baseBranch,
        string tokenUsername, string token, ResiliencePipeline pipeline, CancellationToken ct)
    {
        var options = new CloneOptions
        {
            BranchName = baseBranch,
            FetchOptions =
            {
                CredentialsProvider = (_, _, _) =>
                    new UsernamePasswordCredentials { Username = tokenUsername, Password = token }
            }
        };

        await pipeline.ExecuteAsync(async _ =>
        {
            await Task.CompletedTask;
            Repository.Clone(cloneUrl, workspacePath, options);
        }, ct);
    }

    public static async Task Pull(
        string workspacePath, string baseBranch,
        string tokenUsername, string token, ResiliencePipeline pipeline, CancellationToken ct)
    {
        await pipeline.ExecuteAsync(async _ =>
        {
            await Task.CompletedTask;
            using var repo = new Repository(workspacePath);
            var remote = repo.Network.Remotes["origin"];

            var fetchOptions = new FetchOptions
            {
                CredentialsProvider = (_, _, _) =>
                    new UsernamePasswordCredentials { Username = tokenUsername, Password = token }
            };
            var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
            Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, null);

            var trackingBranch = repo.Head.TrackedBranch
                ?? repo.Branches[$"origin/{baseBranch}"];
            if (trackingBranch != null)
            {
                var signature = new Signature(
                    GitConstants.CommitAuthorName, GitConstants.CommitAuthorEmail, DateTimeOffset.UtcNow);
                repo.Merge(trackingBranch, signature, new MergeOptions
                {
                    FastForwardStrategy = FastForwardStrategy.FastForwardOnly
                });
            }
        }, ct);
    }

    public static string CreateBranch(string workspacePath, string branchName)
    {
        using var repo = new Repository(workspacePath);
        var branch = repo.CreateBranch(branchName);
        Commands.Checkout(repo, branch);
        return branch.FriendlyName;
    }

    public static void CheckoutRemoteBranch(string workspacePath, string branchName)
    {
        using var repo = new Repository(workspacePath);

        var remoteBranch = repo.Branches[$"origin/{branchName}"];
        if (remoteBranch == null)
        {
            Log.Error("Remote branch 'origin/{BranchName}' not found — branch may have been deleted", branchName);
            throw new InvalidOperationException(
                $"Remote branch 'origin/{branchName}' not found. " +
                $"The branch may have been deleted.");
        }

        var localBranch = repo.CreateBranch(branchName, remoteBranch.Tip);
        repo.Branches.Update(localBranch,
            b => b.TrackedBranch = remoteBranch.CanonicalName);
        Commands.Checkout(repo, localBranch, new CheckoutOptions
        {
            CheckoutModifiers = CheckoutModifiers.Force
        });
    }

    public static IReadOnlyList<string> CommitAll(
        string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, bool allowEmpty,
        IReadOnlyList<string>? pipelineInjectedPaths = null)
    {
        using var repo = new Repository(workspacePath);

        var preStatus = repo.RetrieveStatus(new StatusOptions
        {
            DetectRenamesInIndex = false,
            DetectRenamesInWorkDir = false
        });

        foreach (var entry in preStatus)
        {
            Log.Debug("CommitAllAsync status: {FilePath} = {State}", entry.FilePath, entry.State);
        }

        var stagedAny = false;
        foreach (var entry in preStatus)
        {
            if (entry.State.HasFlag(FileStatus.Conflicted))
            {
                Log.Debug("CommitAllAsync staging conflicted (resolved) file via Index.Add: {FilePath}", entry.FilePath);
                repo.Index.Add(entry.FilePath);
                stagedAny = true;
            }
            else if (entry.State.HasFlag(FileStatus.DeletedFromWorkdir))
            {
                Log.Debug("CommitAllAsync staging deleted file via Index.Remove: {FilePath}", entry.FilePath);
                repo.Index.Remove(entry.FilePath);
                stagedAny = true;
            }
            else if (entry.State.HasFlag(FileStatus.NewInWorkdir)
                || entry.State.HasFlag(FileStatus.ModifiedInWorkdir)
                || entry.State.HasFlag(FileStatus.RenamedInWorkdir)
                || entry.State.HasFlag(FileStatus.TypeChangeInWorkdir))
            {
                Log.Debug("CommitAllAsync staging workdir file via Index.Add: {FilePath}", entry.FilePath);
                repo.Index.Add(entry.FilePath);
                stagedAny = true;
            }
        }

        if (stagedAny)
            repo.Index.Write();

        var unstaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Hardcoded: ALWAYS unstage pipeline-injected paths regardless of configured blacklist.
        // These directories/files contain pipeline metadata and steering content that must
        // never be committed by the pipeline under any circumstances:
        //   .agent  — MCP configs, prompt files, analysis output, review findings
        //   .brain  — cloned brain/knowledge repository
        //   + provider-specific paths from IAgentProvider.PipelineInjectedPaths
        //     (e.g., .kiro for Kiro CLI, AGENTS.md for OpenCode)
        var universalHardcoded = new[] { ".agent", ".brain" };
        var hardcodedBlacklist = pipelineInjectedPaths is { Count: > 0 }
            ? universalHardcoded.Concat(pipelineInjectedPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : universalHardcoded;
        {
            var indexChanges = repo.Diff.Compare<TreeChanges>(repo.Head.Tip?.Tree, DiffTargets.Index);
            foreach (var change in indexChanges)
            {
                if (PathBlacklistHelper.IsPathBlacklisted(change.Path, hardcodedBlacklist))
                {
                    Commands.Unstage(repo, change.Path);
                    unstaged.Add(change.Path.Replace('\\', '/'));
                }
            }
        }

        // Apply configurable blacklist (may overlap with hardcoded — skip already-unstaged paths)
        if (blacklistedPaths is { Count: > 0 })
        {
            var indexChanges = repo.Diff.Compare<TreeChanges>(repo.Head.Tip?.Tree, DiffTargets.Index);
            foreach (var change in indexChanges)
            {
                var normalized = change.Path.Replace('\\', '/');
                if (!unstaged.Contains(normalized) && PathBlacklistHelper.IsPathBlacklisted(change.Path, blacklistedPaths))
                {
                    Commands.Unstage(repo, change.Path);
                    unstaged.Add(normalized);
                }
            }
        }

        var stagedChanges = repo.Diff.Compare<TreeChanges>(repo.Head.Tip?.Tree, DiffTargets.Index);
        Log.Debug("CommitAllAsync final staged count (via Diff): {Count}", stagedChanges.Count);
        foreach (var change in stagedChanges)
        {
            Log.Debug("CommitAllAsync final staged: {FilePath} = {Status}", change.Path, change.Status);
        }

        if (stagedChanges.Count == 0 && !allowEmpty)
        {
            Log.Warning("No changes to commit in workspace {WorkspacePath} — agent did not modify any files", workspacePath);
            throw new InvalidOperationException("No changes to commit. The agent did not modify any files in the workspace.");
        }

        var signature = new Signature(GitConstants.CommitAuthorName, GitConstants.CommitAuthorEmail, DateTimeOffset.UtcNow);
        var commitOptions = allowEmpty ? new CommitOptions { AllowEmptyCommit = true } : new CommitOptions();
        repo.Commit(message, signature, signature, commitOptions);

        return unstaged.ToList();
    }

    public static async Task Push(
        string workspacePath, string branchName, bool forcePush,
        string tokenUsername, string token, ResiliencePipeline pipeline, CancellationToken ct)
    {
        using var repo = new Repository(workspacePath);
        var remote = repo.Network.Remotes["origin"];
        string? pushError = null;
        var options = new PushOptions
        {
            CredentialsProvider = (_, _, _) =>
                new UsernamePasswordCredentials { Username = tokenUsername, Password = token },
            OnPushStatusError = error =>
                pushError = $"Push failed for ref '{error.Reference}': {error.Message}"
        };

        // Force-push uses '+' prefix on refspec to allow non-fast-forward updates (required after rebase)
        var refSpec = forcePush
            ? $"+refs/heads/{branchName}"
            : $"refs/heads/{branchName}";

        if (forcePush)
            Log.Information("Force-pushing branch {BranchName} (post-rebase history rewrite)", branchName);

        await pipeline.ExecuteAsync(async _ =>
        {
            await Task.CompletedTask;
            pushError = null;
            repo.Network.Push(remote, refSpec, options);

            if (pushError != null)
            {
                var category = PushErrorClassifier.Classify(pushError);
                var message = PushErrorClassifier.GetActionableMessage(category, branchName);
                Log.Error("Push failed for branch {BranchName}: {PushError} (category={Category})", branchName, pushError, category);
                // Network and Unknown errors are potentially transient — throw LibGit2SharpException
                // so the Polly resilience pipeline can retry them.
                throw category is PushErrorClassifier.PushFailureCategory.Network
                               or PushErrorClassifier.PushFailureCategory.Unknown
                    ? new LibGit2SharpException(pushError)
                    : new InvalidOperationException(message);
            }
        }, ct);
    }

    public static string GetHeadCommitSha(string workspacePath)
    {
        using var repo = new Repository(workspacePath);
        return repo.Head.Tip.Sha;
    }

    public static async Task<bool> HasCommitsAhead(
        string workspacePath, string baseBranch, ResiliencePipeline pipeline, CancellationToken ct)
    {
        return await pipeline.ExecuteAsync(async _ =>
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var repo = new Repository(workspacePath);
                    var head = repo.Head.Tip;
                    var baseBranchRef = repo.Branches[$"origin/{baseBranch}"]
                        ?? repo.Branches[baseBranch];
                    if (baseBranchRef == null) return true;
                    var mergeBase = repo.ObjectDatabase.FindMergeBase(head, baseBranchRef.Tip);
                    return mergeBase == null || mergeBase.Sha != head.Sha;
                }
                catch
                {
                    return true; // On error, assume there are changes and let PR creation decide
                }
            }, ct);
        }, ct);
    }

    public static IReadOnlyList<FileChangeSummary> GetFileChanges(string workspacePath, string baseBranch)
    {
        try
        {
            using var repo = new Repository(workspacePath);
            var baseBranchRef = repo.Branches[$"origin/{baseBranch}"]
                ?? repo.Branches[baseBranch];
            if (baseBranchRef == null)
                return Array.Empty<FileChangeSummary>();

            var baseCommit = baseBranchRef.Tip;
            var headCommit = repo.Head.Tip;

            var changes = CollectChangesWithLineStats(repo, baseCommit.Tree, headCommit.Tree);

            if (changes.Count == 0)
            {
                var workingDiff = repo.Diff.Compare<TreeChanges>(
                    baseCommit.Tree, DiffTargets.WorkingDirectory);
                foreach (var entry in workingDiff)
                {
                    var status = MapChangeKind(entry.Status);
                    changes.Add(new FileChangeSummary(status, entry.Path));
                }
            }

            return changes;
        }
        catch
        {
            return Array.Empty<FileChangeSummary>();
        }
    }

    public static List<FileChangeSummary> CollectChangesWithLineStats(
        IRepository repo, Tree baseTree, Tree headTree)
    {
        var changes = new List<FileChangeSummary>();
        try
        {
            using var patch = repo.Diff.Compare<Patch>(baseTree, headTree);
            foreach (var entry in patch)
            {
                var status = MapChangeKind(entry.Status);
                changes.Add(new FileChangeSummary(status, entry.Path, entry.LinesAdded, entry.LinesDeleted));
            }
        }
        catch
        {
            var diff = repo.Diff.Compare<TreeChanges>(baseTree, headTree);
            foreach (var entry in diff)
            {
                var status = MapChangeKind(entry.Status);
                changes.Add(new FileChangeSummary(status, entry.Path));
            }
        }
        return changes;
    }

    public static async Task<MergeResult> MergeFromBase(
        string workspacePath, string baseBranchName,
        string tokenUsername, string token, ResiliencePipeline pipeline, CancellationToken ct)
    {
        using var repo = new Repository(workspacePath);

        var headBranchName = repo.Head.FriendlyName;
        var headSha = repo.Head.Tip.Sha[..8];

        Log.Information(
            "Rebase: starting for branch {BranchName} (HEAD={HeadSha}) onto origin/{BaseBranch}",
            headBranchName, headSha, baseBranchName);

        // Fetch latest origin/main to ensure we rebase onto the absolute latest base branch.
        // Wrapped in the resilience pipeline to retry transient network failures.
        var remote = repo.Network.Remotes["origin"];
        var fetchOptions = new FetchOptions
        {
            CredentialsProvider = (_, _, _) =>
                new UsernamePasswordCredentials { Username = tokenUsername, Password = token }
        };
        var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
        await pipeline.ExecuteAsync(async _ =>
        {
            await Task.CompletedTask;
            Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, null);
        }, ct);

        var baseBranch = repo.Branches[$"origin/{baseBranchName}"];
        if (baseBranch == null)
        {
            Log.Error("Base branch 'origin/{BaseBranchName}' not found after fetch", baseBranchName);
            throw new InvalidOperationException(
                $"Base branch 'origin/{baseBranchName}' not found after fetch.");
        }

        var baseSha = baseBranch.Tip.Sha[..8];
        Log.Debug(
            "Rebase: fetched origin/{BaseBranch} at {BaseSha}, branch HEAD at {HeadSha}",
            baseBranchName, baseSha, headSha);

        // If the base branch tip is already an ancestor of HEAD, no rebase needed.
        var mergeBase = repo.ObjectDatabase.FindMergeBase(repo.Head.Tip, baseBranch.Tip);
        if (mergeBase?.Sha == baseBranch.Tip.Sha)
        {
            Log.Information(
                "Rebase: branch {BranchName} is already up-to-date with origin/{BaseBranch} (base={BaseSha}), no rebase needed",
                headBranchName, baseBranchName, baseSha);
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
                headBranchName, commitsAhead, commitsBehind, baseBranchName, mergeBaseSha);
        }
        else
        {
            Log.Warning("Rebase: no common ancestor between {BranchName} and origin/{BaseBranch}, proceeding with rebase",
                headBranchName, baseBranchName);
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
                headBranchName, baseBranchName, conflictFiles.Count,
                rebaseResult.CompletedStepCount + 1, rebaseResult.TotalStepCount,
                conflictFiles);

            // Force-resolve all conflicts by accepting "theirs" (incoming/base branch version).
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
            headBranchName, baseBranchName, rebaseResult.TotalStepCount, repo.Head.Tip.Sha[..8]);

        return new MergeResult
        {
            Success = true,
            HasConflicts = false,
            ConflictFiles = Array.Empty<string>()
        };
    }

    public static void ForceResolveConflictsUsingTheirs(Repository repo, string workspacePath)
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
                    var blob = repo.Lookup<Blob>(conflict.Theirs.Id);
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

    public static string MapChangeKind(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "Added",
        ChangeKind.Deleted => "Deleted",
        ChangeKind.Renamed => "Renamed",
        ChangeKind.Copied => "Copied",
        ChangeKind.TypeChanged => "Modified",
        _ => "Modified"
    };
}
