using LibGit2Sharp;
using Octokit;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Signature = LibGit2Sharp.Signature;
using Repository = LibGit2Sharp.Repository;
using MergeResult = CodingAgentWebUI.Pipeline.Models.MergeResult;

namespace CodingAgentWebUI.Infrastructure.GitHub;

/// <summary>
/// Performs Git operations via LibGit2Sharp and PR creation via Octokit.
/// Supports both static token authentication (backward compatible) and
/// dynamic token provider delegate (for GitHub App auth).
/// </summary>
public class GitHubRepositoryProvider : GitHubProviderBase, IRepositoryProvider
{
    private readonly string _baseBranch;

    static GitHubRepositoryProvider()
    {
        // Disable libgit2's directory ownership validation. In Docker containers,
        // cloned workspace directories often have ownership mismatches (CVE-2022-24765
        // mitigation). Without this, Commands.Stage() silently fails.
        // See: https://github.com/libgit2/libgit2sharp/issues/2058
        //      https://stackoverflow.com/questions/76366963
        GlobalSettings.SetOwnerValidation(false);
    }

    public RepositoryProviderType ProviderType => RepositoryProviderType.GitHub;

    /// <inheritdoc />
    public string BaseBranch => _baseBranch;

    /// <inheritdoc />
    public string RepositoryFullName => $"{Owner}/{Repo}";

