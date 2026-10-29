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
