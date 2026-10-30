using System.Text;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for <see cref="BrainUpdateService.CommitInboxAndPushAsync"/> covering the
/// <c>ReplayLocalCommitsOntoRemoteAsync</c> path (non-fast-forward retry with file replay).
/// These tests verify that file writes on the replay path use the async overload and
/// that cancellation during a write propagates correctly.
/// </summary>
public sealed class BrainUpdateServiceReplayTests : IDisposable
{
    private readonly Mock<IGitOperations> _mockGit;
    private readonly Mock<IRepositoryProvider> _mockProvider;
    private readonly BrainUpdateService _sut;
    private readonly string _repoPath;

    public BrainUpdateServiceReplayTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"brain-replay-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);

        _mockGit = new Mock<IGitOperations>();
        _mockProvider = new Mock<IRepositoryProvider>();

        _mockProvider.Setup(p => p.BaseBranch).Returns("main");
        _mockProvider.Setup(p => p.PullAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new BrainUpdateService(new LoggerConfiguration().CreateLogger(), _mockGit.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoPath))
            Directory.Delete(_repoPath, recursive: true);
    }

    /// <summary>
    /// Sets up the common mock chain needed to reach the replay path:
    /// DiscardChangesExcept returns an empty list, StageAndCommit is a no-op,
    /// GetCommitsAheadOfRemote returns a single commit with one non-null file,
    /// ResetHardToRemote is a no-op.
    /// </summary>
    private void SetupReplayPath(string fileContent = "# inbox content\n")
    {
        _mockGit
            .Setup(g => g.DiscardChangesExcept(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Array.Empty<string>());

        _mockGit
            .Setup(g => g.StageAndCommit(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>()));

        _mockGit
            .Setup(g => g.GetCommitsAheadOfRemote(_repoPath, "main"))
            .Returns(new[]
            {
                new LocalCommit("abc123", "brain: inbox run-1",
                    new[] { new CommitFileContent("sessions/inbox.md", fileContent, null) })
            });

        _mockGit
            .Setup(g => g.ResetHardToRemote(It.IsAny<string>(), It.IsAny<string>()));
    }

    /// <summary>
    /// Configures the provider to reject the first push with non-fast-forward, then accept the second.
    /// </summary>
    private void SetupPushRejectedOnceThenAccepted()
    {
        var callCount = 0;
        _mockProvider
            .Setup(p => p.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward");
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task CommitInboxAndPushAsync_ReplayAfterNonFastForward_CallsWriteAllTextAsync()
    {
        // Arrange
        SetupReplayPath("# inbox content\n");
        SetupPushRejectedOnceThenAccepted();

        using var cts = new CancellationTokenSource();

        _mockGit
            .Setup(g => g.WriteAllTextAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Encoding>(),
                It.Is<CancellationToken>(t => t == cts.Token)))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.CommitInboxAndPushAsync(
            _repoPath, "sessions/inbox.md", "brain: inbox run-1",
            _mockProvider.Object, cts.Token);

        // Assert
        result.Success.Should().BeTrue("the push should succeed after the rebase-replay");

        // Verify the async overload is called with the caller's CancellationToken (not CancellationToken.None).
        // Using It.Is<CancellationToken>(t => t == cts.Token) instead of It.IsAny<>() ensures that
        // a regression replacing `ct` with `CancellationToken.None` would cause this verification to fail.
        _mockGit.Verify(
            g => g.WriteAllTextAsync(
                It.Is<string>(p => p.EndsWith("inbox.md")),
                "# inbox content\n",
                It.IsAny<Encoding>(),
                It.Is<CancellationToken>(t => t == cts.Token)),
            Times.Once,
            "WriteAllTextAsync must be called exactly once with the caller's CancellationToken forwarded");
    }

    [Fact]
    public async Task CommitInboxAndPushAsync_CancellationDuringFileWrite_PropagatesOperationCanceledException()
    {
        // Arrange
        SetupReplayPath("# inbox content\n");
        SetupPushRejectedOnceThenAccepted();

        using var cts = new CancellationTokenSource();

        // The mock only throws when the correct caller token is forwarded.
        // If the implementation incorrectly passes CancellationToken.None instead of ct,
        // this setup will not match and Moq will return Task.CompletedTask — the test then
        // fails because no OperationCanceledException is thrown.
        _mockGit
            .Setup(g => g.WriteAllTextAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Encoding>(),
                It.Is<CancellationToken>(t => t == cts.Token)))
            .ThrowsAsync(new OperationCanceledException());

        // Act
        var act = async () => await _sut.CommitInboxAndPushAsync(
            _repoPath, "sessions/inbox.md", "brain: inbox run-1",
            _mockProvider.Object, cts.Token);

        // Assert: OperationCanceledException must not be swallowed —
        // CommitInboxAndPushAsync has an explicit `catch (OperationCanceledException) { throw; }` guard.
        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation during a file write must propagate out of CommitInboxAndPushAsync");
    }
}