    /// <summary>
    /// Creates a provider with a static token (backward compatible).
    /// </summary>
    public GitHubRepositoryProvider(string apiUrl, string token, string owner, string repo, string baseBranch)
        : base(apiUrl, token, owner, repo)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
    }

    /// <summary>
    /// Creates a provider with a token provider delegate (for GitHub App auth).
    /// The delegate is called before each API call to obtain a fresh token.
    /// </summary>
    public GitHubRepositoryProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, string owner, string repo, string baseBranch)
        : base(apiUrl, tokenProvider, owner, repo)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitHubClient.
    /// </summary>
    internal GitHubRepositoryProvider(IGitHubClient gitHubClient, string token, string owner, string repo, string baseBranch)
        : base(gitHubClient, token, owner, repo)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
    }

    public Task CloneAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);

            // Derive clone URL: api.github.com → github.com, or GHE api base → GHE base
            var cloneBaseUrl = (ApiUrl ?? string.Empty).Replace("api.github.com", "github.com", StringComparison.OrdinalIgnoreCase);
            // For GHE: https://github.example.com/api/v3 → https://github.example.com
            if (cloneBaseUrl.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
                cloneBaseUrl = cloneBaseUrl[..^"/api/v3".Length];
            var cloneUrl = $"{cloneBaseUrl.TrimEnd('/')}/{Owner}/{Repo}.git";
            var options = new CloneOptions
            {
                BranchName = _baseBranch,
                FetchOptions =
                {
                    CredentialsProvider = (_, _, _) =>
                        new UsernamePasswordCredentials { Username = "x-access-token", Password = token }
                }
            };
            Repository.Clone(cloneUrl, workspacePath, options);
        }, ct);
    }

    public Task PullAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);

            using var repo = new Repository(workspacePath);
            var remote = repo.Network.Remotes["origin"];

            // Fetch
            var fetchOptions = new FetchOptions
            {
                CredentialsProvider = (_, _, _) =>
                    new UsernamePasswordCredentials
                        { Username = "x-access-token", Password = token }
            };
            var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
            Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, null);

            // Fast-forward merge only. If the merge is not fast-forward,
            // PullAsync returns without merging — the caller (BrainUpdateService)
            // owns conflict detection and resolution.
            var trackingBranch = repo.Head.TrackedBranch
                ?? repo.Branches[$"origin/{_baseBranch}"];
            if (trackingBranch != null)
            {
                var signature = new Signature(
                    "CodingAgentWebUI Pipeline", "pipeline@kiro.dev", DateTimeOffset.UtcNow);
                repo.Merge(trackingBranch, signature, new MergeOptions
                {
                    FastForwardStrategy = FastForwardStrategy.FastForwardOnly
                });
                // If merge results in conflicts, leave them in the index for the caller to resolve.
            }
        }, ct);
    }

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

    public Task CommitAllAsync(string workspacePath, string message, CancellationToken ct)
        => CommitAllAsync(workspacePath, message, null, ct);

    public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, CancellationToken ct)
        => CommitAllAsync(workspacePath, message, blacklistedPaths, allowEmpty: false, ct);

    /// <summary>
    /// Stages all changes, unstages blacklisted paths, and commits.
    /// When <paramref name="allowEmpty"/> is true, creates an empty commit if no files changed
    /// (useful for triggering CI re-runs after retry fixes that didn't change files).
    /// </summary>
    public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(message);

        return Task.Run(() =>
        {
            using var repo = new Repository(workspacePath);

            // Retrieve status — this reloads the on-disk index per libgit2sharp docs.
            var preStatus = repo.RetrieveStatus(new StatusOptions
            {
                DetectRenamesInIndex = false,
                DetectRenamesInWorkDir = false
            });

            // Diagnostic: log every entry and its state so we can trace staging issues
            foreach (var entry in preStatus)
            {
                Serilog.Log.Debug("CommitAllAsync status: {FilePath} = {State}", entry.FilePath, entry.State);
            }

            // Stage workdir changes using repo.Index.Add()/Remove() + repo.Index.Write().
            // Deleted files must use Index.Remove() — Index.Add() tries to stat the file
            // on disk and throws NotFoundException for files that no longer exist.
            // This matches the pattern in LibGit2Sharp's own Commands.Stage implementation.
            var stagedAny = false;
            foreach (var entry in preStatus)
            {
                if (entry.State.HasFlag(FileStatus.Conflicted))
                {
                    // Merge conflict resolved in working tree by the agent — stage to mark resolution.
                    Serilog.Log.Debug("CommitAllAsync staging conflicted (resolved) file via Index.Add: {FilePath}", entry.FilePath);
                    repo.Index.Add(entry.FilePath);
                    stagedAny = true;
                }
                else if (entry.State.HasFlag(FileStatus.DeletedFromWorkdir))
                {
                    Serilog.Log.Debug("CommitAllAsync staging deleted file via Index.Remove: {FilePath}", entry.FilePath);
                    repo.Index.Remove(entry.FilePath);
                    stagedAny = true;
                }
                else if (entry.State.HasFlag(FileStatus.NewInWorkdir)
                    || entry.State.HasFlag(FileStatus.ModifiedInWorkdir)
                    || entry.State.HasFlag(FileStatus.RenamedInWorkdir)
                    || entry.State.HasFlag(FileStatus.TypeChangeInWorkdir))
                {
                    Serilog.Log.Debug("CommitAllAsync staging workdir file via Index.Add: {FilePath}", entry.FilePath);
                    repo.Index.Add(entry.FilePath);
                    stagedAny = true;
                }
            }

            if (stagedAny)
                repo.Index.Write();

            // Enforce blacklist: unstage any staged files matching blacklisted path prefixes.
            // Use Diff.Compare against the index to reliably detect staged changes,
            // since RepositoryStatus.Staged can miss NewInIndex files.
            var unstaged = new List<string>();
            if (blacklistedPaths is { Count: > 0 })
            {
                var indexChanges = repo.Diff.Compare<TreeChanges>(repo.Head.Tip?.Tree, DiffTargets.Index);
                foreach (var change in indexChanges)
                {
                    if (CodingAgentWebUI.Pipeline.Services.PipelineFormatting.IsPathBlacklisted(change.Path, blacklistedPaths))
                    {
                        Commands.Unstage(repo, change.Path);
                        unstaged.Add(change.Path.Replace('\\', '/'));
                    }
                }
            }

            // Use Diff.Compare to reliably detect staged changes (index vs HEAD).
            // RepositoryStatus.Staged does not include NewInIndex files.
            var stagedChanges = repo.Diff.Compare<TreeChanges>(repo.Head.Tip?.Tree, DiffTargets.Index);
            Serilog.Log.Debug("CommitAllAsync final staged count (via Diff): {Count}", stagedChanges.Count);
            foreach (var change in stagedChanges)
            {
                Serilog.Log.Debug("CommitAllAsync final staged: {FilePath} = {Status}", change.Path, change.Status);
            }

            if (stagedChanges.Count == 0 && !allowEmpty)
                throw new InvalidOperationException("No changes to commit. The agent did not modify any files in the workspace.");

            var signature = new Signature("CodingAgentWebUI Pipeline", "pipeline@kiro.dev", DateTimeOffset.UtcNow);
            // NOTE: [ARC-07a] The allowEmpty path is currently only exercised by the retry loop's empty commit
            // for CI re-trigger. Add an integration test with a real LibGit2Sharp repo to verify AllowEmptyCommit works.
            var commitOptions = allowEmpty ? new CommitOptions { AllowEmptyCommit = true } : new CommitOptions();
            repo.Commit(message, signature, signature, commitOptions);

            return (IReadOnlyList<string>)unstaged;
        }, ct);
    }

    public Task PushBranchAsync(string workspacePath, string branchName, CancellationToken ct)
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
                    new UsernamePasswordCredentials { Username = "x-access-token", Password = token },
                OnPushStatusError = error =>
                    pushError = $"Push failed for ref '{error.Reference}': {error.Message}"
            };
            repo.Network.Push(remote, $"refs/heads/{branchName}", options);

            if (pushError != null)
                throw new InvalidOperationException(pushError);
        }, ct);
    }

    public async Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prInfo);

        var client = await GetClientAsync(ct);

        var newPr = new NewPullRequest(prInfo.Title, prInfo.BranchName, prInfo.BaseBranch)
        {
            Body = prInfo.Body,
            Draft = prInfo.IsDraft
        };

        var pr = await ExecuteWithRateLimitHandlingAsync(
            () => client.PullRequest.Create(Owner, Repo, newPr));
        return pr.HtmlUrl;
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
    public Task<bool> HasCommitsAheadAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(() =>
        {
            try
            {
                using var repo = new Repository(workspacePath);
                var head = repo.Head.Tip;
                var baseBranchRef = repo.Branches[$"origin/{_baseBranch}"]
                    ?? repo.Branches[_baseBranch];
                if (baseBranchRef == null) return true; // Can't determine — assume there are changes
                var mergeBase = repo.ObjectDatabase.FindMergeBase(head, baseBranchRef.Tip);
                return mergeBase == null || mergeBase.Sha != head.Sha;
            }
            catch
            {
                return true; // On error, assume there are changes and let PR creation decide
            }
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

                // If no committed changes yet, check the working directory against the base branch.
                // This captures files the agent has written but not yet committed.
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
            // Fall back to TreeChanges if Patch fails (e.g. binary files)
            var diff = repo.Diff.Compare<TreeChanges>(baseTree, headTree);
            foreach (var entry in diff)
            {
                var status = MapChangeKind(entry.Status);
                changes.Add(new FileChangeSummary(status, entry.Path));
            }
        }
        return changes;
    }

    public async Task<IReadOnlyList<LinkedPullRequest>> GetAgentPullRequestsAsync(
        string issueIdentifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        var client = await GetClientAsync(ct);
        var branchPrefix = $"feature/auto-{issueIdentifier}-";

        // 1. List all open PRs for the repository
        var allPrs = await ExecuteWithRateLimitHandlingAsync(
            () => client.PullRequest.GetAllForRepository(
                Owner, Repo,
                new PullRequestRequest { State = ItemStateFilter.Open }));

        // 2. Filter by branch name prefix (client-side)
        var matching = allPrs
            .Where(pr => pr.Head.Ref.StartsWith(branchPrefix, StringComparison.Ordinal))
            .ToList();

        if (matching.Count == 0)
            return Array.Empty<LinkedPullRequest>();

        // 3. For each match, fetch individual PR to get Mergeable field
        var results = new List<LinkedPullRequest>();
        foreach (var pr in matching)
        {
            var detailed = await ExecuteWithRateLimitHandlingAsync(
                () => client.PullRequest.Get(Owner, Repo, pr.Number));

            // 4. Fetch review comments (inline + conversation), filter by content markers, cap at 50
            var reviewComments = await ExecuteWithRateLimitHandlingAsync(
                () => client.PullRequest.ReviewComment.GetAll(Owner, Repo, pr.Number));
            var conversationComments = await ExecuteWithRateLimitHandlingAsync(
                () => client.Issue.Comment.GetAllForIssue(Owner, Repo, pr.Number));

            var allComments = reviewComments
                .Where(c => !IsPipelineGeneratedComment(c.Body))
                .Select(c => new Pipeline.Models.PullRequestReviewComment
                {
                    Id = c.Id.ToString(),
                    Body = c.Body ?? string.Empty,
                    Author = c.User?.Login ?? string.Empty,
                    CreatedAt = c.CreatedAt.UtcDateTime,
                    Path = c.Path
                })
                .Concat(conversationComments
                    .Where(c => !IsPipelineGeneratedComment(c.Body))
                    .Select(c => new Pipeline.Models.PullRequestReviewComment
                    {
                        Id = c.Id.ToString(),
                        Body = c.Body ?? string.Empty,
                        Author = c.User?.Login ?? string.Empty,
                        CreatedAt = c.CreatedAt.UtcDateTime,
                        Path = null
                    }))
                .OrderBy(c => c.CreatedAt)
                .Take(50)
                .ToList();

            results.Add(new LinkedPullRequest
            {
                Number = detailed.Number,
                BranchName = detailed.Head.Ref,
                Url = detailed.HtmlUrl,
                IsDraft = detailed.Draft,
                IsMergeable = detailed.Mergeable,
                ReviewComments = allComments
            });
        }

        return results;
    }

    /// <summary>
    /// Checks if a comment was generated by the pipeline using content markers.
    /// Reuses the same pattern as PromptBuilder.ExcludedCommentMarkers.
    /// </summary>
    private static bool IsPipelineGeneratedComment(string? body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        return body.StartsWith("## 🤖", StringComparison.Ordinal)
            || body.Contains("<!-- agent:");
    }

    public Task CheckoutRemoteBranchAsync(string workspacePath, string branchName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(() =>
        {
            using var repo = new Repository(workspacePath);

            // After CloneAsync, all remote tracking branches are available
            var remoteBranch = repo.Branches[$"origin/{branchName}"];
            if (remoteBranch == null)
                throw new InvalidOperationException(
                    $"Remote branch 'origin/{branchName}' not found. " +
                    $"The branch may have been deleted.");

            // Create local branch tracking the remote
            var localBranch = repo.CreateBranch(branchName, remoteBranch.Tip);
            repo.Branches.Update(localBranch,
                b => b.TrackedBranch = remoteBranch.CanonicalName);
            Commands.Checkout(repo, localBranch);
        }, ct);
    }

    public Task<MergeResult> MergeFromBaseAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(() =>
        {
            using var repo = new Repository(workspacePath);

            var baseBranch = repo.Branches[$"origin/{_baseBranch}"]
                ?? repo.Branches[_baseBranch];
            if (baseBranch == null)
                throw new InvalidOperationException(
                    $"Base branch '{_baseBranch}' not found.");

            var signature = new Signature(
                "CodingAgentWebUI Pipeline", "pipeline@kiro.dev", DateTimeOffset.UtcNow);

            var mergeResult = repo.Merge(baseBranch, signature, new MergeOptions
            {
                FileConflictStrategy = CheckoutFileConflictStrategy.Merge
            });

            if (mergeResult.Status == MergeStatus.Conflicts)
            {
                var conflictFiles = repo.Index.Conflicts
                    .Select(c => c.Ancestor?.Path ?? c.Ours?.Path ?? c.Theirs?.Path)
                    .Where(p => p != null)
                    .Distinct()
                    .ToList();

                return new MergeResult
                {
                    Success = false,
                    HasConflicts = true,
                    ConflictFiles = conflictFiles!
                };
            }

            return new MergeResult
            {
                Success = true,
                HasConflicts = false,
                ConflictFiles = Array.Empty<string>()
            };
        }, ct);
    }

    public async Task UpdatePullRequestAsync(int pullRequestNumber, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var client = await GetClientAsync(ct);
        try
        {
            await ExecuteWithRateLimitHandlingAsync(
                () => client.PullRequest.Update(Owner, Repo, pullRequestNumber,
                    new PullRequestUpdate { Body = body }));
        }
        catch (Octokit.NotFoundException ex)
        {
            throw new InvalidOperationException(
                $"Pull request #{pullRequestNumber} not found in {Owner}/{Repo}.", ex);
        }
    }

    private static string MapChangeKind(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "Added",
        ChangeKind.Deleted => "Deleted",
        ChangeKind.Renamed => "Renamed",
        _ => "Modified"
    };
}
