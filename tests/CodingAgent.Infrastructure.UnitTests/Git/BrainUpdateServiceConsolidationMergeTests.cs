using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using LibGit2Sharp;
using Moq;
using Serilog;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for <see cref="BrainUpdateService.PushConsolidationAsync"/> and its file merge. Many repositories
/// feed one brain, so runs push lessons while a brain consolidation works on its clone. The consolidation's
/// push must then keep the consolidated files and add the lines those runs pushed, instead of failing.
/// </summary>
public class BrainUpdateServiceConsolidationMergeTests
{
    // ── MergeConsolidatedFile ───────────────────────────────────────────────────

    [Fact]
    public void MergeConsolidatedFile_RemoteUnchanged_KeepsTheConsolidatedVersion()
    {
        var merged = BrainUpdateService.MergeConsolidatedFile("- a\n- a again\n", "- a\n", "- a\n- a again\n");

        merged.Should().Be("- a\n");
    }

    [Fact]
    public void MergeConsolidatedFile_RemoteUnchangedAndConsolidationDeletedTheFile_DeletesIt()
    {
        var merged = BrainUpdateService.MergeConsolidatedFile("- stale\n", null, "- stale\n");

        merged.Should().BeNull();
    }

    [Fact]
    public void MergeConsolidatedFile_RemoteAppendedLines_AppendsThemToTheConsolidatedVersion()
    {
        var merged = BrainUpdateService.MergeConsolidatedFile(
            "# K\n\n- a\n- a again\n",
            "# K\n\n- a (merged)\n",
            "# K\n\n- a\n- a again\n- lesson 42\n");

        merged.Should().Be("# K\n\n- a (merged)\n\n- lesson 42\n");
    }

    [Fact]
    public void MergeConsolidatedFile_ConsolidationDeletedAFileTheRemoteAddedTo_KeepsOnlyTheAddedLines()
    {
        var merged = BrainUpdateService.MergeConsolidatedFile("# Old\n\n- stale\n", null, "# Old\n\n- stale\n\n- follow-up\n");

        merged.Should().Be("- follow-up\n", "the lesson must not be lost, and the stale content stays consolidated away");
    }

    [Fact]
    public void MergeConsolidatedFile_RemoteOnlyRemovedLines_KeepsTheConsolidatedVersion()
    {
        var merged = BrainUpdateService.MergeConsolidatedFile("- a\n- b\n", "- a and b\n", "- a\n");

        merged.Should().Be("- a and b\n");
    }

    [Fact]
    public void MergeConsolidatedFile_BothAddedTheSameNewFile_KeepsOursAndAddsTheirLines()
    {
        var merged = BrainUpdateService.MergeConsolidatedFile(null, "- ours\n", "- theirs\n");

        merged.Should().Be("- ours\n\n- theirs\n");
    }

    [Fact]
    public void MergeConsolidatedFile_ConsolidatedVersionWithoutTrailingNewline_SeparatesWithABlankLine()
    {
        var merged = BrainUpdateService.MergeConsolidatedFile("- a\n", "- a merged", "- a\n- new\n");

        merged.Should().Be("- a merged\n\n- new\n");
    }

    // ── LinesAddedSince ─────────────────────────────────────────────────────────

    [Fact]
    public void LinesAddedSince_CountsRepeatedLines()
    {
        var added = BrainUpdateService.LinesAddedSince("- x\n", "- x\n- x\n- y\n");

        added.Should().Equal("- x", "- y");
    }

    [Fact]
    public void LinesAddedSince_DropsBlankLinesAtEitherEnd_KeepsThemInside()
    {
        var added = BrainUpdateService.LinesAddedSince("- a\n", "- a\n\n- b\n\n- c\n\n");

        added.Should().Equal("- b", "", "- c");
    }

    [Fact]
    public void LinesAddedSince_ModifiedLineCountsAsAdded()
    {
        var added = BrainUpdateService.LinesAddedSince("- a\n- b\n", "- a\n- b (clarified)\n");

        added.Should().Equal("- b (clarified)");
    }

    [Fact]
    public void LinesAddedSince_IgnoresCarriageReturns()
    {
        var added = BrainUpdateService.LinesAddedSince("- a\r\n", "- a\n- b\r\n");

        added.Should().Equal("- b");
    }

    // ── PushConsolidationAsync with mocked git ──────────────────────────────────

