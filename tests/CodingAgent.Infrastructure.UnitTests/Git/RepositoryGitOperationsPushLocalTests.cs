using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Web.TestUtilities;
using LibGit2Sharp;
using Polly;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for <see cref="RepositoryGitOperations.Push"/> against a real local bare repository.
/// A local path sends no auth challenge, so the credentials callback never fires. The tests
/// show that a new branch reaches the bare remote and that a force push replaces rewritten history.
///
/// No network I/O: <see cref="BrainRepoFixture"/> supplies a bare remote and a working clone.
/// </summary>
public sealed class RepositoryGitOperationsPushLocalTests : IDisposable
{
    private const string FeatureBranch = "feature/auto-1-test";

    private readonly BrainRepoFixture _fixture =
        BrainRepoFixture.Create(new Dictionary<string, string> { ["README.md"] = "seed\n" });
    private readonly string _workPath;

    public RepositoryGitOperationsPushLocalTests()
    {
        _workPath = _fixture.Clone("work");
        RepositoryGitOperations.CreateBranch(_workPath, FeatureBranch);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Push_NewBranch_CreatesBranchOnBareRemote()
    {
        var headSha = CommitFile("first.txt", "first\n");

        await PushAsync(forcePush: false);

        RemoteTipSha().Should().Be(headSha);
    }

    [Fact]
    public async Task Push_Force_ReplacesRewrittenHistoryOnRemote()
    {
        CommitFile("first.txt", "first\n");
        await PushAsync(forcePush: false);

        using (var repo = new Repository(_workPath))
            repo.Reset(ResetMode.Hard, repo.Head.Tip.Parents.Single());
        var rewrittenSha = CommitFile("second.txt", "second\n");

        await PushAsync(forcePush: true);

        RemoteTipSha().Should().Be(rewrittenSha);
    }

    private Task PushAsync(bool forcePush) =>
        RepositoryGitOperations.Push(
            _workPath, _fixture.RemotePath, FeatureBranch, forcePush,
            "user", _ => Task.FromResult("token"), ResiliencePipeline.Empty, CancellationToken.None);

    /// <summary>Writes, stages and commits one file on the checked-out branch. Returns the new HEAD SHA.</summary>
    private string CommitFile(string name, string content)
    {
        using var repo = new Repository(_workPath);
        File.WriteAllText(Path.Combine(_workPath, name), content);
        Commands.Stage(repo, name);
        var signature = new Signature("test", "test@example.com", DateTimeOffset.UtcNow);
        return repo.Commit($"add {name}", signature, signature).Sha;
    }

    private string RemoteTipSha()
    {
        using var remote = new Repository(_fixture.RemotePath);
        return remote.Branches[FeatureBranch].Tip.Sha;
    }
}
