using LibGit2Sharp;

namespace CodingAgentWebUI.Infrastructure.Git;

/// <summary>
/// LibGit2Sharp-backed implementation of <see cref="IGitOperations"/>.
/// All native binary dependencies are isolated to this class.
/// </summary>
public sealed class LibGit2SharpGitOperations : IGitOperations
{
    public IReadOnlyList<string> GetChangedFiles(string repoPath)
    {
        using var repo = new Repository(repoPath);
        var status = repo.RetrieveStatus(new StatusOptions());
        var changedFiles = new List<string>();

        foreach (var entry in status)
        {
            if (entry.State != FileStatus.Ignored && entry.State != FileStatus.Unaltered)
            {
                changedFiles.Add(entry.FilePath);
            }
        }

        return changedFiles;
    }

    public bool HasConflicts(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.Index.Conflicts.Any();
    }

    public IReadOnlyList<ConflictEntry> GetConflicts(string repoPath)
    {
        using var repo = new Repository(repoPath);
        var conflicts = new List<ConflictEntry>();

        foreach (var conflict in repo.Index.Conflicts)
        {
            var oursContent = conflict.Ours != null
                ? repo.Lookup<Blob>(conflict.Ours.Id)?.GetContentText() ?? ""
                : "";
            var theirsContent = conflict.Theirs != null
                ? repo.Lookup<Blob>(conflict.Theirs.Id)?.GetContentText() ?? ""
                : "";
            var filePath = conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor?.Path;

            conflicts.Add(new ConflictEntry(filePath, oursContent, theirsContent));
        }

        return conflicts;
    }

    public void StageAllAndCommit(string repoPath, string message)
    {
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, "*");

        var signature = new Signature(
            GitConstants.CommitAuthorName, GitConstants.CommitAuthorEmail, DateTimeOffset.UtcNow);

        try
        {
            repo.Commit(message, signature, signature);
        }
        catch (LibGit2Sharp.EmptyCommitException ex)
        {
            throw new EmptyCommitException(ex.Message, ex);
        }
    }

    public void StageFile(string repoPath, string relativePath)
    {
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, relativePath);
    }

    public int GetHeadCommitFileCount(string repoPath)
    {
        using var repo = new Repository(repoPath);
        var headCommit = repo.Head.Tip;
        if (headCommit?.Parents.Any() == true)
        {
            var diff = repo.Diff.Compare<TreeChanges>(
                headCommit.Parents.First().Tree, headCommit.Tree);
            return diff.Count;
        }
        return 0;
    }

    public IReadOnlyList<FileChange> GetHeadCommitChanges(string repoPath)
    {
        using var repo = new Repository(repoPath);
        var headCommit = repo.Head.Tip;
        var parentTree = headCommit?.Parents.FirstOrDefault()?.Tree;

        if (parentTree is null || headCommit is null)
            return Array.Empty<FileChange>();

        var diff = repo.Diff.Compare<TreeChanges>(parentTree, headCommit.Tree);
        return diff.Select(change => new FileChange(
            change.Path,
            change.Status switch
            {
                ChangeKind.Added => FileChangeStatus.Added,
                ChangeKind.Deleted => FileChangeStatus.Deleted,
                ChangeKind.Renamed => FileChangeStatus.Renamed,
                _ => FileChangeStatus.Modified
            })).ToList();
    }

    public string? GetFileContentFromHead(string repoPath, string relativePath)
    {
        using var repo = new Repository(repoPath);
        var headCommit = repo.Head.Tip;
        if (headCommit is null) return null;

        var blob = headCommit.Tree[relativePath]?.Target as Blob;
        return blob?.GetContentText();
    }

    public string? GetFileContentFromHeadParent(string repoPath, string relativePath)
    {
        using var repo = new Repository(repoPath);
        var headCommit = repo.Head.Tip;
        var parentTree = headCommit?.Parents.FirstOrDefault()?.Tree;
        if (parentTree is null) return null;

        var blob = parentTree[relativePath]?.Target as Blob;
        return blob?.GetContentText();
    }

    public void ResetHardToRemote(string repoPath, string remoteBranch)
    {
        using var repo = new Repository(repoPath);
        var remoteBranchRef = repo.Branches[$"origin/{remoteBranch}"];
        if (remoteBranchRef != null)
        {
            repo.Reset(ResetMode.Hard, remoteBranchRef.Tip);
        }
    }

    public bool RemoteBranchExists(string repoPath, string remoteBranch)
    {
        using var repo = new Repository(repoPath);
        return repo.Branches[$"origin/{remoteBranch}"] != null;
    }
}
