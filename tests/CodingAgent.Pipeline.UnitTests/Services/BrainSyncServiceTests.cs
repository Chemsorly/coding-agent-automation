using AwesomeAssertions;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="BrainSyncService"/>: exercises the real
/// service body against mocked dependencies.
/// v1 SyncPostRunAsync tests deleted in spec 054 Task 6.3.
/// </summary>
public class BrainSyncServiceTests : IDisposable
{
    private readonly Mock<IBrainUpdateService> _brainUpdateService;
    private readonly Mock<IRepositoryProvider> _brainProvider;
    private readonly Mock<Serilog.ILogger> _logger;
    private readonly BrainSyncService _sut;

    public BrainSyncServiceTests()
    {
        _brainUpdateService = new Mock<IBrainUpdateService>();
        _brainProvider = new Mock<IRepositoryProvider>();
        _logger = new Mock<Serilog.ILogger>();

        _sut = new BrainSyncService(_brainUpdateService.Object, _logger.Object);
    }

    public void Dispose() { }

    // ── WriteInboxAsync — run field assignments (spec 054, req 6, T6) ────────

    [Fact]
    public async Task WriteInboxAsync_SuccessPath_SetsRunFields()
    {
        // Arrange
        var run = CreateRun();
        var rel = "inbox/2026-01-01_test.md";

        _brainUpdateService
            .Setup(s => s.WriteInboxFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new BrainSyncResult { Success = true, FilesCommitted = 1, CommitSha = "deadbeef" });
        _brainProvider.Setup(p => p.GetCommitWebUrl(It.IsAny<string>())).Returns("https://github.com/org/brain/commit/deadbeef");

        // Act
        await _sut.WriteInboxAsync(run, _brainProvider.Object, rel, "text", CancellationToken.None);

        // Assert: success path sets all fields
        run.BrainInboxFile.Should().Be(rel, "BrainInboxFile must be set to the relative path on success");
        run.BrainUpdatesPushed.Should().BeTrue("BrainUpdatesPushed must be true when push succeeds");
        run.BrainPushFailed.Should().BeFalse("BrainPushFailed must be false when push succeeds");
        run.BrainInboxFileUrl.Should().Be("https://github.com/org/brain/commit/deadbeef");
    }

    [Fact]
    public async Task WriteInboxAsync_PushFails_SetsFailureFields()
    {
        var run = CreateRun();
        var rel = "inbox/2026-01-01_test.md";

        _brainUpdateService
            .Setup(s => s.WriteInboxFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new BrainSyncResult { Success = false, ErrorMessage = "conflict" });

        await _sut.WriteInboxAsync(run, _brainProvider.Object, rel, "text", CancellationToken.None);

        run.BrainInboxFile.Should().Be(rel, "BrainInboxFile must still be set even when push fails");
        run.BrainUpdatesPushed.Should().BeFalse("BrainUpdatesPushed must be false when push fails");
        run.BrainPushFailed.Should().BeTrue("BrainPushFailed must be true when push fails");
    }

    [Fact]
    public async Task WriteInboxAsync_CommitThrows_SetsFailureFieldsAndDoesNotThrow()
    {
        // T6: WriteInboxAsync is non-fatal — must catch exceptions and set failure flags.
        var run = CreateRun();
        var rel = "inbox/2026-01-01_test.md";

        _brainUpdateService
            .Setup(s => s.WriteInboxFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("unexpected failure"));

        var act = () => _sut.WriteInboxAsync(run, _brainProvider.Object, rel, "text", CancellationToken.None);
        await act.Should().NotThrowAsync("WriteInboxAsync must be non-fatal (T6)");

        run.BrainUpdatesPushed.Should().BeFalse();
        run.BrainPushFailed.Should().BeTrue("BrainPushFailed must be true when an exception is caught");
    }

    [Fact]
    public async Task WriteInboxAsync_PassesExactPathTextAndMessage()
    {
        var run = CreateRun();
        var rel = "inbox/2026-01-01_test.md";
        var text = "candidate text";
        var expectedMessage = BrainLayout.InboxCommitMessage(run.RunId, $"#{run.IssueIdentifier}");

        _brainUpdateService
            .Setup(s => s.WriteInboxFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new BrainSyncResult { Success = true, FilesCommitted = 1, CommitSha = "abc123" });

        await _sut.WriteInboxAsync(run, _brainProvider.Object, rel, text, CancellationToken.None);

        _brainUpdateService.Verify(s => s.WriteInboxFileAsync(
            rel,
            text,
            expectedMessage,
            _brainProvider.Object,
            It.IsAny<CancellationToken>(),
            It.IsAny<int>()),
            Times.Once);
    }

