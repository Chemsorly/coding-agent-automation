using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="BrainSyncService.SyncPostRunAsync"/>: exercises the real
/// service body against mocked <see cref="IBrainUpdateService"/> and verifies that
/// the correct telemetry counters are emitted on each path.
///
/// Uses <see cref="TestMeterFactory"/> + <see cref="MetricCollector{T}"/> for isolated
/// metric observation — each test class gets its own <see cref="System.Diagnostics.Metrics.Meter"/>
/// and does NOT need <c>[Collection("Metrics")]</c>.
/// </summary>
public class BrainSyncServiceTests : IDisposable
{
    private readonly Mock<IBrainUpdateService> _brainUpdateService;
    private readonly Mock<IRepositoryProvider> _brainProvider;
    private readonly Mock<Serilog.ILogger> _logger;
    private readonly BrainSyncService _sut;
    private readonly TestMeterFactory _meterFactory;

    // Collectors — one per instrument under test
    private readonly MetricCollector<long> _brainUpdatesEmpty;
    private readonly MetricCollector<long> _brainUpdatesCommitted;
    private readonly MetricCollector<long> _brainFilesWritten;

    public BrainSyncServiceTests()
    {
        _brainUpdateService = new Mock<IBrainUpdateService>();
        _brainProvider = new Mock<IRepositoryProvider>();
        _logger = new Mock<Serilog.ILogger>();

        _meterFactory = new TestMeterFactory();
        _sut = new BrainSyncService(_brainUpdateService.Object, _logger.Object, _meterFactory);

        // Bind collectors to the instruments created by the factory inside BrainSyncService
        _brainUpdatesEmpty     = new MetricCollector<long>(_meterFactory, "CodingAgent.Pipeline", "brain.updates.empty");
        _brainUpdatesCommitted = new MetricCollector<long>(_meterFactory, "CodingAgent.Pipeline", "brain.updates.committed");
        _brainFilesWritten     = new MetricCollector<long>(_meterFactory, "CodingAgent.Pipeline", "brain.files.written");
    }

    public void Dispose() => _meterFactory.Dispose();

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
    public async Task SyncPostRunAsync_WhenNoChanges_IncrementsBrainUpdatesEmptyCounter()
    {
        var run = CreateRun();
        _brainUpdateService
            .Setup(s => s.DetectChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)Array.Empty<string>());

        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _brainUpdatesEmpty.GetMeasurementSnapshot()
            .Should().ContainSingle(m => m.Value == 1);
    }

    [Fact]
    public async Task SyncPostRunAsync_WhenNoChanges_DoesNotIncrementCommittedOrFilesWrittenCounters()
    {
        var run = CreateRun();
        _brainUpdateService
            .Setup(s => s.DetectChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)Array.Empty<string>());

        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _brainUpdatesCommitted.GetMeasurementSnapshot().Should().BeEmpty();
        _brainFilesWritten.GetMeasurementSnapshot().Should().BeEmpty();
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

    [Fact]
    public async Task SyncPostRunAsync_WhenChangesDetected_IncrementsBrainUpdatesCommittedCounter()
    {
        var run = CreateRun();
        SetupSuccessfulPush(changedFiles: ["lessons.md"], filesCommitted: 1);

        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _brainUpdatesCommitted.GetMeasurementSnapshot()
            .Should().ContainSingle(m => m.Value == 1);
    }

    [Fact]
    public async Task SyncPostRunAsync_WhenChangesDetected_IncrementsBrainFilesWrittenByFileCount()
    {
        var run = CreateRun();
        SetupSuccessfulPush(changedFiles: ["a.md", "b.md"], filesCommitted: 2);

        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _brainFilesWritten.GetMeasurementSnapshot()
            .Should().ContainSingle(m => m.Value == 2);
    }

    [Fact]
    public async Task SyncPostRunAsync_WhenChangesDetected_DoesNotIncrementBrainUpdatesEmptyCounter()
    {
        var run = CreateRun();
        SetupSuccessfulPush(changedFiles: ["lessons.md"], filesCommitted: 1);

        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _brainUpdatesEmpty.GetMeasurementSnapshot().Should().BeEmpty();
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

    [Fact]
    public async Task SyncPostRunAsync_WhenPushFails_DoesNotIncrementBrainUpdatesCommittedCounter()
    {
        var run = CreateRun();
        SetupFailedPush(changedFiles: ["lessons.md"]);

        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _brainUpdatesCommitted.GetMeasurementSnapshot().Should().BeEmpty();
        _brainUpdatesEmpty.GetMeasurementSnapshot().Should().BeEmpty();
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
