using LibGit2Sharp;
using Octokit;
using Polly;
using System.Text.RegularExpressions;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline;
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
    private string? _cachedBotLogin;

    // Static compiled regex patterns for ParseIssueReferences (avoid per-call allocation)
    private static readonly Regex ClosingKeywordPattern = new(
        @"(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+(?:#|GH-)(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CrossRepoPattern = new(
        @"[\w\-\.]+/[\w\-\.]+#(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GhPattern = new(
        @"\bGH-(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SimpleHashPattern = new(
        @"(?<![&\w/])#(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    /// <inheritdoc />
    public bool SupportsInlineReviewComments => true;

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
                        new UsernamePasswordCredentials { Username = GitConstants.TokenUsername, Password = token }
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
                            { Username = GitConstants.TokenUsername, Password = token }
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

            var signature = new Signature(GitConstants.CommitAuthorName, GitConstants.CommitAuthorEmail, DateTimeOffset.UtcNow);
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
                    new UsernamePasswordCredentials { Username = GitConstants.TokenUsername, Password = token },
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
                    return true; // On error, assume there are changes and let PR creation decide
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
        var branchPrefix = $"{PipelineConstants.BranchPrefix}{issueIdentifier}-";

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
        return body.StartsWith(CommentMarkers.PipelinePrefix, StringComparison.Ordinal)
            || body.Contains(CommentMarkers.AgentCommentPrefix);
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
            Commands.Checkout(repo, localBranch, new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force
            });
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
                GitConstants.CommitAuthorName, GitConstants.CommitAuthorEmail, DateTimeOffset.UtcNow);

            var mergeResult = repo.Merge(baseBranch, signature, new MergeOptions
            {
                FileConflictStrategy = CheckoutFileConflictStrategy.Merge
            });

            if (mergeResult.Status == MergeStatus.Conflicts)
            {
                AutoResolveRenameDeleteConflicts(repo, workspacePath);

                // Re-check: are there still unresolved (textual) conflicts?
                var remainingConflicts = repo.Index.Conflicts
                    .Select(c => c.Ancestor?.Path ?? c.Ours?.Path ?? c.Theirs?.Path)
                    .Where(p => p != null)
                    .Distinct()
                    .ToList();

                if (remainingConflicts.Count > 0)
                {
                    return new MergeResult
                    {
                        Success = false,
                        HasConflicts = true,
                        ConflictFiles = remainingConflicts!
                    };
                }

                // All conflicts were rename/delete — auto-resolved. Commit the merge.
                repo.Commit(
                    $"Merge {_baseBranch} (auto-resolved rename/delete conflicts)",
                    signature, signature);

                return new MergeResult
                {
                    Success = true,
                    HasConflicts = false,
                    ConflictFiles = Array.Empty<string>()
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

    /// <inheritdoc />
    public async Task<PagedResult<PullRequestSummary>> ListOpenPullRequestsAsync(
        int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);

        // GitHub's PR list API doesn't support label filtering directly.
        // Use the Issues API (PRs are issues on GitHub) with label filtering,
        // then fetch full PR details for items that are pull requests.
        var request = new RepositoryIssueRequest
        {
            State = ItemStateFilter.Open
        };

        if (labels is { Count: > 0 })
        {
            foreach (var label in labels)
                request.Labels.Add(label);
        }

        var apiOptions = new ApiOptions
        {
            PageSize = pageSize + 1, // fetch one extra to detect HasMore
            StartPage = page,
            PageCount = 1
        };

        var issues = await ExecuteWithResilienceAsync(
            client => client.Issue.GetAllForRepository(Owner, Repo, request, apiOptions),
            "ListOpenPullRequests", ct);

        // Base HasMore on raw issues count (before PR filtering) to avoid early termination
        var hasMore = issues.Count > pageSize;

        // Filter to only pull requests (opposite of issue provider which filters them out)
        var prIssues = issues
            .Where(i => i.PullRequest != null)
            .Take(pageSize)
            .ToList();

        // Fetch full PR details for each matching issue to get Draft, Head, Base info
        var items = new List<PullRequestSummary>();
        foreach (var issue in prIssues)
        {
            var pr = await ExecuteWithResilienceAsync(
                client => client.PullRequest.Get(Owner, Repo, issue.Number),
                "ListOpenPullRequests.GetDetail", ct);

            items.Add(new PullRequestSummary
            {
                Number = pr.Number,
                Identifier = pr.Number.ToString(),
                Title = pr.Title,
                Description = pr.Body ?? string.Empty,
                Labels = pr.Labels.Select(l => l.Name).ToArray(),
                BranchName = pr.Head.Ref,
                TargetBranch = pr.Base.Ref,
                Url = pr.HtmlUrl,
                IsDraft = pr.Draft,
                CreatedAt = pr.CreatedAt.UtcDateTime
            });
        }

        return new PagedResult<PullRequestSummary>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        };
    }

    /// <inheritdoc />
    public async Task SubmitPullRequestReviewAsync(
        int prNumber, string body, PullRequestReviewType type, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Use the Pull Request Reviews API so that reviews are dismissible
        // and support inline comments in future overloads.
        var review = new Octokit.PullRequestReviewCreate
        {
            Body = body,
            Event = MapReviewEvent(type)
        };

        await ExecuteWithResilienceAsync(
            client => client.PullRequest.Review.Create(Owner, Repo, prNumber, review),
            "SubmitPullRequestReview", ct);
    }

    /// <inheritdoc />
    public async Task SubmitPullRequestReviewAsync(
        int prNumber, ReviewSubmission submission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // When Comments is empty, delegate to the existing body-only overload
        // to produce the same observable result.
        if (submission.Comments.Count == 0)
        {
            await SubmitPullRequestReviewAsync(prNumber, submission.Body, submission.Type, ct);
            return;
        }

        // Build the review payload with inline comments using the raw API,
        // because Octokit's DraftPullRequestReviewComment doesn't support 'line' and 'side' fields.
        var comments = submission.Comments.Select(c => new
        {
            path = c.Path,
            line = c.Line,
            side = c.Side == DiffSide.Left ? "LEFT" : "RIGHT",
            body = c.Body
        }).ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["body"] = submission.Body,
            ["event"] = MapReviewEventString(submission.Type),
            ["comments"] = comments
        };

        if (submission.CommitId is not null)
        {
            payload["commit_id"] = submission.CommitId;
        }

        try
        {
            await ExecuteWithResilienceAsync(
                async client =>
                {
                    var url = new Uri($"repos/{Owner}/{Repo}/pulls/{prNumber}/reviews", UriKind.Relative);
                    await client.Connection.Post<object>(url, payload, "application/json", null);
                    return true;
                },
                "SubmitPullRequestReviewWithComments", ct);
        }
        catch (ApiValidationException)
        {
            // On HTTP 422, retry once without any comments (body-only fallback).
            // GitHub's 422 response doesn't reliably identify which comment failed.
            Log.Warning(
                "GitHub returned 422 when submitting review with {CommentCount} inline comments on PR #{PrNumber}. " +
                "Retrying with body-only fallback.",
                submission.Comments.Count, prNumber);

            await SubmitPullRequestReviewAsync(prNumber, submission.Body, submission.Type, ct);
        }
    }

    /// <inheritdoc />
    public async Task DismissPreviousReviewAsync(int prNumber, string marker, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(reason);

        // NOTE: This method only finds reviews posted via the Pull Request Reviews API (spec 026+).
        // Old issue-comment-based reviews (from spec 025, pre-migration) are NOT found here —
        // they remain as-is with their <!-- agent:pr-review-superseded --> collapse markers.
        // This is acceptable per the design doc (migration edge case).

        // Get the authenticated bot login to identify our own reviews (cached).
        if (_cachedBotLogin is null)
        {
            var currentUser = await ExecuteWithResilienceAsync(
                client => client.User.Current(),
                "DismissPreviousReview.GetCurrentUser", ct);
            _cachedBotLogin = currentUser.Login;
        }
        var botLogin = _cachedBotLogin;

        // Get all reviews on the PR. Octokit's GetAll handles pagination automatically.
        var allReviews = await ExecuteWithResilienceAsync(
            client => client.PullRequest.Review.GetAll(Owner, Repo, prNumber),
            "DismissPreviousReview.GetAllReviews", ct);

        // Filter reviews authored by the bot that contain the marker in their body.
        var matchingReviews = allReviews
            .Where(r => string.Equals(r.User.Login, botLogin, StringComparison.OrdinalIgnoreCase)
                        && r.Body?.Contains(marker, StringComparison.Ordinal) == true)
            .ToList();

        if (matchingReviews.Count == 0)
        {
            return; // No-op when no matching reviews found.
        }

        Log.Information(
            "Found {Count} previous review(s) to dismiss on PR #{PrNumber}",
            matchingReviews.Count, prNumber);

        // Dismiss each matching review. Log warning and continue on individual failures.
        foreach (var review in matchingReviews)
        {
            try
            {
                await ExecuteWithResilienceAsync(
                    async client =>
                    {
                        var url = new Uri(
                            $"repos/{Owner}/{Repo}/pulls/{prNumber}/reviews/{review.Id}/dismissals",
                            UriKind.Relative);
                        var payload = new { message = reason, @event = "DISMISS" };
                        await client.Connection.Put<object>(url, payload);
                        return true;
                    },
                    $"DismissPreviousReview.Dismiss({review.Id})", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(
                    ex,
                    "Failed to dismiss review {ReviewId} on PR #{PrNumber}. Continuing with remaining reviews.",
                    review.Id, prNumber);
            }
        }
    }

    /// <summary>
    /// Maps the pipeline's <see cref="PullRequestReviewType"/> to the GitHub API event string.
    /// </summary>
    private static string MapReviewEventString(PullRequestReviewType type) => type switch
    {
        PullRequestReviewType.Comment => "COMMENT",
        PullRequestReviewType.RequestChanges => "REQUEST_CHANGES",
        _ => "COMMENT"
    };

    /// <summary>
    /// Maps the pipeline's <see cref="PullRequestReviewType"/> to Octokit's <see cref="Octokit.PullRequestReviewEvent"/>.
    /// </summary>
    private static Octokit.PullRequestReviewEvent MapReviewEvent(PullRequestReviewType type) => type switch
    {
        PullRequestReviewType.Comment => Octokit.PullRequestReviewEvent.Comment,
        PullRequestReviewType.RequestChanges => Octokit.PullRequestReviewEvent.RequestChanges,
        _ => Octokit.PullRequestReviewEvent.Comment
    };

    /// <inheritdoc />
    public async Task<long?> FindExistingReviewCommentAsync(int prNumber, string marker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(marker);

        var comments = await ExecuteWithResilienceAsync(
            client => client.Issue.Comment.GetAllForIssue(Owner, Repo, prNumber),
            "FindExistingReviewComment", ct);

        var match = comments.FirstOrDefault(c => c.Body?.Contains(marker, StringComparison.Ordinal) == true);
        return match?.Id;
    }

    /// <inheritdoc />
    public async Task UpdateReviewCommentAsync(int prNumber, long commentId, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        await ExecuteWithResilienceAsync(
            async client =>
            {
                // Use the raw Connection API to avoid Octokit's int limitation on comment IDs.
                // GitHub comment IDs can exceed int.MaxValue on active repositories.
                var url = new Uri($"repos/{Owner}/{Repo}/issues/comments/{commentId}", UriKind.Relative);
                var payload = new { body };
                await client.Connection.Patch<object>(url, payload);
                return true;
            },
            "UpdateReviewComment", ct);
    }

    /// <inheritdoc />
    public async Task AddPrLabelAsync(int prNumber, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(label);

        await ExecuteWithResilienceAsync(
            client => client.Issue.Labels.AddToIssue(Owner, Repo, prNumber, new[] { label }),
            "AddPrLabel", ct);
    }

    /// <inheritdoc />
    public async Task RemovePrLabelAsync(int prNumber, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(label);

        try
        {
            await ExecuteWithResilienceAsync(
                async client => { await client.Issue.Labels.RemoveFromIssue(Owner, Repo, prNumber, label); return true; },
                "RemovePrLabel", ct);
        }
        catch (Octokit.NotFoundException)
        {
            // Label not present on PR — no-op
        }
    }

    /// <inheritdoc />
    public Task<bool> EnsureAgentLabelsForPullRequestsAsync(CancellationToken ct)
    {
        // On GitHub, PRs share the issues label namespace — labels created for issues
        // are already available for PRs. No additional setup needed.
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ExtractLinkedIssuesAsync(int prNumber, CancellationToken ct)
    {
        var issueNumbers = new HashSet<string>(StringComparer.Ordinal);

        // Priority (a): Try GitHub timeline events API for closing references
        try
        {
            var events = await ExecuteWithResilienceAsync(
                client => client.Issue.Timeline.GetAllForIssue(Owner, Repo, prNumber),
                "ExtractLinkedIssues.Timeline", ct);

            foreach (var evt in events)
            {
                // Look for cross-referenced events that indicate closing references
                if (evt.Event == EventInfoState.Crossreferenced && evt.Source?.Issue != null)
                {
                    issueNumbers.Add(evt.Source.Issue.Number.ToString());
                }
            }

            if (issueNumbers.Count > 0)
            {
                // API found results — still parse title/body for additional references
                // that may not appear in timeline events (e.g., "Related to #42" without closing keyword).
                // The HashSet deduplicates across all sources.
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Failed to extract linked issues via timeline API for PR #{PrNumber}, falling back to parsing", prNumber);
        }

        // Priority (b) and (c): Parse PR title and body for issue references
        var pr = await ExecuteWithResilienceAsync(
            client => client.PullRequest.Get(Owner, Repo, prNumber),
            "ExtractLinkedIssues.GetPr", ct);

        // Parse title first (priority b), then body (priority c)
        ParseIssueReferences(pr.Title, issueNumbers);
        ParseIssueReferences(pr.Body, issueNumbers);

        return issueNumbers.ToList().AsReadOnly();
    }

    /// <summary>
    /// Parses a text string for GitHub issue reference patterns and adds found issue numbers to the set.
    /// Recognizes: #N, owner/repo#N, GH-N, closes #N, fixes #N, resolves #N (case-insensitive).
    /// </summary>
    internal static void ParseIssueReferences(string? text, HashSet<string> issueNumbers)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        // Extract from closing keywords first
        foreach (Match match in ClosingKeywordPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }

        // Extract from cross-repo references
        foreach (Match match in CrossRepoPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }

        // Extract from GH-N references
        foreach (Match match in GhPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }

        // Extract from simple #N references
        foreach (Match match in SimpleHashPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
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
