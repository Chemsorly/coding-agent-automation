namespace CodingAgent.Infrastructure.UnitTests.Git;

using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Moq;
using Xunit;

/// <summary>
/// Tests for <see cref="BrainUpdateService.CommitInboxAndPushAsync"/> on <see cref="BrainRepoFixture"/>
/// (spec 054, Tasks 6/8A.3 — BrainUpdateServiceInboxTests). No Category trait (T12).
/// </summary>
public sealed class BrainUpdateServiceInboxTests : IDisposable
{
    private readonly BrainRepoFixture _fixture;
    private readonly BrainUpdateService _sut;

    public BrainUpdateServiceInboxTests()
    {
        _fixture = BrainRepoFixture.Create(
            new Dictionary<string, string> { ["README.md"] = BrainLayout.ReadmePath + "\n" });
        _sut = new BrainUpdateService(new Serilog.LoggerConfiguration().CreateLogger());
    }

    [Fact]
    public async Task CommitInboxAndPush_NewInboxFile_AppearsInRemote()
    {
        // Arrange: clone the brain, write an inbox file
        var provider = _fixture.Provider();
        var clonePath = _fixture.Clone("clone1");
        var inboxRelative = BrainLayout.InboxFileName(DateOnly.FromDateTime(DateTime.UtcNow), "run-inbox-1");
        var fullPath = Path.Combine(clonePath, inboxRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "<!-- brain:v2 inbox -->\n- a lesson\n");

        // Act
        var result = await _sut.CommitInboxAndPushAsync(
            clonePath, inboxRelative,
            BrainLayout.InboxCommitMessage("run-inbox-1", "#1"),
            provider.Object, CancellationToken.None);

        // Assert: push succeeded and file is in the remote
        result.Success.Should().BeTrue("inbox push must succeed on a clean remote");
        var remote = _fixture.RemoteFiles();
        remote.Should().ContainKey(inboxRelative, "inbox file must appear in the remote after push");
        remote[inboxRelative].Should().Contain("brain:v2 inbox");
    }

    [Fact]
    public async Task CommitInboxAndPush_ConflictRetry_BothCommitsLand()
    {
        // Clone 1 pushes first, Clone 2 (stale) gets a non-fast-forward → retries and merges.
        var provider = _fixture.Provider();
        var clone1 = _fixture.Clone("c1");
        var clone2 = _fixture.Clone("c2");

        // Clone 1 pushes an inbox file
        var path1 = BrainLayout.InboxFileName(DateOnly.FromDateTime(DateTime.UtcNow), "run-c1");
        var full1 = Path.Combine(clone1, path1.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full1)!);
        await File.WriteAllTextAsync(full1, $"<!-- brain:v2 inbox -->\nrun c1\n");
        _fixture.CommitAndPush(clone1, new Dictionary<string, string?> { [path1] = $"<!-- brain:v2 inbox -->\nrun c1\n" }, "brain: inbox run-c1");

        // Clone 2 is now stale — CommitInboxAndPushAsync should retry
        var path2 = BrainLayout.InboxFileName(DateOnly.FromDateTime(DateTime.UtcNow.AddSeconds(1)), "run-c2");
        var full2 = Path.Combine(clone2, path2.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full2)!);
        await File.WriteAllTextAsync(full2, $"<!-- brain:v2 inbox -->\nrun c2\n");

        var result = await _sut.CommitInboxAndPushAsync(
            clone2, path2,
            BrainLayout.InboxCommitMessage("run-c2", "#2"),
            provider.Object, CancellationToken.None, maxPushRetries: 3);

        result.Success.Should().BeTrue("retry-push must succeed after rebasing onto clone1's commit");
        var remote = _fixture.RemoteFiles();
        remote.Should().ContainKey(path2, "clone2 inbox file must land in the remote after retry");
    }

    [Fact]
    public async Task CommitInboxAndPush_AllRetriesExhausted_ReturnsFailure()
    {
        // Use a provider whose push always fails with non-fast-forward
        var provider = new Mock<IRepositoryProvider>();
        provider.Setup(p => p.BaseBranch).Returns("main");
        provider.Setup(p => p.PullAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        provider.Setup(p => p.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Push failed for ref 'refs/heads/main': non-fast-forward"));

        var clone = _fixture.Clone("clone-fail");
        var rel = BrainLayout.InboxFileName(DateOnly.FromDateTime(DateTime.UtcNow), "run-fail");
        var full = Path.Combine(clone, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, "<!-- brain:v2 inbox -->\n");

        var result = await _sut.CommitInboxAndPushAsync(
            clone, rel, "brain: fail", provider.Object, CancellationToken.None, maxPushRetries: 1);

        result.Success.Should().BeFalse("exhausted retries must return a failure result (T6)");
    }

    public void Dispose() => _fixture.Dispose();
}
