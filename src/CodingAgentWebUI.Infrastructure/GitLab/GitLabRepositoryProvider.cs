using LibGit2Sharp;
using NGitLab;
using Polly;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using Signature = LibGit2Sharp.Signature;
using Repository = LibGit2Sharp.Repository;
using MergeResult = CodingAgentWebUI.Pipeline.Models.MergeResult;

namespace CodingAgentWebUI.Infrastructure.GitLab;

/// <summary>
/// GitLab implementation of <see cref="IRepositoryProvider"/> for Git operations.
/// Uses LibGit2Sharp for local git operations and NGitLab for merge request management.
/// This is a partial class — merge request and review operations are in a separate file (task 6.2).
/// </summary>
public partial class GitLabRepositoryProvider : GitLabProviderBase, IRepositoryProvider
{
    private readonly string _baseBranch;
    private readonly ResiliencePipeline _gitPipeline;

    /// <inheritdoc />
    public RepositoryProviderType ProviderType => RepositoryProviderType.GitLab;

    /// <inheritdoc />
    public string BaseBranch => _baseBranch;

    /// <inheritdoc />
    public string RepositoryFullName => PathWithNamespace ?? $"project/{ProjectId}";

    /// <inheritdoc />
    public bool SupportsInlineReviewComments => true;

