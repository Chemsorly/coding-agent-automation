using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="BrainSyncService.SyncPostRunAsync"/>: exercises the real
/// service body against mocked <see cref="IBrainUpdateService"/> and verifies service logic.
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

    private static PipelineRun CreateRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "owner/repo#1",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}")
    };

    // ── Empty changes path ──────────────────────────────────────────────────

    [Fact]
    public async Task SyncPostRunAsync_WhenNoChanges_SetsBrainUpdatesPushedFalse()
    {
        var run = CreateRun();
        _brainUpdateService
            .Setup(s => s.DetectChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)Array.Empty<string>());

        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        run.BrainUpdatesPushed.Should().BeFalse();
    }

    [Fact]
    public async Task SyncPostRunAsync_WhenNoChanges_EmitsNoBrainChangesOutputLine()
    {
        var run = CreateRun();
        _brainUpdateService
            .Setup(s => s.DetectChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)Array.Empty<string>());

        var output = new List<string>();
        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None,
            onOutputLine: line => output.Add(line));

        output.Should().Contain(l => l.Contains("No brain changes detected"));
    }

    // ── Non-empty changes path — successful push ────────────────────────────

    [Fact]
    public async Task SyncPostRunAsync_WhenChangesDetected_SetsBrainUpdatesPushedTrue()
    {
        var run = CreateRun();
        SetupSuccessfulPush(changedFiles: ["lessons.md"], filesCommitted: 1);

        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        run.BrainUpdatesPushed.Should().BeTrue();
    }

    [Fact]
    public async Task SyncPostRunAsync_WhenChangesDetected_SetsBrainFilesCommitted()
    {
        var run = CreateRun();
        SetupSuccessfulPush(changedFiles: ["lessons.md", "log.md"], filesCommitted: 2);

        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        run.BrainFilesCommitted.Should().Be(2);
    }

    // ── Non-empty changes path — failed push ───────────────────────────────

    [Fact]
    public async Task SyncPostRunAsync_WhenPushFails_SetsBrainUpdatesPushedFalse()
    {
        var run = CreateRun();
        SetupFailedPush(changedFiles: ["lessons.md"]);

        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        run.BrainUpdatesPushed.Should().BeFalse();
    }

    // ── Fallback log entry when operation log not updated ──────────────────

    [Fact]
    public async Task SyncPostRunAsync_WhenOperationLogNotUpdated_AppendsFallbackLogEntry()
    {
        var run = CreateRun();
        var changedFiles = new[] { "sessions/2026-09-02_test.md" };

        _brainUpdateService
            .Setup(s => s.DetectChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)changedFiles);
        _brainUpdateService
            .Setup(s => s.Validate(It.IsAny<string>(), It.IsAny<RunId>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(new BrainValidationResult { OperationLogUpdated = false });
        _brainUpdateService
            .Setup(s => s.AppendFallbackLogEntryAsync(It.IsAny<string>(), It.IsAny<RunId>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _brainUpdateService
            .Setup(s => s.CommitAndPushAsync(It.IsAny<string>(), It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new BrainSyncResult { Success = true, FilesCommitted = 1 });

        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _brainUpdateService.Verify(s => s.AppendFallbackLogEntryAsync(
            It.IsAny<string>(),
            It.IsAny<RunId>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncPostRunAsync_WhenOperationLogIsUpdated_DoesNotAppendFallbackLogEntry()
    {
        var run = CreateRun();
        var changedFiles = new[] { "log.md", "sessions/2026-09-02_test.md" };

        _brainUpdateService
            .Setup(s => s.DetectChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)changedFiles);
        _brainUpdateService
            .Setup(s => s.Validate(It.IsAny<string>(), It.IsAny<RunId>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(new BrainValidationResult { OperationLogUpdated = true });
        _brainUpdateService
            .Setup(s => s.CommitAndPushAsync(It.IsAny<string>(), It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new BrainSyncResult { Success = true, FilesCommitted = 2 });

        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _brainUpdateService.Verify(s => s.AppendFallbackLogEntryAsync(
            It.IsAny<string>(),
            It.IsAny<RunId>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

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

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void SetupSuccessfulPush(IReadOnlyList<string> changedFiles, int filesCommitted)
    {
        _brainUpdateService
            .Setup(s => s.DetectChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(changedFiles);
        _brainUpdateService
            .Setup(s => s.Validate(It.IsAny<string>(), It.IsAny<RunId>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(new BrainValidationResult { OperationLogUpdated = true });
        _brainUpdateService
            .Setup(s => s.CommitAndPushAsync(It.IsAny<string>(), It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new BrainSyncResult { Success = true, FilesCommitted = filesCommitted });
    }

    private void SetupFailedPush(IReadOnlyList<string> changedFiles)
    {
        _brainUpdateService
            .Setup(s => s.DetectChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(changedFiles);
        _brainUpdateService
            .Setup(s => s.Validate(It.IsAny<string>(), It.IsAny<RunId>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(new BrainValidationResult { OperationLogUpdated = true });
        _brainUpdateService
            .Setup(s => s.CommitAndPushAsync(It.IsAny<string>(), It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<IRepositoryProvider>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new BrainSyncResult { Success = false, FilesCommitted = 0, ErrorMessage = "push rejected" });
    }
}
