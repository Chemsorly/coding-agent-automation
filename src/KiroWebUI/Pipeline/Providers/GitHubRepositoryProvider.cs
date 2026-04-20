using LibGit2Sharp;
using Octokit;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;using Signature = LibGit2Sharp.Signature;
using Repository = LibGit2Sharp.Repository;

namespace KiroWebUI.Pipeline.Providers;

/// <summary>
/// Performs Git operations via LibGit2Sharp and PR creation via Octokit.
/// Supports both static token authentication (backward compatible) and
/// dynamic token provider delegate (for GitHub App auth).
/// </summary>
public class GitHubRepositoryProvider : IRepositoryProvider
{
    private readonly GitHubClientProvider _clientProvider;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _baseBranch;

    public RepositoryProviderType ProviderType => RepositoryProviderType.GitHub;

    /// <inheritdoc />
    public string BaseBranch => _baseBranch;

    /// <inheritdoc />
    public string RepositoryFullName => $"{_owner}/{_repo}";

    /// <summary>
    /// Creates a provider with a static token (backward compatible).
    /// </summary>
    public GitHubRepositoryProvider(string apiUrl, string token, string owner, string repo, string baseBranch)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(baseBranch);

        _clientProvider = new GitHubClientProvider(apiUrl, token);
        _owner = owner;
        _repo = repo;
        _baseBranch = baseBranch;
    }

    /// <summary>
    /// Creates a provider with a token provider delegate (for GitHub App auth).
    /// The delegate is called before each API call to obtain a fresh token.
    /// </summary>
    public GitHubRepositoryProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, string owner, string repo, string baseBranch)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(baseBranch);

        _clientProvider = new GitHubClientProvider(apiUrl, tokenProvider);
        _owner = owner;
        _repo = repo;
        _baseBranch = baseBranch;
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitHubClient.
    /// </summary>
    internal GitHubRepositoryProvider(IGitHubClient gitHubClient, string token, string owner, string repo, string baseBranch)
    {
        ArgumentNullException.ThrowIfNull(gitHubClient);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(baseBranch);

        _clientProvider = new GitHubClientProvider(gitHubClient, token);
        _owner = owner;
        _repo = repo;
        _baseBranch = baseBranch;
    }

    public Task CloneAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);

            // Derive clone URL: api.github.com → github.com, or GHE api base → GHE base
            var cloneBaseUrl = (_clientProvider.ApiUrl ?? string.Empty).Replace("api.github.com", "github.com", StringComparison.OrdinalIgnoreCase);
            // For GHE: https://github.example.com/api/v3 → https://github.example.com
            if (cloneBaseUrl.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
                cloneBaseUrl = cloneBaseUrl[..^"/api/v3".Length];
            var cloneUrl = $"{cloneBaseUrl.TrimEnd('/')}/{_owner}/{_repo}.git";
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
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(message);

        return Task.Run(() =>
        {
            using var repo = new Repository(workspacePath);

            // Stage all changes from the working directory. We first retrieve status
            // to find all modified/new/deleted files, then stage them individually.
            // This is more reliable than Commands.Stage(repo, "*") when the index
            // has been modified externally (e.g., by the agent's CLI git add).
            var preStatus = repo.RetrieveStatus(new StatusOptions
            {
                DetectRenamesInIndex = false,
                DetectRenamesInWorkDir = false
            });

            foreach (var entry in preStatus)
            {
                // Stage any file that has working directory changes (new, modified, deleted)
                if (entry.State.HasFlag(FileStatus.NewInWorkdir)
                    || entry.State.HasFlag(FileStatus.ModifiedInWorkdir)
                    || entry.State.HasFlag(FileStatus.DeletedFromWorkdir)
                    || entry.State.HasFlag(FileStatus.RenamedInWorkdir)
                    || entry.State.HasFlag(FileStatus.TypeChangeInWorkdir))
                {
                    Commands.Stage(repo, entry.FilePath);
                }
            }

            // Also run the broad stage to catch anything the per-file approach missed
            Commands.Stage(repo, "*");

            // Enforce blacklist: unstage any staged files matching blacklisted path prefixes
            var unstaged = new List<string>();
            if (blacklistedPaths is { Count: > 0 })
            {
                var status = repo.RetrieveStatus();
                var stagedFiles = status.Staged
                    .Select(e => e.FilePath)
                    .ToList();

                foreach (var filePath in stagedFiles)
                {
                    if (Services.PipelineFormatting.IsPathBlacklisted(filePath, blacklistedPaths))
                    {
                        Commands.Unstage(repo, filePath);
                        unstaged.Add(filePath.Replace('\\', '/'));
                    }
                }
            }

            // Check if there are any staged changes left after blacklist filtering
            var finalStatus = repo.RetrieveStatus();
            if (finalStatus.Staged.Count() == 0)
                throw new InvalidOperationException("No changes to commit. The agent did not modify any files in the workspace.");

            var signature = new Signature("KiroWebUI Pipeline", "pipeline@kiro.dev", DateTimeOffset.UtcNow);
            repo.Commit(message, signature, signature);

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

        var pr = await client.PullRequest.Create(_owner, _repo, newPr);
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
                var diff = repo.Diff.Compare<TreeChanges>(baseCommit.Tree, headCommit.Tree);

                var changes = new List<FileChangeSummary>();
                foreach (var entry in diff)
                {
                    var status = entry.Status switch
                    {
                        ChangeKind.Added => "Added",
                        ChangeKind.Deleted => "Deleted",
                        ChangeKind.Renamed => "Renamed",
                        _ => "Modified"
                    };
                    changes.Add(new FileChangeSummary(status, entry.Path));
                }

                // If no committed changes yet, check the working directory against the base branch.
                // This captures files the agent has written but not yet committed.
                if (changes.Count == 0)
                {
                    var workingDiff = repo.Diff.Compare<TreeChanges>(
                        baseCommit.Tree, DiffTargets.WorkingDirectory);
                    foreach (var entry in workingDiff)
                    {
                        var status = entry.Status switch
                        {
                            ChangeKind.Added => "Added",
                            ChangeKind.Deleted => "Deleted",
                            ChangeKind.Renamed => "Renamed",
                            _ => "Modified"
                        };
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
    public async Task ValidateAsync(CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        await client.Repository.Get(_owner, _repo);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Returns a current token, either from the static field or by calling the token provider.
    /// </summary>
    private Task<string> GetTokenAsync(CancellationToken ct)
        => _clientProvider.GetTokenAsync(ct);

    /// <summary>
    /// Returns a GitHubClient configured with a current token.
    /// </summary>
    private Task<IGitHubClient> GetClientAsync(CancellationToken ct)
        => _clientProvider.GetClientAsync(ct);

}
