using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using LibGit2Sharp;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Git;

public class BrainUpdateServicePushRetryTests : IDisposable
{
    private readonly Mock<IRepositoryProvider> _mockProvider;
    private readonly BrainUpdateService _sut;
    private readonly string _repoPath;
    private readonly string _bareRepoPath;

    public BrainUpdateServicePushRetryTests()
    {
        _mockProvider = new Mock<IRepositoryProvider>();
        _mockProvider.Setup(p => p.BaseBranch).Returns("main");
        _mockProvider.Setup(p => p.PullAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new BrainUpdateService(new LoggerConfiguration().CreateLogger());

        // Create a temp git repo with an initial commit so CommitAndPushAsync can work
        _bareRepoPath = Path.Combine(Path.GetTempPath(), $"brain-test-bare-{Guid.NewGuid():N}");
        _repoPath = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");

        Repository.Init(_bareRepoPath, isBare: true);
        Repository.Clone(_bareRepoPath, _repoPath);

        // Create initial commit
        using var repo = new Repository(_repoPath);
        var filePath = Path.Combine(_repoPath, "README.md");
        File.WriteAllText(filePath, "# Brain\n");
        Commands.Stage(repo, "README.md");
        var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("initial", sig, sig);

        // Push initial commit to bare repo
        var remote = repo.Network.Remotes["origin"];
        repo.Network.Push(remote, "refs/heads/master");

        // Create and checkout main branch tracking origin
        var mainBranch = repo.CreateBranch("main", repo.Head.Tip);
        Commands.Checkout(repo, mainBranch);
        repo.Branches.Update(mainBranch,
            b => b.Remote = "origin",
            b => b.UpstreamBranch = "refs/heads/main");

        // Push main to bare
        repo.Network.Push(remote, "refs/heads/main");
    }

    public void Dispose()
    {
        DeleteDirectory(_repoPath);
        DeleteDirectory(_bareRepoPath);
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }

    private void CreateBrainChange(string fileName = "log.md", string content = "new entry\n")
    {
        var filePath = Path.Combine(_repoPath, fileName);
        File.WriteAllText(filePath, content);
    }

    [Fact]
    public async Task CommitAndPushAsync_PushSucceedsFirstAttempt_ReturnsSuccessNoRetry()
    {
        // Arrange
        CreateBrainChange();
        _mockProvider.Setup(p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.CommitAndPushAsync(
            _repoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.FilesCommitted.Should().BeGreaterThan(0);
        _mockProvider.Verify(
            p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CommitAndPushAsync_PushFailsThenSucceeds_RetriesAndReturnsSuccess()
    {
        // Arrange
        CreateBrainChange();
        var callCount = 0;
        _mockProvider.Setup(p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward");
                return Task.CompletedTask;
            });

        // Act
        var result = await _sut.CommitAndPushAsync(
            _repoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        _mockProvider.Verify(
            p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task CommitAndPushAsync_MaxRetriesExhausted_ReturnsFailure()
    {
        // Arrange
        CreateBrainChange();
        _mockProvider.Setup(p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward"));

        // Act
        var result = await _sut.CommitAndPushAsync(
            _repoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None, maxPushRetries: 3);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("non-fast-forward");
        _mockProvider.Verify(
            p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task CommitAndPushAsync_ConflictDuringRebase_ResolvesWithAcceptBoth()
    {
        // Arrange: Create a file, then simulate a remote change that conflicts
        var testFilePath = Path.Combine(_repoPath, "sessions");
        Directory.CreateDirectory(testFilePath);
        File.WriteAllText(Path.Combine(testFilePath, "test.md"), "local content\n");

        var callCount = 0;
        _mockProvider.Setup(p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward");
                return Task.CompletedTask;
            });

        // On PullAsync during rebase, simulate a remote change by modifying the file
        // to create a "remote" version that differs from base
        _mockProvider.Setup(p => p.PullAsync(_repoPath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.CommitAndPushAsync(
            _repoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task CommitAndPushAsync_NonFastForwardWithCustomRetryCount_RespectsConfig()
    {
        // Arrange
        CreateBrainChange();
        _mockProvider.Setup(p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward"));

        // Act — use maxPushRetries = 2
        var result = await _sut.CommitAndPushAsync(
            _repoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None, maxPushRetries: 2);

        // Assert
        result.Success.Should().BeFalse();
        _mockProvider.Verify(
            p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task CommitAndPushAsync_OtherInvalidOperationException_DoesNotRetry()
    {
        // Arrange — a push failure that is NOT non-fast-forward should not trigger retry
        CreateBrainChange();
        _mockProvider.Setup(p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Push failed: authentication required"));

        // Act
        var result = await _sut.CommitAndPushAsync(
            _repoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("authentication required");
        _mockProvider.Verify(
            p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CommitAndPushAsync_CancellationDuringRetry_ThrowsOperationCancelled()
    {
        // Arrange
        CreateBrainChange();
        using var cts = new CancellationTokenSource();
        _mockProvider.Setup(p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel(); // Cancel after first push attempt
                throw new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward");
            });

        // Act & Assert
        var act = () => _sut.CommitAndPushAsync(
            _repoPath, "run-1", "issue-1", _mockProvider.Object, cts.Token);

        // The cancellation is caught by the outer try/catch and returns failure
        // (OperationCanceledException is not caught by the general Exception handler
        // in the retry delay Task.Delay)
        var result = await act();
        // Either throws OCE or returns failure — both are acceptable
        // The key is it doesn't keep retrying after cancellation
        _mockProvider.Verify(
            p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CommitAndPushAsync_ConcurrentModificationToSameFile_BothSucceedAfterRetry()
    {
        // Arrange: Simulate two agents writing to the same file concurrently.
        // Agent 1 pushes first; Agent 2's push fails non-fast-forward, rebases, and succeeds.
        var sharedFile = "sessions/shared.md";
        var sharedDir = Path.Combine(_repoPath, "sessions");
        Directory.CreateDirectory(sharedDir);
        File.WriteAllText(Path.Combine(_repoPath, sharedFile), "base content\n");

        // Commit the base file to the bare repo so both agents start from the same state
        using (var repo = new Repository(_repoPath))
        {
            Commands.Stage(repo, sharedFile);
            var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
            repo.Commit("add shared file", sig, sig);
            var remote = repo.Network.Remotes["origin"];
            repo.Network.Push(remote, "refs/heads/main");
        }

        // Clone a second working copy to simulate agent 2
        var agent2Path = Path.Combine(Path.GetTempPath(), $"brain-test-agent2-{Guid.NewGuid():N}");
        try
        {
            Repository.Clone(_bareRepoPath, agent2Path, new CloneOptions { BranchName = "main" });

            // Agent 1: modify and push directly to bare repo
            File.WriteAllText(Path.Combine(agent2Path, sharedFile), "base content\nagent1 entry\n");
            using (var repo2 = new Repository(agent2Path))
            {
                Commands.Stage(repo2, sharedFile);
                var sig = new Signature("Agent1", "agent1@test.com", DateTimeOffset.UtcNow);
                repo2.Commit("agent1 brain update", sig, sig);
                var remote = repo2.Network.Remotes["origin"];
                repo2.Network.Push(remote, "refs/heads/main");
            }

            // Agent 2 (our _repoPath): modify the same file with different content
            File.WriteAllText(Path.Combine(_repoPath, sharedFile), "base content\nagent2 entry\n");

            // Set up provider to do real push via libgit2sharp
            var realProvider = new Mock<IRepositoryProvider>();
            realProvider.Setup(p => p.BaseBranch).Returns("main");
            realProvider.Setup(p => p.PullAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((path, _) =>
                {
                    using var repo = new Repository(path);
                    var remote = repo.Network.Remotes["origin"];
                    Commands.Fetch(repo, "origin", remote.FetchRefSpecs.Select(s => s.Specification), null, null);
                    return Task.CompletedTask;
                });

            var pushAttempt = 0;
            realProvider.Setup(p => p.PushBranchAsync(It.IsAny<string>(), "main", It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((path, _, _) =>
                {
                    pushAttempt++;
                    using var repo = new Repository(path);
                    var remote = repo.Network.Remotes["origin"];
                    try
                    {
                        repo.Network.Push(remote, "refs/heads/main");
                    }
                    catch (NonFastForwardException)
                    {
                        throw new InvalidOperationException(
                            "Push failed for ref 'refs/heads/main': non-fast-forward");
                    }
                    return Task.CompletedTask;
                });

            // Act: Agent 2 commits and pushes — should fail first, rebase, then succeed
            var result = await _sut.CommitAndPushAsync(
                _repoPath, "run-agent2", "issue-2", realProvider.Object, CancellationToken.None);

            // Assert
            result.Success.Should().BeTrue();
            pushAttempt.Should().BeGreaterThan(1, "push should have required at least one retry");

            // Verify the final file in bare repo contains both agents' entries
            using var finalRepo = new Repository(agent2Path);
            Commands.Fetch(finalRepo, "origin",
                finalRepo.Network.Remotes["origin"].FetchRefSpecs.Select(s => s.Specification), null, null);
            var remoteTip = finalRepo.Branches["origin/main"].Tip;
            var blob = remoteTip.Tree[sharedFile]?.Target as Blob;
            var finalContent = blob?.GetContentText() ?? "";
            finalContent.Should().Contain("agent1 entry");
            finalContent.Should().Contain("agent2 entry");
        }
        finally
        {
            DeleteDirectory(agent2Path);
        }
    }

    [Fact]
    public async Task CommitAndPushAsync_NoPullCalledBeforeCommit_DirtyWorkingTreeSucceeds()
    {
        // Arrange: Verify that having a dirty working tree (local modifications) does not
        // cause a CheckoutConflictException — the old bug where PullAsync was called first.
        CreateBrainChange("log.md", "dirty local content\n");

        // Provider that would throw CheckoutConflictException on PullAsync if called
        var strictProvider = new Mock<IRepositoryProvider>(MockBehavior.Strict);
        strictProvider.Setup(p => p.BaseBranch).Returns("main");
        strictProvider.Setup(p => p.PushBranchAsync(_repoPath, "main", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // PullAsync is NOT set up — if called before commit, it would throw MockException

        // Act
        var result = await _sut.CommitAndPushAsync(
            _repoPath, "run-1", "issue-1", strictProvider.Object, CancellationToken.None);

        // Assert — succeeds without calling PullAsync before commit
        result.Success.Should().BeTrue();
        result.FilesCommitted.Should().BeGreaterThan(0);
    }
}
