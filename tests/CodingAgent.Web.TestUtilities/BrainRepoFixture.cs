using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using LibGit2Sharp;
using Moq;

namespace CodingAgent.Web.TestUtilities;

/// <summary>
/// Creates a bare brain repository plus clones for tests.
/// Modelled on <c>CreateRemote</c>, <c>LocalProvider</c> and <c>PushMain</c> of
/// <c>BrainUpdateServiceConsolidationMergeTests</c>. Dispose resets file attributes and removes the
/// temp root (T9).
/// </summary>
/// <remarks>No <c>Category</c> trait: runs in CI and counts for Sonar (T12).</remarks>
public sealed class BrainRepoFixture : IDisposable
{
    private readonly string _root;
    private bool _disposed;

    public string RemotePath { get; }

    private BrainRepoFixture(string root, string remotePath)
    {
        _root = root;
        RemotePath = remotePath;
    }

    /// <summary>
    /// Creates a seeded repository on branch <c>main</c>, plus a bare clone as the remote.
    /// </summary>
    public static BrainRepoFixture Create(IReadOnlyDictionary<string, string> files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"brain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var source = Path.Combine(root, "source");
        Repository.Init(source);
        using (var repo = new Repository(source))
        {
            foreach (var (path, content) in files)
            {
                var fullPath = Path.Combine(source, path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, content);
                Commands.Stage(repo, path);
            }
            var sig = new Signature("test", "test@example.com", DateTimeOffset.UtcNow);
            repo.Commit("seed", sig, sig);
            if (repo.Head.FriendlyName != "main")
                repo.Branches.Rename(repo.Head.FriendlyName, "main");
        }

        var remote = Path.Combine(root, "remote.git");
        Repository.Clone(source, remote, new CloneOptions { IsBare = true });
        return new BrainRepoFixture(root, remote);
    }

    /// <summary>Creates a working clone named <paramref name="name"/> under the fixture root.</summary>
    public string Clone(string name)
    {
        var clonePath = Path.Combine(_root, name);
        Repository.Clone(RemotePath, clonePath);
        return clonePath;
    }

    /// <summary>
    /// Commits files in <paramref name="clonePath"/> and pushes to origin.
    /// A <see langword="null"/> value deletes the file.
    /// </summary>
    public void CommitAndPush(string clonePath, IReadOnlyDictionary<string, string?> files, string message)
    {
        using var repo = new Repository(clonePath);
        foreach (var (path, content) in files)
        {
            var fullPath = Path.Combine(clonePath, path);
            if (content is null)
            {
                File.Delete(fullPath);
                Commands.Stage(repo, path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, content);
                Commands.Stage(repo, path);
            }
        }
        var sig = new Signature("test", "test@example.com", DateTimeOffset.UtcNow);
        repo.Commit(message, sig, sig);
        PushToRemote(repo);
    }

    /// <summary>
    /// Creates a <see cref="Mock{IRepositoryProvider}"/> backed by this fixture's remote.
    /// <list type="bullet">
    ///   <item><c>BaseBranch</c> = <c>main</c></item>
    ///   <item><c>CloneAsync</c> — clones the remote to the given path</item>
    ///   <item><c>PullAsync</c> — fetches origin</item>
    ///   <item><c>GetHeadCommitShaAsync</c> — reads HEAD SHA</item>
    ///   <item><c>CommitAllAsync(path, message, ct)</c> — via <see cref="RepositoryGitOperations.CommitAll"/> so <c>.agent/</c> stays out</item>
    ///   <item><c>PushBranchAsync(path, branch, ct)</c> — maps <see cref="NonFastForwardException"/> to <see cref="InvalidOperationException"/></item>
    ///   <item><c>GetCommitWebUrl</c> → <see langword="null"/></item>
    /// </list>
    /// </summary>
    public Mock<IRepositoryProvider> Provider()
    {
        var provider = new Mock<IRepositoryProvider>();
        var remotePath = RemotePath;

        provider.Setup(p => p.BaseBranch).Returns("main");

        provider.Setup(p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns((WorkspacePath path, CancellationToken _) =>
            {
                Repository.Clone(remotePath, path.Value);
                return Task.CompletedTask;
            });

        provider.Setup(p => p.PullAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns((WorkspacePath path, CancellationToken _) =>
            {
                using var repo = new Repository(path.Value);
                Commands.Fetch(repo, "origin", Array.Empty<string>(), null, null);
                return Task.CompletedTask;
            });

        provider.Setup(p => p.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns((WorkspacePath path, CancellationToken _) =>
            {
                using var repo = new Repository(path.Value);
                return Task.FromResult(repo.Head.Tip.Sha);
            });

        // The 3-arg overload is what BrainUpdateService calls (CommitInboxAndPushAsync).
        provider.Setup(p => p.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((WorkspacePath path, string message, CancellationToken _) =>
            {
                RepositoryGitOperations.CommitAll(path.Value, message, null, allowEmpty: false);
                return Task.CompletedTask;
            });

        provider.Setup(p => p.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns((WorkspacePath path, BranchName _, CancellationToken _) =>
            {
                using var repo = new Repository(path.Value);
                PushToRemote(repo);
                return Task.CompletedTask;
            });

        provider.Setup(p => p.GetCommitWebUrl(It.IsAny<string>()))
            .Returns((string _) => null);

        return provider;
    }

    /// <summary>Returns all file paths and contents at the tip of <c>main</c> in the remote (recursively).</summary>
    public IReadOnlyDictionary<string, string> RemoteFiles()
    {
        using var repo = new Repository(RemotePath);
        var tip = repo.Branches["main"].Tip;
        var result = new Dictionary<string, string>();
        CollectBlobs(tip.Tree, "", result);
        return result;
    }

    private static void CollectBlobs(Tree tree, string prefix, Dictionary<string, string> result)
    {
        foreach (var entry in tree)
        {
            if (entry.Target is Blob blob)
            {
                result[prefix + entry.Name] = blob.GetContentText();
            }
            else if (entry.Target is Tree subTree)
            {
                CollectBlobs(subTree, prefix + entry.Name + "/", result);
            }
        }
    }

    /// <summary>Returns commit messages on <c>main</c> from newest to oldest.</summary>
    public IReadOnlyList<string> RemoteCommitMessages()
    {
        using var repo = new Repository(RemotePath);
        return repo.Branches["main"].Commits.Select(c => c.Message.TrimEnd()).ToList();
    }

    private static void PushToRemote(Repository repo)
    {
        try
        {
            repo.Network.Push(repo.Network.Remotes["origin"], "refs/heads/main:refs/heads/main");
        }
        catch (LibGit2SharpException ex) when (
            ex is NonFastForwardException
            || ex.Message.Contains("fastforward", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("fast-forward", StringComparison.OrdinalIgnoreCase))
        {
            // Map to the same exception shape as the real providers (T10).
            throw new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!Directory.Exists(_root)) return;
        // Git object files are read-only on Windows (T9).
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
