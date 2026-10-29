using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using AwesomeAssertions;

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
        var workspace = Path.Combine(Path.GetTempPath(), $"bss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspace, ".brain")); // brain path must exist
        var run = CreateRun();
        run.WorkspacePath = workspace;
        var rel = "inbox/2026-01-01_test.md";

        _brainUpdateService
            .Setup(s => s.CommitInboxAndPushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new BrainSyncResult { Success = true, FilesCommitted = 1 });
        _brainProvider.Setup(p => p.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("deadbeef");
        _brainProvider.Setup(p => p.GetCommitWebUrl(It.IsAny<string>())).Returns("https://github.com/org/brain/commit/deadbeef");

        try
        {
            // Act
            await _sut.WriteInboxAsync(run, _brainProvider.Object, rel, "text", CancellationToken.None);

            // Assert: success path sets all fields
            run.BrainInboxFile.Should().Be(rel, "BrainInboxFile must be set to the relative path on success");
            run.BrainUpdatesPushed.Should().BeTrue("BrainUpdatesPushed must be true when push succeeds");
            run.BrainPushFailed.Should().BeFalse("BrainPushFailed must be false when push succeeds");
            run.BrainInboxFileUrl.Should().Be("https://github.com/org/brain/commit/deadbeef");
        }
        finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); }
    }

    [Fact]
    public async Task WriteInboxAsync_PushFails_SetsFailureFields()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"bss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspace, ".brain"));
        var run = CreateRun();
        run.WorkspacePath = workspace;
        var rel = "inbox/2026-01-01_test.md";

        _brainUpdateService
            .Setup(s => s.CommitInboxAndPushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new BrainSyncResult { Success = false, ErrorMessage = "conflict" });

        try
        {
            await _sut.WriteInboxAsync(run, _brainProvider.Object, rel, "text", CancellationToken.None);

            run.BrainInboxFile.Should().Be(rel, "BrainInboxFile must still be set even when push fails");
            run.BrainUpdatesPushed.Should().BeFalse("BrainUpdatesPushed must be false when push fails");
            run.BrainPushFailed.Should().BeTrue("BrainPushFailed must be true when push fails");
        }
        finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); }
    }

    [Fact]
    public async Task WriteInboxAsync_CommitThrows_SetsFailureFieldsAndDoesNotThrow()
    {
        // T6: WriteInboxAsync is non-fatal — must catch exceptions and set failure flags.
        var workspace = Path.Combine(Path.GetTempPath(), $"bss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspace, ".brain"));
        var run = CreateRun();
        run.WorkspacePath = workspace;
        var rel = "inbox/2026-01-01_test.md";

        _brainUpdateService
            .Setup(s => s.CommitInboxAndPushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("unexpected failure"));

        try
        {
            var act = () => _sut.WriteInboxAsync(run, _brainProvider.Object, rel, "text", CancellationToken.None);
            await act.Should().NotThrowAsync("WriteInboxAsync must be non-fatal (T6)");

            run.BrainUpdatesPushed.Should().BeFalse();
            run.BrainPushFailed.Should().BeTrue("BrainPushFailed must be true when an exception is caught");
        }
        finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); }
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