    [Fact]
    public async Task WriteInboxAsync_CommitUrlComesFromTheWriteResult()
    {
        var run = CreateRun();
        var rel = "inbox/2026-01-01_test.md";
        var expectedUrl = "https://github.com/org/brain/commit/deadbeef";

        _brainUpdateService
            .Setup(s => s.WriteInboxFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new BrainSyncResult { Success = true, FilesCommitted = 1, CommitSha = "deadbeef" });
        _brainProvider.Setup(p => p.GetCommitWebUrl("deadbeef")).Returns(expectedUrl);

        await _sut.WriteInboxAsync(run, _brainProvider.Object, rel, "text", CancellationToken.None);

        run.BrainInboxFileUrl.Should().Be(expectedUrl);
        // GetHeadCommitShaAsync must never be called — URL comes from WriteInboxFileAsync result
        _brainProvider.Verify(p => p.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WriteInboxAsync_DoesNotTouchTheWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"bss-nowrites-{Guid.NewGuid():N}");
        var run = CreateRun();
        run.WorkspacePath = workspace; // non-existent directory

        var rel = "inbox/2026-01-01_test.md";

        _brainUpdateService
            .Setup(s => s.WriteInboxFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new BrainSyncResult { Success = true, FilesCommitted = 1, CommitSha = "abc" });

        await _sut.WriteInboxAsync(run, _brainProvider.Object, rel, "text", CancellationToken.None);

        run.BrainUpdatesPushed.Should().BeTrue();
        Directory.Exists(workspace).Should().BeFalse("WriteInboxAsync must not create any local directories");
    }

    [Fact]
    public async Task WriteInboxAsync_KeepsTextInMemory_WhenWriteFails()
    {
        var run = CreateRun();
        var rel = "inbox/2026-01-01_test.md";
        var text = "important candidate";

        _brainUpdateService
            .Setup(s => s.WriteInboxFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new BrainSyncResult { Success = false, ErrorMessage = "network error" });

        await _sut.WriteInboxAsync(run, _brainProvider.Object, rel, text, CancellationToken.None);

        run.BrainInboxText.Should().Be(text, "BrainInboxText must be preserved even when the write fails (DD8)");
        run.BrainInboxFile.Should().Be(rel, "BrainInboxFile must be set for rewrite tracking even on failure");
    }

    [Theory]
    [InlineData("general/SKILL.md")]
    [InlineData("inbox/../general/SKILL.md")]
    [InlineData("inbox/a/b.md")]
    [InlineData("README.md")]
    public async Task WriteInboxAsync_PathOutsideInbox_IsRefused(string invalidPath)
    {
        var run = CreateRun();

        await _sut.WriteInboxAsync(run, _brainProvider.Object, invalidPath, "text", CancellationToken.None);

        _brainUpdateService.Verify(s => s.WriteInboxFileAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()),
            Times.Never,
            $"path '{invalidPath}' must be refused before calling WriteInboxFileAsync");
        run.BrainPushFailed.Should().BeTrue();
        run.BrainUpdatesPushed.Should().BeFalse();
        run.BrainInboxFile.Should().BeNull("BrainInboxFile must stay null when the path is refused");
        run.BrainInboxText.Should().BeNull("BrainInboxText must NOT be set when the path is refused (guard fires before the assignment)");
    }

    [Fact]
    public async Task WriteInboxAsync_DifferentPathThanEarlierWrite_IsRefused()
    {
        var run = CreateRun();
        run.BrainInboxFile = "inbox/2026-01-01_a.md";
        run.BrainInboxText = "original candidates from first write";

        await _sut.WriteInboxAsync(run, _brainProvider.Object, "inbox/2026-01-01_b.md", "second write text", CancellationToken.None);

        _brainUpdateService.Verify(s => s.WriteInboxFileAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()),
            Times.Never);
        run.BrainInboxFile.Should().Be("inbox/2026-01-01_a.md", "BrainInboxFile must remain unchanged when refused");
        run.BrainInboxText.Should().Be("original candidates from first write",
            "BrainInboxText must NOT be overwritten when a different-path write is refused (DD8)");
    }

    private static PipelineRun CreateRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "owner/repo#1",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        StartedAt = DateTime.UtcNow,
        WorkspacePath = Path.GetTempPath(),
    };

    // ── SyncPreRunAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SyncPreRunAsync_WithWorkspacePath_ClonesOrPullsBrainIntoSubdirectory()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"brain-pre-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        WorkspacePath workspacePath = workspace;

        var run = CreateRun();
        run.WorkspacePath = workspace;

        _brainProvider
            .Setup(p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        try
        {
            await _sut.SyncPreRunAsync(run, _brainProvider.Object, workspacePath, CancellationToken.None);

            var expectedBrainPath = Path.Combine(workspace, ".brain");
            _brainProvider.Verify(
                p => p.CloneAsync(It.Is<WorkspacePath>(w => w.Value == expectedBrainPath), It.IsAny<CancellationToken>()),
                Times.Once,
                "SyncPreRunAsync should derive the brain path as workspacePath/.brain");

            run.BrainContextLoaded.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task SyncPreRunAsync_WhenBrainDirectoryExists_PullsInsteadOfClones()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"brain-pre-test-{Guid.NewGuid():N}");
        var brainPath = Path.Combine(workspace, ".brain");
        Directory.CreateDirectory(brainPath);
        WorkspacePath workspacePath = workspace;

        var run = CreateRun();
        run.WorkspacePath = workspace;

        _brainProvider
            .Setup(p => p.PullAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        try
        {
            await _sut.SyncPreRunAsync(run, _brainProvider.Object, workspacePath, CancellationToken.None);

            var expectedBrainPath = Path.Combine(workspace, ".brain");
            _brainProvider.Verify(
                p => p.PullAsync(It.Is<WorkspacePath>(w => w.Value == expectedBrainPath), It.IsAny<CancellationToken>()),
                Times.Once,
                "SyncPreRunAsync should pull when .brain directory already exists");
            _brainProvider.Verify(
                p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }
}
