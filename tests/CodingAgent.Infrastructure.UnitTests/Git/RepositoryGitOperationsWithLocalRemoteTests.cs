using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using LibGit2Sharp;
using Polly;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for <see cref="RepositoryGitOperations"/> methods that require a remote,
/// using a local file-path "remote" clone to avoid real network calls.
///
/// LibGit2Sharp supports cloning from local paths (file:// or bare paths), so we can
/// simulate a full push/pull/clone workflow entirely on disk.
/// </summary>
public class RepositoryGitOperationsWithLocalRemoteTests : IDisposable
{
    private readonly string _originPath;   // bare "remote" repo
    private readonly string _workspacePath; // local clone

    public RepositoryGitOperationsWithLocalRemoteTests()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        _originPath = Path.Combine(Path.GetTempPath(), $"git-origin-{testId}");
        _workspacePath = Path.Combine(Path.GetTempPath(), $"git-clone-{testId}");
        Directory.CreateDirectory(_originPath);
        Directory.CreateDirectory(_workspacePath);

        InitBareOriginWithInitialCommit();
        CloneOriginToWorkspace();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_originPath, recursive: true); } catch { }
        try { Directory.Delete(_workspacePath, recursive: true); } catch { }
    }

    private void InitBareOriginWithInitialCommit()
    {
        // Create a normal (non-bare) repo to get an initial commit, then push to bare.
        var tempSrc = Path.Combine(Path.GetTempPath(), $"git-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempSrc);
        try
        {
            Repository.Init(tempSrc);
            using var repo = new Repository(tempSrc);
            File.WriteAllText(Path.Combine(tempSrc, "README.md"), "# Origin");
            Commands.Stage(repo, "README.md");
            var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
            repo.Commit("Initial commit", sig, sig);

            // Init bare origin and push to it
            Repository.Init(_originPath, isBare: true);
            repo.Network.Remotes.Add("origin", _originPath);
            repo.Network.Push(repo.Network.Remotes["origin"],
                $"refs/heads/{repo.Head.FriendlyName}:refs/heads/{repo.Head.FriendlyName}");
        }
        finally
        {
            try { Directory.Delete(tempSrc, recursive: true); } catch { }
        }
    }

    private void CloneOriginToWorkspace()
    {
        Repository.Clone(_originPath, _workspacePath);
    }

    private void AddCommitToOrigin(string fileName = "extra.txt", string content = "extra content")
    {
        // Add a commit to the origin so we can test pull/merge
        using var origin = new Repository(_originPath);
        // A bare repo can't have working directory — need to push from a temp non-bare repo
        var tempPath = Path.Combine(Path.GetTempPath(), $"git-push-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPath);
        try
        {
            Repository.Clone(_originPath, tempPath);
            using var tempRepo = new Repository(tempPath);
            File.WriteAllText(Path.Combine(tempPath, fileName), content);
            Commands.Stage(tempRepo, fileName);
            var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
            tempRepo.Commit($"Add {fileName}", sig, sig);
            tempRepo.Network.Push(tempRepo.Network.Remotes["origin"],
                $"refs/heads/{tempRepo.Head.FriendlyName}:refs/heads/{tempRepo.Head.FriendlyName}");
        }
        finally
        {
            try { Directory.Delete(tempPath, recursive: true); } catch { }
        }
    }

    // ── HasCommitsAhead — with real remote ─────────────────────────────────────

    [Fact]
    public async Task HasCommitsAhead_WithNewCommitOnOrigin_ReturnsTrueAfterFetch()
    {
        // Confirm not ahead initially
        using (var repo = new Repository(_workspacePath))
        {
            var baseBranchName = repo.Head.FriendlyName;
            // Create a local branch tracking origin to use as "base"
            repo.Branches.Add("base-snapshot", repo.Head.Tip);
        }

        // Add a commit locally (HEAD is now ahead of origin)
        AddAndCommitLocal("src/local.cs");

        var result = await RepositoryGitOperations.HasCommitsAhead(
            _workspacePath, "base-snapshot", ResiliencePipeline.Empty, CancellationToken.None);

        result.Should().BeTrue("HEAD is one commit ahead of base-snapshot");
    }

    private void AddAndCommitLocal(string relativePath, string content = "// new")
    {
        var fullPath = Path.Combine(_workspacePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        using var repo = new Repository(_workspacePath);
        Commands.Stage(repo, relativePath);
        var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit($"Add {relativePath}", sig, sig);
    }
}