    [Fact]
    public async Task PushConsolidationAsync_PushSucceeds_DoesNotMerge()
    {
        var git = new Mock<IGitOperations>();
        var provider = new Mock<IRepositoryProvider>();
        provider.Setup(p => p.BaseBranch).Returns("main");
        var sut = new BrainUpdateService(new LoggerConfiguration().CreateLogger(), git.Object);

        var result = await sut.PushConsolidationAsync("/brain", "Brain consolidation run r1", provider.Object, CancellationToken.None);

        result.Should().Be(1);
        provider.Verify(p => p.PushBranchAsync("/brain", (BranchName)"main", It.IsAny<CancellationToken>()), Times.Once);
        git.Verify(g => g.ResetHardToRemote(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        provider.Verify(p => p.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PushConsolidationAsync_RejectedOnce_ReturnsSecondAttempt()
    {
        var git = new Mock<IGitOperations>();
        git.Setup(g => g.GetHeadCommitChanges("/brain"))
            .Returns([new FileChange("knowledge.md", FileChangeStatus.Modified)]);
        git.Setup(g => g.GetFileContentFromHead("/brain", "knowledge.md")).Returns("- merged\n");
        git.Setup(g => g.GetFileContentFromHeadParent("/brain", "knowledge.md")).Returns("- a\n");
        // TODO: PushConsolidationAsync_RejectedEveryTime_ThrowsAfterTheLastAttempt does NOT set up
        // FileExists; this test does. Moq returns false by default, so both tests pass by coincidence.
        // Add FileExists setup to the existing rejection test as well, so the intent is explicit and
        // a future change to FileExists semantics will not be masked.
        git.Setup(g => g.FileExists(It.IsAny<string>())).Returns(false);

        var provider = new Mock<IRepositoryProvider>();
        provider.Setup(p => p.BaseBranch).Returns("main");
        var pushCallCount = 0;
        provider.Setup(p => p.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                pushCallCount++;
                if (pushCallCount == 1)
                    throw new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward");
                return Task.CompletedTask;
            });
        provider.Setup(p => p.PullAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        provider.Setup(p => p.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new BrainUpdateService(new LoggerConfiguration().CreateLogger(), git.Object);

        var result = await sut.PushConsolidationAsync("/brain", "Brain consolidation run r1", provider.Object, CancellationToken.None, maxPushRetries: 3);

        result.Should().Be(2);
        provider.Verify(p => p.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        git.Verify(g => g.ResetHardToRemote("/brain", "main"), Times.Once);
    }

    [Fact]
    public async Task PushConsolidationAsync_OtherErrorThanNonFastForward_ThrowsWithoutMerging()
    {
        var git = new Mock<IGitOperations>();
        var provider = new Mock<IRepositoryProvider>();
        provider.Setup(p => p.BaseBranch).Returns("main");
        provider.Setup(p => p.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Push failed: 403 forbidden"));
        var sut = new BrainUpdateService(new LoggerConfiguration().CreateLogger(), git.Object);

        var act = () => sut.PushConsolidationAsync("/brain", "Brain consolidation run r1", provider.Object, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*403*");
        git.Verify(g => g.ResetHardToRemote(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task PushConsolidationAsync_RejectedEveryTime_ThrowsAfterTheLastAttempt()
    {
        var git = new Mock<IGitOperations>();
        git.Setup(g => g.GetHeadCommitChanges("/brain"))
            .Returns([new FileChange("knowledge.md", FileChangeStatus.Modified)]);
        git.Setup(g => g.GetFileContentFromHead("/brain", "knowledge.md")).Returns("- merged\n");
        git.Setup(g => g.GetFileContentFromHeadParent("/brain", "knowledge.md")).Returns("- a\n");
        var provider = new Mock<IRepositoryProvider>();
        provider.Setup(p => p.BaseBranch).Returns("main");
        provider.Setup(p => p.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward"));
        var sut = new BrainUpdateService(new LoggerConfiguration().CreateLogger(), git.Object);

        var act = () => sut.PushConsolidationAsync("/brain", "Brain consolidation run r1", provider.Object, CancellationToken.None, maxPushRetries: 2);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*non-fast-forward*");
        provider.Verify(p => p.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        git.Verify(g => g.ResetHardToRemote("/brain", "main"), Times.Once);
    }

    // ── PushConsolidationAsync with real repositories ───────────────────────────

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PushConsolidationAsync_LessonsPushedDuringTheConsolidation_AreMergedAndNothingIsLost()
    {
        var root = Path.Combine(Path.GetTempPath(), $"brain-consolidation-merge-{Guid.NewGuid():N}");
        try
        {
            var remote = CreateRemote(root, new Dictionary<string, string>
            {
                ["knowledge.md"] = "# Knowledge\n\n- use retries\n- use retries with backoff\n",
                ["old.md"] = "# Old\n\n- stale note\n",
                ["log.md"] = "- seed\n"
            });

            // The consolidation clones the brain and consolidates it.
            var consolidation = Path.Combine(root, "consolidation");
            Repository.Clone(remote, consolidation);
            File.WriteAllText(Path.Combine(consolidation, "knowledge.md"), "# Knowledge\n\n- use retries with backoff\n");
            File.Delete(Path.Combine(consolidation, "old.md"));
            Directory.CreateDirectory(Path.Combine(consolidation, ".agent"));
            File.WriteAllText(Path.Combine(consolidation, ".agent", "brain-consolidation-review.md"), "review notes");
            RepositoryGitOperations.CommitAll(consolidation, "Brain consolidation run r1", null, allowEmpty: false);

            // Meanwhile a run of another repository pushes its lessons to the same brain.
            var lesson = Path.Combine(root, "lesson");
            Repository.Clone(remote, lesson);
            File.AppendAllText(Path.Combine(lesson, "knowledge.md"), "- pin the SDK version\n");
            File.AppendAllText(Path.Combine(lesson, "old.md"), "- stale note is still true for v1\n");
            Directory.CreateDirectory(Path.Combine(lesson, "topics"));
            File.WriteAllText(Path.Combine(lesson, "topics", "ci.md"), "- cache the NuGet folder\n");
            RepositoryGitOperations.CommitAll(lesson, "brain: update from run r2 (42)", null, allowEmpty: false);
            PushMain(lesson);
            string lessonCommit;
            using (var lessonRepo = new Repository(lesson))
                lessonCommit = lessonRepo.Head.Tip.Sha;

            var sut = new BrainUpdateService(new LoggerConfiguration().CreateLogger());

            await sut.PushConsolidationAsync(consolidation, "Brain consolidation run r1", LocalProvider().Object, CancellationToken.None);

            using var remoteRepo = new Repository(remote);
            var tip = remoteRepo.Branches["main"].Tip;
            tip.Message.Trim().Should().Be("Brain consolidation run r1");
            remoteRepo.ObjectDatabase.FindMergeBase(tip, remoteRepo.Lookup<Commit>(lessonCommit))!.Sha
                .Should().Be(lessonCommit, "the consolidation lands on top of the lesson, nothing is force-pushed away");
            Content(tip, "knowledge.md").Should().Be("# Knowledge\n\n- use retries with backoff\n\n- pin the SDK version\n");
            Content(tip, "old.md").Should().Be("- stale note is still true for v1\n");
            Content(tip, "topics/ci.md").Should().Be("- cache the NuGet folder\n");
            Content(tip, "log.md").Should().Be("- seed\n");
            tip.Tree[".agent"].Should().BeNull("the consolidation's working files are never committed");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static string CreateRemote(string root, Dictionary<string, string> files)
    {
        var source = Path.Combine(root, "source");
        Repository.Init(source);
        using (var repo = new Repository(source))
        {
            foreach (var (path, content) in files)
            {
                File.WriteAllText(Path.Combine(source, path), content);
                Commands.Stage(repo, path);
            }
            var signature = new Signature("test", "test@example.com", DateTimeOffset.UtcNow);
            repo.Commit("seed", signature, signature);
            if (repo.Head.FriendlyName != "main")
                repo.Branches.Rename(repo.Head.FriendlyName, "main");
        }

        var remote = Path.Combine(root, "remote.git");
        Repository.Clone(source, remote, new CloneOptions { IsBare = true });
        return remote;
    }

    /// <summary>A brain provider on local repositories: fetches, commits like the real providers, and pushes to origin.</summary>
    private static Mock<IRepositoryProvider> LocalProvider()
    {
        var provider = new Mock<IRepositoryProvider>();
        provider.Setup(p => p.BaseBranch).Returns("main");
        provider.Setup(p => p.PullAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns((WorkspacePath path, CancellationToken _) =>
            {
                using var repo = new Repository(path.Value);
                Commands.Fetch(repo, "origin", Array.Empty<string>(), null, null);
                return Task.CompletedTask;
            });
        provider.Setup(p => p.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((WorkspacePath path, string message, CancellationToken _) =>
            {
                RepositoryGitOperations.CommitAll(path.Value, message, null, allowEmpty: false);
                return Task.CompletedTask;
            });
        provider.Setup(p => p.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns((WorkspacePath path, BranchName _, CancellationToken _) =>
            {
                PushMain(path.Value);
                return Task.CompletedTask;
            });
        return provider;
    }

    private static void PushMain(string path)
    {
        using var repo = new Repository(path);
        try
        {
            repo.Network.Push(repo.Network.Remotes["origin"], "refs/heads/main:refs/heads/main");
        }
        catch (LibGit2SharpException ex) when (ex is NonFastForwardException
            || ex.Message.Contains("fastforward", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("fast-forward", StringComparison.OrdinalIgnoreCase))
        {
            // The real providers report a rejected push this way.
            throw new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward", ex);
        }
    }

    private static string? Content(Commit commit, string path) =>
        (commit.Tree[path]?.Target as Blob)?.GetContentText();

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        // Git object files are read-only on Windows.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