    /// <summary>
    /// Creates a provider with a static access token.
    /// </summary>
    public GitLabRepositoryProvider(string apiUrl, string accessToken, int projectId, string baseBranch)
        : base(apiUrl, accessToken, projectId)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    /// <summary>
    /// Creates a provider with a dynamic token provider delegate (for OrchestratorProxy token refresh).
    /// </summary>
    public GitLabRepositoryProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, int projectId, string baseBranch)
        : base(apiUrl, tokenProvider, projectId)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitLabClient.
    /// </summary>
    internal GitLabRepositoryProvider(IGitLabClient client, int projectId, string baseBranch)
        : base(client, projectId)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    // ─── Git Operations ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task CloneAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);
            var cloneUrl = BuildAuthenticatedCloneUrl(token);

            var options = new CloneOptions
            {
                BranchName = _baseBranch,
                FetchOptions =
                {
                    CredentialsProvider = (_, _, _) =>
                        new UsernamePasswordCredentials
                        {
                            Username = GitConstants.GitLabTokenUsername,
                            Password = token
                        }
                }
            };

            await _gitPipeline.ExecuteAsync(async _ =>
            {
                await Task.CompletedTask;
                Repository.Clone(cloneUrl, workspacePath, options);
            }, ct);
        }, ct);
    }

    /// <inheritdoc />
    public Task PullAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);

            await _gitPipeline.ExecuteAsync(async _ =>
            {
                await Task.CompletedTask;
                using var repo = new Repository(workspacePath);
                var remote = repo.Network.Remotes["origin"];

                var fetchOptions = new FetchOptions
                {
                    CredentialsProvider = (_, _, _) =>
                        new UsernamePasswordCredentials
                        {
                            Username = GitConstants.GitLabTokenUsername,
                            Password = token
                        }
                };
                var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
                Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, null);

                var trackingBranch = repo.Head.TrackedBranch
                    ?? repo.Branches[$"origin/{_baseBranch}"];
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
        }, ct);
    }

    /// <inheritdoc />
    public Task<string> CreateBranchAsync(string workspacePath, string branchName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(() =>
        {
            using var repo = new Repository(workspacePath);
            var branch = repo.CreateBranch(branchName);
            Commands.Checkout(repo, branch);
            return branch.FriendlyName;
        }, ct);
    }

    /// <inheritdoc />
    public Task CheckoutRemoteBranchAsync(string workspacePath, string branchName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(() =>
        {
            using var repo = new Repository(workspacePath);

            var remoteBranch = repo.Branches[$"origin/{branchName}"];
            if (remoteBranch == null)
                throw new InvalidOperationException(
                    $"Remote branch 'origin/{branchName}' not found. " +
                    $"The branch may have been deleted.");

            var localBranch = repo.CreateBranch(branchName, remoteBranch.Tip);
            repo.Branches.Update(localBranch,
                b => b.TrackedBranch = remoteBranch.CanonicalName);
            Commands.Checkout(repo, localBranch, new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force
            });
        }, ct);
    }

    /// <inheritdoc />
    public Task CommitAllAsync(string workspacePath, string message, CancellationToken ct)
        => CommitAllAsync(workspacePath, message, null, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, CancellationToken ct)
        => CommitAllAsync(workspacePath, message, blacklistedPaths, allowEmpty: false, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(message);

        return Task.Run(() =>
        {
            using var repo = new Repository(workspacePath);

            var preStatus = repo.RetrieveStatus(new StatusOptions
            {
                DetectRenamesInIndex = false,
                DetectRenamesInWorkDir = false
            });

            var stagedAny = false;
            foreach (var entry in preStatus)
            {
                if (entry.State.HasFlag(FileStatus.Conflicted))
                {
                    repo.Index.Add(entry.FilePath);
                    stagedAny = true;
                }
                else if (entry.State.HasFlag(FileStatus.DeletedFromWorkdir))
                {
                    repo.Index.Remove(entry.FilePath);
                    stagedAny = true;
                }
                else if (entry.State.HasFlag(FileStatus.NewInWorkdir)
                    || entry.State.HasFlag(FileStatus.ModifiedInWorkdir)
                    || entry.State.HasFlag(FileStatus.RenamedInWorkdir)
                    || entry.State.HasFlag(FileStatus.TypeChangeInWorkdir))
                {
                    repo.Index.Add(entry.FilePath);
                    stagedAny = true;
                }
            }

            if (stagedAny)
                repo.Index.Write();

            var unstaged = new List<string>();
            if (blacklistedPaths is { Count: > 0 })
            {
                var indexChanges = repo.Diff.Compare<TreeChanges>(
                    repo.Head.Tip?.Tree, DiffTargets.Index);
                foreach (var change in indexChanges)
                {
                    if (Pipeline.Services.PipelineFormatting.IsPathBlacklisted(
                        change.Path, blacklistedPaths))
                    {
                        Commands.Unstage(repo, change.Path);
                        unstaged.Add(change.Path.Replace('\\', '/'));
                    }
                }
            }

            var stagedChanges = repo.Diff.Compare<TreeChanges>(
                repo.Head.Tip?.Tree, DiffTargets.Index);

            if (stagedChanges.Count == 0 && !allowEmpty)
                throw new InvalidOperationException(
                    "No changes to commit. The agent did not modify any files in the workspace.");

            var signature = new Signature(
                GitConstants.CommitAuthorName, GitConstants.CommitAuthorEmail, DateTimeOffset.UtcNow);
            var commitOptions = allowEmpty
                ? new CommitOptions { AllowEmptyCommit = true }
                : new CommitOptions();
            repo.Commit(message, signature, signature, commitOptions);

            return (IReadOnlyList<string>)unstaged;
        }, ct);
    }

    /// <inheritdoc />
    public Task PushBranchAsync(string workspacePath, string branchName, CancellationToken ct)
        => PushBranchAsync(workspacePath, branchName, forcePush: false, ct);

    /// <inheritdoc />
    public Task PushBranchAsync(string workspacePath, string branchName, bool forcePush, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);

            using var repo = new Repository(workspacePath);
            var remote = repo.Network.Remotes["origin"];
            string? pushError = null;
            var options = new PushOptions
            {
                CredentialsProvider = (_, _, _) =>
                    new UsernamePasswordCredentials
                    {
                        Username = GitConstants.GitLabTokenUsername,
                        Password = token
                    },
                OnPushStatusError = error =>
                    pushError = $"Push failed for ref '{error.Reference}': {error.Message}"
            };

            // Force-push uses '+' prefix on refspec to allow non-fast-forward updates
            var refSpec = forcePush
                ? $"+refs/heads/{branchName}"
                : $"refs/heads/{branchName}";

            if (forcePush)
                Log.Information("Force-pushing branch {BranchName} (post-rebase history rewrite)", branchName);

            await _gitPipeline.ExecuteAsync(async _ =>
            {
                await Task.CompletedTask;
                pushError = null;
                repo.Network.Push(remote, refSpec, options);

                if (pushError != null)
                {
                    var category = PushErrorClassifier.Classify(pushError);
                    var message = PushErrorClassifier.GetActionableMessage(category, branchName);
                    // Network and Unknown errors are potentially transient — throw LibGit2SharpException
                    // so the Polly resilience pipeline can retry them.
                    throw category is PushErrorClassifier.PushFailureCategory.Network
                                   or PushErrorClassifier.PushFailureCategory.Unknown
                        ? new LibGit2SharpException(pushError)
                        : new InvalidOperationException(message);
                }
            }, ct);
        }, ct);
    }

    /// <inheritdoc />
    public Task<string> GetHeadCommitShaAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(() =>
        {
            using var repo = new Repository(workspacePath);
            return repo.Head.Tip.Sha;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<bool> HasCommitsAheadAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return await _gitPipeline.ExecuteAsync(async _ =>
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var repo = new Repository(workspacePath);
                    var head = repo.Head.Tip;
                    var baseBranchRef = repo.Branches[$"origin/{_baseBranch}"]
                        ?? repo.Branches[_baseBranch];
                    if (baseBranchRef == null) return true;
                    var mergeBase = repo.ObjectDatabase.FindMergeBase(head, baseBranchRef.Tip);
                    return mergeBase == null || mergeBase.Sha != head.Sha;
                }
                catch
                {
                    return true; // On error, assume there are changes
                }
            }, ct);
        }, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(() =>
        {
            try
            {
                using var repo = new Repository(workspacePath);
                var baseBranchRef = repo.Branches[$"origin/{_baseBranch}"]
                    ?? repo.Branches[_baseBranch];
                if (baseBranchRef == null)
                    return (IReadOnlyList<FileChangeSummary>)Array.Empty<FileChangeSummary>();

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

                return (IReadOnlyList<FileChangeSummary>)changes;
            }
            catch
            {
                return (IReadOnlyList<FileChangeSummary>)Array.Empty<FileChangeSummary>();
            }
        }, ct);
    }

    /// <inheritdoc />
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

            // Fetch latest origin/baseBranch
            var remote = repo.Network.Remotes["origin"];
            var fetchOptions = new FetchOptions
            {
                CredentialsProvider = (_, _, _) =>
                    new UsernamePasswordCredentials
                    {
                        Username = GitConstants.GitLabTokenUsername,
                        Password = token
                    }
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
                    "Rebase: branch {BranchName} is already up-to-date with origin/{BaseBranch}, no rebase needed",
                    headBranchName, _baseBranch);
                return new MergeResult
                {
                    Success = true,
                    HasConflicts = false,
                    ConflictFiles = Array.Empty<string>()
                };
            }

            if (mergeBase != null)
            {
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
                    "Rebase: branch {BranchName} is {CommitsAhead} ahead, {CommitsBehind} behind origin/{BaseBranch}",
                    headBranchName, commitsAhead, commitsBehind, _baseBranch);
            }

            var identity = new Identity(GitConstants.CommitAuthorName, GitConstants.CommitAuthorEmail);
            var rebaseOptions = new RebaseOptions();
            var rebaseResult = repo.Rebase.Start(repo.Head, baseBranch, baseBranch, identity, rebaseOptions);

            if (rebaseResult.Status == RebaseStatus.Conflicts)
            {
                var conflictFiles = repo.Index.Conflicts
                    .Select(c => c.Ancestor?.Path ?? c.Ours?.Path ?? c.Theirs?.Path)
                    .Where(p => p != null)
                    .Distinct()
                    .ToList();

                Log.Warning(
                    "Rebase: branch {BranchName} onto origin/{BaseBranch} produced {ConflictCount} conflict(s). Force-resolving using incoming (main wins).",
                    headBranchName, _baseBranch, conflictFiles.Count);

                ForceResolveConflictsUsingTheirs(repo, workspacePath);

                var continueIdentity = new Identity(GitConstants.CommitAuthorName, GitConstants.CommitAuthorEmail);
                var continueResult = repo.Rebase.Continue(continueIdentity, new RebaseOptions());

                while (continueResult.Status == RebaseStatus.Conflicts)
                {
                    var additionalConflicts = repo.Index.Conflicts
                        .Select(c => c.Ancestor?.Path ?? c.Ours?.Path ?? c.Theirs?.Path)
                        .Where(p => p != null)
                        .Distinct()
                        .ToList();

                    foreach (var f in additionalConflicts.Where(f => !conflictFiles.Contains(f)))
                        conflictFiles.Add(f!);

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

    /// <inheritdoc />
    public Task<bool> EnsureAgentLabelsForPullRequestsAsync(CancellationToken ct)
    {
        // GitLab labels are project-scoped — issues and MRs share the same namespace.
        // No additional label creation needed for MRs.
        return Task.FromResult(true);
    }

    // ─── MR/Review Operations (implemented in GitLabRepositoryProvider.MergeRequests.cs) ───

    // ─── Private Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an authenticated clone URL from the cached <c>http_url_to_repo</c>.
    /// Format: <c>https://oauth2:{token}@{host}/{namespace}/{project}.git</c>
    /// </summary>
    private string BuildAuthenticatedCloneUrl(string token)
    {
        var httpUrl = HttpUrlToRepo;
        if (string.IsNullOrEmpty(httpUrl))
            throw new InvalidOperationException(
                "Clone URL not available. Call ValidateAsync before CloneAsync to populate project metadata.");

        // Insert oauth2:{token}@ into the URL after the scheme
        var uri = new Uri(httpUrl);
        return $"{uri.Scheme}://{GitConstants.GitLabTokenUsername}:{token}@{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}{uri.AbsolutePath}";
    }

    private static List<FileChangeSummary> CollectChangesWithLineStats(
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
                    var blob = repo.Lookup<Blob>(conflict.Theirs.Id);
                    if (blob != null)
                    {
                        var filePath = Path.Combine(
                            workspacePath,
                            conflict.Theirs.Path.Replace('/', Path.DirectorySeparatorChar));
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
                        var filePath = Path.Combine(
                            workspacePath,
                            pathToRemove.Replace('/', Path.DirectorySeparatorChar));
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

    private static string MapChangeKind(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Modified => "modified",
        ChangeKind.Renamed => "renamed",
        ChangeKind.Copied => "copied",
        ChangeKind.TypeChanged => "modified",
        _ => "modified"
    };
}
