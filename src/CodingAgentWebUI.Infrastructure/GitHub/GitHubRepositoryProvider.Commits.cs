using LibGit2Sharp;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using Signature = LibGit2Sharp.Signature;
using Repository = LibGit2Sharp.Repository;

namespace CodingAgentWebUI.Infrastructure.GitHub;

public partial class GitHubRepositoryProvider
{
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
        => PushBranchAsync(workspacePath, branchName, forcePush: false, ct);

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
                    new UsernamePasswordCredentials { Username = GitConstants.TokenUsername, Password = token },
                OnPushStatusError = error =>
                    pushError = $"Push failed for ref '{error.Reference}': {error.Message}"
            };

            // Force-push uses '+' prefix on refspec to allow non-fast-forward updates (required after rebase)
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
}
