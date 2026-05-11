using LibGit2Sharp;
using Octokit;
using Polly;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
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
    private readonly ResiliencePipeline _gitPipeline;

    static GitHubRepositoryProvider()
    {
        // Disable libgit2's directory ownership validation. In Docker containers,
        // cloned workspace directories often have ownership mismatches (CVE-2022-24765
        // mitigation). Without this, Commands.Stage() silently fails.
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
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    /// <summary>
    /// Creates a provider with a token provider delegate (for GitHub App auth).
    /// </summary>
    public GitHubRepositoryProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, string owner, string repo, string baseBranch)
        : base(apiUrl, tokenProvider, owner, repo)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitHubClient.
    /// </summary>
    internal GitHubRepositoryProvider(IGitHubClient gitHubClient, string token, string owner, string repo, string baseBranch)
        : base(gitHubClient, token, owner, repo)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    public Task CloneAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);

            // Derive clone URL
            var cloneBaseUrl = (ApiUrl ?? string.Empty).Replace("api.github.com", "github.com", StringComparison.OrdinalIgnoreCase);
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

            await _gitPipeline.ExecuteAsync(async _ =>
            {
                await Task.CompletedTask;
                Repository.Clone(cloneUrl, workspacePath, options);
            }, ct);
        }, ct);
    }

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
                            { Username = "x-access-token", Password = token }
                };
                var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
                Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, null);

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
                }
            }, ct);
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
    /// </summary>
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

            foreach (var entry in preStatus)
            {
                Serilog.Log.Debug("CommitAllAsync status: {FilePath} = {State}", entry.FilePath, entry.State);
            }

            var stagedAny = false;
            foreach (var entry in preStatus)
            {
                if (entry.State.HasFlag(FileStatus.Conflicted))
                {
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

            var stagedChanges = repo.Diff.Compare<TreeChanges>(repo.Head.Tip?.Tree, DiffTargets.Index);
            Serilog.Log.Debug("CommitAllAsync final staged count (via Diff): {Count}", stagedChanges.Count);
            foreach (var change in stagedChanges)
            {
                Serilog.Log.Debug("CommitAllAsync final staged: {FilePath} = {Status}", change.Path, change.Status);
            }

            if (stagedChanges.Count == 0 && !allowEmpty)
                throw new InvalidOperationException("No changes to commit. The agent did not modify any files in the workspace.");

            var signature = new Signature("CodingAgentWebUI Pipeline", "pipeline@kiro.dev", DateTimeOffset.UtcNow);
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

            await _gitPipeline.ExecuteAsync(async _ =>
            {
                await Task.CompletedTask;
                pushError = null;
                repo.Network.Push(remote, $"refs/heads/{branchName}", options);

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

    public async Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prInfo);

        var newPr = new NewPullRequest(prInfo.Title, prInfo.BranchName, prInfo.BaseBranch)
        {
            Body = prInfo.Body,
            Draft = prInfo.IsDraft
        };

        var pr = await ExecuteWithResilienceAsync(
            client => client.PullRequest.Create(Owner, Repo, newPr),
            "CreatePullRequest", ct);
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
    // TODO: [RES-07] HasCommitsAheadAsync (divergence check) is not wrapped with _gitPipeline.
    // FindMergeBase is a local operation on already-fetched data, but the acceptance criteria
    // names it as one of the 5 LibGit2Sharp network operations to wrap.
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
                if (baseBranchRef == null) return true;
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
        var branchPrefix = $"feature/auto-{issueIdentifier}-";

        // 1. List all open PRs for the repository
        var allPrs = await ExecuteWithResilienceAsync(
            client => client.PullRequest.GetAllForRepository(
                Owner, Repo,
                new PullRequestRequest { State = ItemStateFilter.Open }),
            "GetAgentPullRequests.List", ct);

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
            var detailed = await ExecuteWithResilienceAsync(
                client => client.PullRequest.Get(Owner, Repo, pr.Number),
                "GetAgentPullRequests.Get", ct);

            // 4. Fetch review comments
            var reviewComments = await ExecuteWithResilienceAsync(
                client => client.PullRequest.ReviewComment.GetAll(Owner, Repo, pr.Number),
                "GetAgentPullRequests.ReviewComments", ct);
            var conversationComments = await ExecuteWithResilienceAsync(
                client => client.Issue.Comment.GetAllForIssue(Owner, Repo, pr.Number),
                "GetAgentPullRequests.ConversationComments", ct);

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

            var remoteBranch = repo.Branches[$"origin/{branchName}"];
            if (remoteBranch == null)
                throw new InvalidOperationException(
                    $"Remote branch 'origin/{branchName}' not found. " +
                    $"The branch may have been deleted.");

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

    public async Task UpdatePullRequestAsync(int pullRequestNumber, string body, bool markReady, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            await ExecuteWithResilienceAsync(
                client => client.PullRequest.Update(Owner, Repo, pullRequestNumber,
                    new PullRequestUpdate { Body = body }),
                "UpdatePullRequest", ct);

            // Mark as ready for review if requested.
            // GitHub REST API doesn't support changing draft status — requires GraphQL mutation.
            if (markReady)
            {
                try
                {
                    var pr = await ExecuteWithResilienceAsync(
                        client => client.PullRequest.Get(Owner, Repo, pullRequestNumber),
                        "GetPullRequestForDraftCheck", ct);

                    if (pr.Draft)
                    {
                        var client = await GetClientAsync(ct);
                        var graphqlBody = $"{{\"query\":\"mutation {{ markPullRequestReadyForReview(input: {{pullRequestId: \\\"{pr.NodeId}\\\"}}) {{ pullRequest {{ isDraft }} }} }}\"}}";
                        await client.Connection.Post<object>(
                            new Uri("https://api.github.com/graphql"),
                            graphqlBody,
                            "application/json",
                            "application/json");
                        Log.Information("Marked PR #{PrNumber} as ready for review", pullRequestNumber);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "Failed to mark PR #{PrNumber} as ready for review (non-fatal)", pullRequestNumber);
                }
            }
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
