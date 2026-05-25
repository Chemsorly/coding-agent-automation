using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Git;

public class BrainUpdateServicePushRetryTests
{
    private readonly Mock<IRepositoryProvider> _mockProvider;
    private readonly Mock<IGitOperations> _mockGit;
    private readonly BrainUpdateService _sut;
    private const string RepoPath = "/fake/brain/repo";

    public BrainUpdateServicePushRetryTests()
    {
        _mockProvider = new Mock<IRepositoryProvider>();
        _mockProvider.Setup(p => p.BaseBranch).Returns("main");
        _mockProvider.Setup(p => p.PullAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockGit = new Mock<IGitOperations>();
        // Default: no conflicts, commit succeeds, 1 file committed
        _mockGit.Setup(g => g.HasConflicts(It.IsAny<string>())).Returns(false);
        _mockGit.Setup(g => g.GetHeadCommitFileCount(It.IsAny<string>())).Returns(1);

        _sut = new BrainUpdateService(new LoggerConfiguration().CreateLogger(), _mockGit.Object);
    }

    [Fact]
    public async Task CommitAndPushAsync_PushSucceedsFirstAttempt_ReturnsSuccessNoRetry()
    {
        _mockProvider.Setup(p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.CommitAndPushAsync(
            RepoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.FilesCommitted.Should().Be(1);
        _mockProvider.Verify(
            p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CommitAndPushAsync_PushFailsThenSucceeds_RetriesAndReturnsSuccess()
    {
        var callCount = 0;
        _mockProvider.Setup(p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward");
                return Task.CompletedTask;
            });

        // Rebase needs: GetHeadCommitChanges returns some changes, file content accessors
        SetupRebaseMocks();

        var result = await _sut.CommitAndPushAsync(
            RepoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        _mockProvider.Verify(
            p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task CommitAndPushAsync_MaxRetriesExhausted_ReturnsFailure()
    {
        _mockProvider.Setup(p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward"));

        SetupRebaseMocks();

        var result = await _sut.CommitAndPushAsync(
            RepoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None, maxPushRetries: 3);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("non-fast-forward");
        _mockProvider.Verify(
            p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task CommitAndPushAsync_ConflictDuringRebase_ResolvesWithAcceptBoth()
    {
        var callCount = 0;
        _mockProvider.Setup(p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward");
                return Task.CompletedTask;
            });

        // Set up rebase with a file that both sides modified
        _mockGit.Setup(g => g.GetHeadCommitChanges(RepoPath))
            .Returns(new[] { new FileChange("sessions/test.md", FileChangeStatus.Modified) });
        _mockGit.Setup(g => g.GetFileContentFromHead(RepoPath, "sessions/test.md"))
            .Returns("local content\n");
        _mockGit.Setup(g => g.GetFileContentFromHeadParent(RepoPath, "sessions/test.md"))
            .Returns("base content\n");

        var result = await _sut.CommitAndPushAsync(
            RepoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task CommitAndPushAsync_NonFastForwardWithCustomRetryCount_RespectsConfig()
    {
        _mockProvider.Setup(p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward"));

        SetupRebaseMocks();

        var result = await _sut.CommitAndPushAsync(
            RepoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None, maxPushRetries: 2);

        result.Success.Should().BeFalse();
        _mockProvider.Verify(
            p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task CommitAndPushAsync_OtherInvalidOperationException_DoesNotRetry()
    {
        _mockProvider.Setup(p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Push failed: authentication required"));

        var result = await _sut.CommitAndPushAsync(
            RepoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("authentication required");
        _mockProvider.Verify(
            p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CommitAndPushAsync_CancellationDuringRetry_DoesNotKeepRetrying()
    {
        using var cts = new CancellationTokenSource();
        _mockProvider.Setup(p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cts.Cancel();
                throw new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward");
            });

        SetupRebaseMocks();

        var result = await _sut.CommitAndPushAsync(
            RepoPath, "run-1", "issue-1", _mockProvider.Object, cts.Token);

        // Either returns failure or throws OCE — key is it doesn't keep retrying
        _mockProvider.Verify(
            p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CommitAndPushAsync_EmptyCommit_ReturnsSuccessWithZeroFiles()
    {
        _mockGit.Setup(g => g.StageAllAndCommit(RepoPath, It.IsAny<string>()))
            .Throws(new EmptyCommitException());

        var result = await _sut.CommitAndPushAsync(
            RepoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.FilesCommitted.Should().Be(0);
        // Push should NOT be called when there's nothing to commit
        _mockProvider.Verify(
            p => p.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CommitAndPushAsync_NoPullCalledBeforeCommit_PushCalledDirectly()
    {
        // Verify that PullAsync is NOT called before the initial commit+push
        // (the old bug where PullAsync was called first, causing CheckoutConflictException)
        _mockProvider.Setup(p => p.PushBranchAsync(RepoPath, "main", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.CommitAndPushAsync(
            RepoPath, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        // PullAsync should NOT be called on the happy path (only during rebase)
        _mockProvider.Verify(
            p => p.PullAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Sets up mock git operations for the rebase path (non-fast-forward retry).
    /// </summary>
    private void SetupRebaseMocks()
    {
        _mockGit.Setup(g => g.GetHeadCommitChanges(RepoPath))
            .Returns(new[] { new FileChange("log.md", FileChangeStatus.Modified) });
        _mockGit.Setup(g => g.GetFileContentFromHead(RepoPath, "log.md"))
            .Returns("new entry\n");
        _mockGit.Setup(g => g.GetFileContentFromHeadParent(RepoPath, "log.md"))
            .Returns("base content\n");
    }
}
