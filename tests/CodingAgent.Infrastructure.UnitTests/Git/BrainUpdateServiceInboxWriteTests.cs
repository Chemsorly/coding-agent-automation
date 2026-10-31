using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Moq;
using Serilog;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Unit tests for <see cref="BrainUpdateService.WriteInboxFileAsync"/>.
/// Spec 054 — replaces the deleted BrainUpdateServiceInboxTests (CommitInboxAndPushAsync).
/// No Category trait (T12).
/// </summary>
public class BrainUpdateServiceInboxWriteTests : IDisposable
{
    private readonly BrainUpdateService _sut;
    private readonly BrainRepoFixture _fixture;

    public BrainUpdateServiceInboxWriteTests()
    {
        _fixture = BrainRepoFixture.Create(
            new Dictionary<string, string>
            {
                ["README.md"] = "# Brain\n",
                ["general/SKILL.md"] = "<!-- brain:v2 skill -->\n"
            });
        _sut = new BrainUpdateService(new LoggerConfiguration().CreateLogger());
    }

    public void Dispose() => _fixture.Dispose();

    // ── Happy paths ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteInboxFile_Success_ReturnsTheCommit()
    {
        var provider = new Mock<IRepositoryProvider>();
        provider
            .Setup(p => p.WriteFileToBaseBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteFileWriteResult("c1"));

        var result = await _sut.WriteInboxFileAsync(
            "inbox/2026-01-01_test.md", "text", "brain: inbox test",
            provider.Object, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.CommitSha.Should().Be("c1");
        result.FilesCommitted.Should().Be(1);
    }

    [Fact]
    public async Task WriteInboxFile_ConflictOnce_RetriesAndSucceeds()
    {
        var callCount = 0;
        var provider = new Mock<IRepositoryProvider>();
        provider
            .Setup(p => p.WriteFileToBaseBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new RemoteFileConflictException("conflict", new Exception());
                return new RemoteFileWriteResult("c2");
            });

        var result = await _sut.WriteInboxFileAsync(
            "inbox/2026-01-01_test.md", "text", "msg",
            provider.Object, CancellationToken.None, maxPushRetries: 3);

        result.Success.Should().BeTrue();
        provider.Verify(p => p.WriteFileToBaseBranchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "should retry once after a conflict");
    }

    [Fact]
    public async Task WriteInboxFile_ConflictEveryTime_ReturnsFailure()
    {
        var provider = new Mock<IRepositoryProvider>();
        provider
            .Setup(p => p.WriteFileToBaseBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RemoteFileConflictException("always conflict", new Exception()));

        var result = await _sut.WriteInboxFileAsync(
            "inbox/2026-01-01_test.md", "text", "msg",
            provider.Object, CancellationToken.None, maxPushRetries: 2);

        result.Success.Should().BeFalse();
        provider.Verify(p => p.WriteFileToBaseBranchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "with maxPushRetries=2, exactly 2 attempts should be made");
    }

    [Fact]
    public async Task WriteInboxFile_OtherFailure_ReturnsFailureWithoutRetry()
    {
        var provider = new Mock<IRepositoryProvider>();
        provider
            .Setup(p => p.WriteFileToBaseBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network error"));

        var result = await _sut.WriteInboxFileAsync(
            "inbox/2026-01-01_test.md", "text", "msg",
            provider.Object, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("network error");
        provider.Verify(p => p.WriteFileToBaseBranchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once, "non-conflict exceptions must not be retried");
    }

    [Fact]
    public async Task WriteInboxFile_Cancelled_Propagates()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();  // cancel before the call so the mock can observe it

        var provider = new Mock<IRepositoryProvider>();
        provider
            .Setup(p => p.WriteFileToBaseBranchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.Is<CancellationToken>(t => t.IsCancellationRequested)))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.WriteInboxFileAsync(
                "inbox/2026-01-01_test.md", "text", "msg",
                provider.Object, cts.Token));

        // Verify the token was actually forwarded — if the production code passed CancellationToken.None
        // the matcher above would not trigger and the mock would return a null result instead of throwing.
        provider.Verify(p => p.WriteFileToBaseBranchAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.Is<CancellationToken>(t => t.IsCancellationRequested)),
            Times.Once, "the cancellation token must be forwarded to WriteFileToBaseBranchAsync");
    }

    [Fact]
    public async Task WriteInboxFile_NeverPushesThroughGit()
    {
        // The new path must never call PushBranchAsync or PullAsync on the provider.
        var provider = _fixture.Provider();
        provider
            .Setup(p => p.WriteFileToBaseBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteFileWriteResult("sha-via-api"));

        await _sut.WriteInboxFileAsync(
            "inbox/2026-01-01_test.md", "text", "msg",
            provider.Object, CancellationToken.None);

        provider.Verify(p => p.PushBranchAsync(
            It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()),
            Times.Never, "WriteInboxFileAsync must never push through git");
        provider.Verify(p => p.PushBranchAsync(
            It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        provider.Verify(p => p.PullAsync(
            It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()),
            Times.Never, "WriteInboxFileAsync must never pull through git");
    }

    [Fact]
    public async Task WriteInboxFile_ConcurrentWrites_AllLand()
    {
        // 5 parallel writes with different paths. Per-path: first attempt throws RemoteFileConflictException,
        // second calls fixture.WriteFileOnRemote. Uses a separate Mock (not fixture.Provider()) because
        // fixture.Provider() always succeeds on the first attempt.
        var paths = Enumerable.Range(1, 5)
            .Select(i => $"inbox/2026-01-01_run{i}.md")
            .ToList();

        var callCounts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var provider = new Mock<IRepositoryProvider>();
        provider
            .Setup(p => p.WriteFileToBaseBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, string content, string msg, CancellationToken _) =>
            {
                var n = callCounts.AddOrUpdate(path, 1, (_, v) => v + 1);
                if (n == 1)
                    throw new RemoteFileConflictException($"first attempt conflict for {path}", new Exception());
                return Task.FromResult(new RemoteFileWriteResult(_fixture.WriteFileOnRemote(path, content, msg)));
            });

        var tasks = paths.Select(path =>
            _sut.WriteInboxFileAsync(path, $"content for {path}", "msg", provider.Object, CancellationToken.None, maxPushRetries: 3));

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Success.Should().BeTrue());

        var remoteFiles = _fixture.RemoteFiles();
        foreach (var path in paths)
        {
            remoteFiles.Should().ContainKey(path, $"path {path} should have been written to the remote");
        }
    }
}
