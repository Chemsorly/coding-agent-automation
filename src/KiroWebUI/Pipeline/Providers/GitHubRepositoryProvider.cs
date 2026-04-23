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

            // Stage workdir changes using repo.Index.Add() + repo.Index.Write().
            // This is the pattern from the libgit2sharp wiki (git-add, git-commit)
            // and is more reliable than Commands.Stage() which can silently fail.
            var stagedAny = false;
            foreach (var entry in preStatus)
            {
                if (entry.State.HasFlag(FileStatus.NewInWorkdir)
                    || entry.State.HasFlag(FileStatus.ModifiedInWorkdir)
                    || entry.State.HasFlag(FileStatus.DeletedFromWorkdir)
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
                    if (Services.PipelineFormatting.IsPathBlacklisted(change.Path, blacklistedPaths))
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

    private static string MapChangeKind(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "Added",
        ChangeKind.Deleted => "Deleted",
        ChangeKind.Renamed => "Renamed",
        _ => "Modified"
    };

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
