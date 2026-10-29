using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using LibGit2Sharp;

namespace CodingAgent.Infrastructure.Git;

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
        if (headCommit is null)
            return Array.Empty<FileChange>();

        var parentTree = headCommit.Parents.FirstOrDefault()?.Tree;
        if (parentTree is null)
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

    public bool FileExists(string fullPath) => File.Exists(fullPath);

    public string ReadAllText(string fullPath) => File.ReadAllText(fullPath);

    public void WriteAllText(string fullPath, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public void DeleteFile(string fullPath)
    {
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }

    // ── Inbox push operations (spec 054 Task 6.1) ─────────────────────────

    public IReadOnlyList<string> DiscardChangesExcept(string repoPath, string keepRelativePath)
    {
        using var repo = new Repository(repoPath);
        var discarded = new List<string>();
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true
        });

        // Restore modified/deleted tracked files
        foreach (var entry in status)
        {
            var path = entry.FilePath.Replace('\\', '/');
            if (path == keepRelativePath) continue;
            if (entry.State.HasFlag(FileStatus.ModifiedInWorkdir)
                || entry.State.HasFlag(FileStatus.DeletedFromWorkdir))
            {
                repo.CheckoutPaths("HEAD", [entry.FilePath], new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
                discarded.Add(path);
            }
        }

        // Delete untracked files (except kept path)
        foreach (var entry in status)
        {
            var path = entry.FilePath.Replace('\\', '/');
            if (path == keepRelativePath) continue;
            if (!entry.State.HasFlag(FileStatus.NewInWorkdir)) continue;
            var fullPath = Path.Combine(repoPath, entry.FilePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                discarded.Add(path);
            }
        }

        // Remove then-empty directories
        TryDeleteEmptyDirectories(repoPath, keepRelativePath);

        return discarded;
    }

    private static void TryDeleteEmptyDirectories(string repoPath, string keepRelativePath)
    {
        foreach (var dir in Directory.GetDirectories(repoPath, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            // Skip .git directory and all its subdirectories
            var relative = dir.Substring(repoPath.Length).TrimStart(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith(".git", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Directory.Exists(dir)) continue;
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                try { Directory.Delete(dir); } catch { }
            }
        }
    }

    public void StageAndCommit(string repoPath, IReadOnlyList<string> relativePaths, string message)
    {
        using var repo = new Repository(repoPath);
        foreach (var path in relativePaths)
            Commands.Stage(repo, path);

        var sig = new Signature(
            GitConstants.CommitAuthorName, GitConstants.CommitAuthorEmail, DateTimeOffset.UtcNow);
        try
        {
            repo.Commit(message, sig, sig);
        }
        catch (LibGit2Sharp.EmptyCommitException ex)
        {
            throw new EmptyCommitException(ex.Message, ex);
        }
    }

    public IReadOnlyList<LocalCommit> GetCommitsAheadOfRemote(string repoPath, string remoteBranch)
    {
        using var repo = new Repository(repoPath);
        var remote = repo.Branches[$"origin/{remoteBranch}"];
        if (remote is null) return [];

        var filter = new CommitFilter
        {
            IncludeReachableFrom = repo.Head.Tip,
            ExcludeReachableFrom = remote.Tip
        };

        var commits = new List<LocalCommit>();
        foreach (var commit in repo.Commits.QueryBy(filter).Reverse())
        {
            var files = new List<CommitFileContent>();
            var parent = commit.Parents.FirstOrDefault();
            foreach (var diff in repo.Diff.Compare<TreeChanges>(parent?.Tree, commit.Tree))
            {
                var content = diff.Status != ChangeKind.Deleted
                    ? GetBlobContent(commit.Tree, diff.Path)
                    : null;
                var parentContent = parent is not null && diff.Status != ChangeKind.Added
                    ? GetBlobContent(parent.Tree, diff.OldPath)
                    : null;
                files.Add(new CommitFileContent(diff.Path, content, parentContent));
            }
            commits.Add(new LocalCommit(commit.Sha, commit.Message.Trim(), files));
        }
        return commits;
    }

    public string? GetFileContentAtRemote(string repoPath, string remoteBranch, string relativePath)
    {
        using var repo = new Repository(repoPath);
        var remote = repo.Branches[$"origin/{remoteBranch}"];
        if (remote is null) return null;
        return GetBlobContent(remote.Tip.Tree, relativePath);
    }

    private static string? GetBlobContent(Tree tree, string path)
    {
        var entry = tree[path];
        if (entry is null || entry.TargetType != TreeEntryTargetType.Blob) return null;
        var blob = (Blob)entry.Target;
        return blob.GetContentText();
    }

    // ── Review git operations (spec 054 Task 7A.1) ─────────────────────────

    public IReadOnlyList<string> ListTrackedFiles(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.Index.Select(e => e.Path.Replace('\\', '/')).ToList();
    }

    public string? ReadFileAtCommit(string repoPath, string commitSha, string relativePath)
    {
        using var repo = new Repository(repoPath);
        try
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit is null) return null;
            return GetBlobContent(commit.Tree, relativePath);
        }
        catch { return null; }
    }

    public string? ReadFileAtHead(string repoPath, string relativePath)
    {
        using var repo = new Repository(repoPath);
        var head = repo.Head.Tip;
        if (head is null) return null;
        return GetBlobContent(head.Tree, relativePath);
    }

    public IReadOnlyList<BrainPathChange> GetWorkingTreeChanges(string repoPath)
    {
        using var repo = new Repository(repoPath);
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true
        });
        var changes = new List<BrainPathChange>();
        foreach (var entry in status)
        {
            if (entry.State is FileStatus.Ignored or FileStatus.Unaltered) continue;
            var path = entry.FilePath.Replace('\\', '/');
            var kind = entry.State.HasFlag(FileStatus.NewInWorkdir) || entry.State.HasFlag(FileStatus.NewInIndex)
                ? BrainPathChangeKind.Added
                : entry.State.HasFlag(FileStatus.DeletedFromWorkdir) || entry.State.HasFlag(FileStatus.DeletedFromIndex)
                    ? BrainPathChangeKind.Deleted
                    : BrainPathChangeKind.Modified;
            changes.Add(new BrainPathChange(path, kind));
        }
        return changes;
    }

    public string GetReviewPatch(string repoPath)
    {
        using var repo = new Repository(repoPath);
        // Stage skill dirs and README
        foreach (var pattern in new[] { "projects", "technology", "general", "review", "README.md" })
            try { Commands.Stage(repo, pattern); } catch { /* missing paths are ok */ }

        using var patch = repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.Index);
        return patch.Content;
    }

    public IReadOnlyList<BrainCommitInfo> GetSkillCommitsSinceLastReview(string repoPath)
    {
        using var repo = new Repository(repoPath);
        var commits = new List<Commit>();

        // Find the newest review commit
        Commit? latestReview = null;
        foreach (var commit in repo.Commits.QueryBy(new CommitFilter { SortBy = CommitSortStrategies.Topological }))
        {
            if (commit.Message.TrimStart().StartsWith("brain: review run", StringComparison.Ordinal))
            {
                latestReview = commit;
                break;
            }
        }

        // Collect skill commits newer than the review commit
        foreach (var commit in repo.Commits.QueryBy(new CommitFilter { SortBy = CommitSortStrategies.Topological }))
        {
            if (latestReview is not null && commit.Sha == latestReview.Sha) break;

            var msg = commit.Message.TrimStart();
            if (msg.StartsWith("brain: review run", StringComparison.Ordinal)
                || msg.StartsWith("brain: archive v1 content", StringComparison.Ordinal))
                continue;

            // Check if it touches skill paths
            var parent = commit.Parents.FirstOrDefault();
            var touched = repo.Diff.Compare<TreeChanges>(parent?.Tree, commit.Tree)
                .Any(c => c.Path.StartsWith("projects/", StringComparison.Ordinal)
                    || c.Path.StartsWith("technology/", StringComparison.Ordinal)
                    || c.Path.StartsWith("general/", StringComparison.Ordinal));
            if (!touched) continue;

            commits.Add(commit);
            if (commits.Count >= 50) break;
        }

        commits.Reverse(); // oldest first
        return commits.Select(c => new BrainCommitInfo(c.Sha, c.Message.TrimEnd())).ToList();
    }
}
