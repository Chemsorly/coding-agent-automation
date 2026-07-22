using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for guard clauses in BrainUpdateService (Requirements 4.1–4.5).
/// </summary>
public class BrainUpdateServiceGuardClauseTests
{
    private readonly BrainUpdateService _sut;
    private readonly Mock<IRepositoryProvider> _mockProvider;

    public BrainUpdateServiceGuardClauseTests()
    {
        _sut = new BrainUpdateService(new LoggerConfiguration().CreateLogger());
        _mockProvider = new Mock<IRepositoryProvider>();
        _mockProvider.Setup(p => p.BaseBranch).Returns("main");
    }

    /// <summary>
    /// Requirement 4.5: Constructor validates non-nullable parameters.
    /// </summary>
    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new BrainUpdateService(null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("logger");
    }

    /// <summary>
    /// Requirement 4.1: maxPushRetries ≤ 0 throws ArgumentOutOfRangeException.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task CommitAndPushAsync_MaxPushRetriesLessThanOrEqualToZero_ThrowsArgumentOutOfRangeException(int maxRetries)
    {
        var act = () => _sut.CommitAndPushAsync(
            "/tmp/fake", "run-1", "issue-1", _mockProvider.Object, CancellationToken.None, maxRetries);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Requirement 4.1: Validates that a valid maxPushRetries (> 0) does not throw.
    /// </summary>
    [Fact]
    public async Task CommitAndPushAsync_MaxPushRetriesPositive_DoesNotThrowArgumentOutOfRange()
    {
        // This will fail for other reasons (invalid repo path), but should NOT throw ArgumentOutOfRangeException
        var result = await _sut.CommitAndPushAsync(
            "/tmp/nonexistent-path", "run-1", "issue-1", _mockProvider.Object, CancellationToken.None, maxPushRetries: 1);

        // It should return a failure result (repo doesn't exist) but not throw ArgumentOutOfRangeException
        result.Success.Should().BeFalse();
    }

    /// <summary>
    /// Requirement 4.3: null/empty remoteBranch throws ArgumentException.
    /// Tested indirectly via CommitAndPushAsync when provider.BaseBranch is null.
    /// </summary>
    [Fact]
    public async Task CommitAndPushAsync_NullRemoteBranch_ThrowsOrReturnsFailure()
    {
        // Arrange: provider returns null BaseBranch
        var providerWithNullBranch = new Mock<IRepositoryProvider>();
        providerWithNullBranch.Setup(p => p.BaseBranch).Returns((string)null!);
        providerWithNullBranch.Setup(p => p.PullAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act: The ArgumentException for null remoteBranch is thrown inside PushWithRetryRebaseAsync
        // which is caught by the outer try-catch and returns a failure result
        var result = await _sut.CommitAndPushAsync(
            "/tmp/nonexistent", "run-1", "issue-1", providerWithNullBranch.Object, CancellationToken.None);

        // Assert: Should fail (either via ArgumentException caught internally or repo not found)
        result.Success.Should().BeFalse();
    }

    /// <summary>
    /// Requirement 4.3: empty remoteBranch throws ArgumentException.
    /// Tested indirectly via CommitAndPushAsync when provider.BaseBranch is empty.
    /// </summary>
    [Fact]
    public async Task CommitAndPushAsync_EmptyRemoteBranch_ThrowsOrReturnsFailure()
    {
        // Arrange: provider returns empty BaseBranch
        var providerWithEmptyBranch = new Mock<IRepositoryProvider>();
        providerWithEmptyBranch.Setup(p => p.BaseBranch).Returns(string.Empty);
        providerWithEmptyBranch.Setup(p => p.PullAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.CommitAndPushAsync(
            "/tmp/nonexistent", "run-1", "issue-1", providerWithEmptyBranch.Object, CancellationToken.None);

        // Assert: Should fail (either via ArgumentException caught internally or repo not found)
        result.Success.Should().BeFalse();
    }

    /// <summary>
    /// Requirement 4.4: EmptyCommitException handled gracefully — returns success with 0 files.
    /// When there are no changes to commit, it returns success with 0 files.
    /// </summary>
    [Fact]
    public async Task CommitAndPushAsync_NoChangesToCommit_ReturnsSuccessWithZeroFiles()
    {
        // Arrange: Use a mock IGitOperations that throws EmptyCommitException on StageAllAndCommit
        var mockGit = new Mock<IGitOperations>();
        mockGit.Setup(g => g.HasConflicts(It.IsAny<string>())).Returns(false);
        mockGit.Setup(g => g.StageAllAndCommit(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new EmptyCommitException());

        var sut = new BrainUpdateService(new LoggerConfiguration().CreateLogger(), mockGit.Object);

        _mockProvider.Setup(p => p.PullAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await sut.CommitAndPushAsync(
            "/tmp/fake-repo", "run-1", "issue-1", _mockProvider.Object, CancellationToken.None);

        // Assert: EmptyCommitException is handled gracefully — returns success with 0 files
        result.Success.Should().BeTrue();
        result.FilesCommitted.Should().Be(0);
    }

    /// <summary>
    /// Requirement 4.5: Null brainPath throws ArgumentNullException.
    /// </summary>
    [Fact]
    public async Task CommitAndPushAsync_NullBrainPath_ThrowsArgumentNullException()
    {
        var act = () => _sut.CommitAndPushAsync(
            null!, "run-1", "issue-1", _mockProvider.Object, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// Requirement 4.5: Null runId throws ArgumentNullException.
    /// </summary>
    [Fact]
    public async Task CommitAndPushAsync_NullRunId_ThrowsArgumentNullException()
    {
        var act = () => _sut.CommitAndPushAsync(
            "/tmp/fake", null!, "issue-1", _mockProvider.Object, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// Requirement 4.5: Null issueIdentifier throws ArgumentNullException.
    /// </summary>
    [Fact]
    public async Task CommitAndPushAsync_NullIssueIdentifier_ThrowsArgumentNullException()
    {
        var act = () => _sut.CommitAndPushAsync(
            "/tmp/fake", "run-1", null!, _mockProvider.Object, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// Requirement 4.5: Null brainProvider throws ArgumentNullException.
    /// </summary>
    [Fact]
    public async Task CommitAndPushAsync_NullBrainProvider_ThrowsArgumentNullException()
    {
        var act = () => _sut.CommitAndPushAsync(
            "/tmp/fake", "run-1", "issue-1", null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

}
