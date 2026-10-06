using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Models;
using LibGit2Sharp;
using Polly;
using MergeResult = CodingAgent.Pipeline.Models.MergeResult;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for <see cref="RepositoryGitOperations.MergeFromBase"/> against real local repositories:
/// an "origin" repository plays the remote with <c>main</c>, and a clone holds the PR branch.
///
/// The rework design is that main is authoritative: where the branch and main changed the same
/// file, main's version is kept and the branch's change to it is dropped, for the agent to
/// re-apply. In a rebase, git's "theirs" is the branch commit being replayed, so resolving with
/// "theirs" kept the branch's version and reverted main (issue #3093).
///
/// All tests use only local temp git repos — no network I/O.
/// </summary>
public sealed class RepositoryGitOperationsMergeFromBaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rgo-rebase-{Guid.NewGuid():N}");
    private readonly string _originPath;
    private readonly string _workPath;
    private readonly string _baseBranch;
    private readonly Signature _signature = new("test", "test@example.com", DateTimeOffset.UtcNow);

    public RepositoryGitOperationsMergeFromBaseTests()
    {
        _originPath = Path.Combine(_root, "origin");
        _workPath = Path.Combine(_root, "work");
        Directory.CreateDirectory(_originPath);
        Repository.Init(_originPath);

        using (var origin = new Repository(_originPath))
        {
            WriteAndStage(origin, _originPath, "shared.txt", "base\n");
            WriteAndStage(origin, _originPath, "branch-only.txt", "base\n");
            WriteAndStage(origin, _originPath, "deleted-on-main.txt", "base\n");
            WriteAndStage(origin, _originPath, "deleted-on-branch.txt", "base\n");
            WriteAndStage(origin, _originPath, "service.txt", ServiceContent("base"));
            origin.Commit("base", _signature, _signature);
            _baseBranch = origin.Head.FriendlyName;
        }

        Repository.Clone(_originPath, _workPath);
        using (var work = new Repository(_workPath))
            Commands.Checkout(work, work.CreateBranch("feature/auto-1-test"));
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task FileChangedOnBothSides_KeepsMainsVersion_AndKeepsBranchOnlyChanges()
    {
        CommitOnBranch("branch change", ("shared.txt", "branch version\n"), ("branch-only.txt", "branch version\n"));
        CommitOnMain("main change", ("shared.txt", "main version\n"));

        var result = await MergeFromBaseAsync();

        result.ForceResolved.Should().BeTrue();
        result.ConflictFiles.Should().Equal("shared.txt");
        ReadWork("shared.txt").Should().Be("main version\n", "main is authoritative for conflicting files");
        ReadWork("branch-only.txt").Should().Be("branch version\n", "changes main did not touch survive the rebase");
    }

    [Fact]
    public async Task FileAddedOnBothSides_KeepsMainsVersion()
    {
        CommitOnBranch("branch adds", ("added-on-both.txt", "branch version\n"));
        CommitOnMain("main adds", ("added-on-both.txt", "main version\n"));

        var result = await MergeFromBaseAsync();

        result.ConflictFiles.Should().Equal("added-on-both.txt");
        ReadWork("added-on-both.txt").Should().Be("main version\n");
        AssertRebaseFinishedCleanly();
    }

    [Fact]
    public async Task ConflictsInSeveralBranchCommits_ResolvesEachStepKeepingMain()
    {
        CommitOnBranch("first branch commit", ("shared.txt", "branch version\n"));
        CommitOnBranch("second branch commit", ("added-on-both.txt", "branch version\n"));
        CommitOnMain("main change", ("shared.txt", "main version\n"), ("added-on-both.txt", "main version\n"));

        var result = await MergeFromBaseAsync();

        result.ConflictFiles.Should().BeEquivalentTo(["shared.txt", "added-on-both.txt"],
            "the second replayed commit conflicts too and is resolved in its own step");
        ReadWork("shared.txt").Should().Be("main version\n");
        ReadWork("added-on-both.txt").Should().Be("main version\n");
        AssertRebaseFinishedCleanly();
    }

    [Fact]
    public async Task FileDeletedOnMainAndChangedOnBranch_DeletesFile()
    {
        CommitOnBranch("branch change", ("deleted-on-main.txt", "branch version\n"));
        CommitOnMain("main deletes", deletions: ["deleted-on-main.txt"]);

        var result = await MergeFromBaseAsync();

        result.ConflictFiles.Should().Contain("deleted-on-main.txt");
        File.Exists(Path.Combine(_workPath, "deleted-on-main.txt")).Should().BeFalse("main deleted it");
    }

    [Fact]
    public async Task FileDeletedOnBranchAndChangedOnMain_KeepsMainsVersion()
    {
        CommitOnBranch("branch deletes", deletions: ["deleted-on-branch.txt"]);
        CommitOnMain("main change", ("deleted-on-branch.txt", "main version\n"));

        var result = await MergeFromBaseAsync();

        result.ConflictFiles.Should().Contain("deleted-on-branch.txt");
        ReadWork("deleted-on-branch.txt").Should().Be("main version\n");
    }

    // libgit2's rebase does no rename detection: a branch rename replays as a deletion plus an
    // addition. Keeping main's side for the old path is not enough; the branch's copy at the new
    // path still landed, holding the file as it was before main's change.

    [Fact]
    public async Task FileRenamedOnMainAndChangedOnBranch_KeepsMainsRenamedFile()
    {
        CommitOnBranch("branch change", ("service.txt", ServiceContent("branch")));
        Commit(_originPath, "main renames", [("renamed-service.txt", ServiceContent("main"))], ["service.txt"]);

        var result = await MergeFromBaseAsync();

        result.ForceResolved.Should().BeTrue();
        ReadWork("renamed-service.txt").Should().Be(ServiceContent("main"), "main is authoritative for conflicting files");
        File.Exists(Path.Combine(_workPath, "service.txt")).Should().BeFalse("main renamed it away");
        AssertRebaseFinishedCleanly();
    }

    [Fact]
    public async Task FileRenamedOnBranchAndChangedOnMain_KeepsMainsFileAndDropsTheRename()
    {
        Commit(_workPath, "branch renames", [("renamed-service.txt", ServiceContent("branch"))], ["service.txt"]);
        CommitOnMain("main change", ("service.txt", ServiceContent("main")));

        var result = await MergeFromBaseAsync();

        result.ForceResolved.Should().BeTrue();
        result.ConflictFiles.Should().BeEquivalentTo(["service.txt", "renamed-service.txt"]);
        result.ForceResolvedContext.Select(c => c.Path).Should().Contain("renamed-service.txt");
        ReadWork("service.txt").Should().Be(ServiceContent("main"), "main is authoritative for conflicting files");
        File.Exists(Path.Combine(_workPath, "renamed-service.txt")).Should().BeFalse(
            "the branch's change to the conflicting file, its rename included, is dropped for the agent to re-apply");
        AssertRebaseFinishedCleanly();
    }

    [Fact]
    public async Task FileDeletedOnMainAndRenamedOnBranch_DropsTheRenamedCopy()
    {
        Commit(_workPath, "branch renames", [("renamed-service.txt", ServiceContent("branch"))], ["service.txt"]);
        CommitOnMain("main deletes", deletions: ["service.txt"]);

        var result = await MergeFromBaseAsync();

        File.Exists(Path.Combine(_workPath, "renamed-service.txt")).Should().BeFalse("code main deleted must not come back under a new name");
        File.Exists(Path.Combine(_workPath, "service.txt")).Should().BeFalse();
        result.ForceResolved.Should().BeTrue();
        result.ConflictFiles.Should().BeEquivalentTo(["service.txt", "renamed-service.txt"]);
        AssertRebaseFinishedCleanly();
    }

    [Fact]
    public async Task FileRenamedOnBranchOnly_KeepsTheRename()
    {
        Commit(_workPath, "branch renames", [("renamed-service.txt", ServiceContent("branch"))], ["service.txt"]);
        CommitOnMain("main change", ("shared.txt", "main version\n"));

        var result = await MergeFromBaseAsync();

        result.HasConflicts.Should().BeFalse();
        ReadWork("renamed-service.txt").Should().Be(ServiceContent("branch"));
        File.Exists(Path.Combine(_workPath, "service.txt")).Should().BeFalse();
        AssertRebaseFinishedCleanly();
    }

    [Fact]
    public async Task FileRenamedDifferentlyOnBothSides_KeepsMainsName()
    {
        Commit(_workPath, "branch renames", [("branch-name.txt", ServiceContent("branch"))], ["service.txt"]);
        Commit(_originPath, "main renames", [("main-name.txt", ServiceContent("main"))], ["service.txt"]);

        var result = await MergeFromBaseAsync();

        ReadWork("main-name.txt").Should().Be(ServiceContent("main"));
        File.Exists(Path.Combine(_workPath, "branch-name.txt")).Should().BeFalse();
        File.Exists(Path.Combine(_workPath, "service.txt")).Should().BeFalse();
        AssertRebaseFinishedCleanly();
        result.ForceResolved.Should().BeTrue();
    }

    [Fact]
    public async Task ForceResolved_ReportsTheDroppedBranchChangeAndMainsChange()
    {
        CommitOnBranch("branch change", ("shared.txt", "branch version\n"));
        CommitOnMain("main change for #42", ("shared.txt", "main version\n"));

        var result = await MergeFromBaseAsync();

        result.MergeBaseSha.Should().NotBeNullOrEmpty();
        result.PreviousHeadSha.Should().NotBeNullOrEmpty();
        result.BaseHeadSha.Should().NotBeNullOrEmpty();
        var context = result.ForceResolvedContext.Should().ContainSingle().Subject;
        context.Path.Should().Be("shared.txt");
        context.BranchChange.Should().Contain("+branch version");
        context.BaseChange.Should().Contain("+main version");
        context.BaseCommits.Should().ContainSingle().Which.Should().EndWith("main change for #42");
    }

    [Fact]
    public async Task NoConflicts_KeepsAllChanges_AndReportsNoContext()
    {
        CommitOnBranch("branch change", ("branch-only.txt", "branch version\n"));
        CommitOnMain("main change", ("shared.txt", "main version\n"));

        var result = await MergeFromBaseAsync();

        result.HasConflicts.Should().BeFalse();
        result.ForceResolvedContext.Should().BeEmpty();
        ReadWork("branch-only.txt").Should().Be("branch version\n");
        ReadWork("shared.txt").Should().Be("main version\n");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private Task<MergeResult> MergeFromBaseAsync() =>
        RepositoryGitOperations.MergeFromBase(
            new WorkspacePath(_workPath), _baseBranch, "user", "token", ResiliencePipeline.Empty, CancellationToken.None);

    private void CommitOnBranch(string message, params (string Path, string Content)[] files) =>
        Commit(_workPath, message, files, []);

    private void CommitOnBranch(string message, string[] deletions) =>
        Commit(_workPath, message, [], deletions);

    private void CommitOnMain(string message, params (string Path, string Content)[] files) =>
        Commit(_originPath, message, files, []);

    private void CommitOnMain(string message, string[] deletions) =>
        Commit(_originPath, message, [], deletions);

    private void Commit(string repoPath, string message, (string Path, string Content)[] files, string[] deletions)
    {
        using var repo = new Repository(repoPath);
        foreach (var (path, content) in files)
            WriteAndStage(repo, repoPath, path, content);
        foreach (var path in deletions)
        {
            File.Delete(Path.Combine(repoPath, path));
            Commands.Stage(repo, path);
        }
        repo.Commit(message, _signature, _signature);
    }

    private static void WriteAndStage(Repository repo, string repoPath, string path, string content)
    {
        File.WriteAllText(Path.Combine(repoPath, path), content);
        Commands.Stage(repo, path);
    }

    // Normalised: libgit2 honours a global core.autocrlf on checkout.
    private string ReadWork(string path) => File.ReadAllText(Path.Combine(_workPath, path)).ReplaceLineEndings("\n");

    private void AssertRebaseFinishedCleanly()
    {
        using var repo = new Repository(_workPath);
        repo.Info.CurrentOperation.Should().Be(CurrentOperation.None, "the rebase must run to completion");
        repo.Index.Conflicts.Should().BeEmpty();
        repo.RetrieveStatus().IsDirty.Should().BeFalse("every resolution must be committed");
        repo.Info.IsHeadDetached.Should().BeFalse();
        repo.Branches["feature/auto-1-test"].Tip.Should().Be(repo.Head.Tip, "the pipeline pushes refs/heads/<branch>");
    }

    // Long enough for rename detection to pair the old and new path; only line 10 differs per side,
    // so a rename on one side plus an edit on the other is a content conflict.
    private static string ServiceContent(string line10) =>
        string.Concat(Enumerable.Range(1, 20).Select(i => i == 10 ? $"{line10}\n" : $"line {i}\n"));
}
