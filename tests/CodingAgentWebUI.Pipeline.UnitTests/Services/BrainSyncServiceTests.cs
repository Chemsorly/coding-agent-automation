using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="BrainSyncService.SyncPostRunAsync"/>: exercises the real
/// service body against mocked <see cref="IBrainUpdateService"/> and verifies that
/// the correct telemetry counters are emitted on each path.
/// </summary>
/// <remarks>
/// Placed in the "Metrics" collection to prevent concurrent <see cref="MeterListener"/>
/// contention with other tests that listen on the same static <see cref="PipelineTelemetry.Meter"/>.
/// </remarks>
[Collection("Metrics")]
public class BrainSyncServiceTests : IDisposable
{
    private readonly Mock<IBrainUpdateService> _brainUpdateService;
    private readonly Mock<IRepositoryProvider> _brainProvider;
    private readonly Mock<Serilog.ILogger> _logger;
    private readonly BrainSyncService _sut;

    private readonly MeterListener _meterListener;
    private ConcurrentBag<(string Name, long Value)> _measurements;

    public BrainSyncServiceTests()
    {
        _brainUpdateService = new Mock<IBrainUpdateService>();
        _brainProvider = new Mock<IRepositoryProvider>();
        _logger = new Mock<Serilog.ILogger>();
        _sut = new BrainSyncService(_brainUpdateService.Object, _logger.Object);

        _measurements = [];
        _meterListener = new MeterListener();

        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            // TODO [WARNING]: The callback accesses _measurements through `this` (field access), so
            // reassigning `_measurements = []` in tests correctly affects what the callback writes to.
            // This is correct but fragile: if the callback were captured as a local closure rather than
            // accessing the field through the implicit `this`, reassignment would break measurement
            // collection. Preserve the field-access pattern when modifying this callback.
            _measurements.Add((instrument.Name, measurement));
        });
        _meterListener.Start();

        // Warm-up: force the listener to observe all brain metric instruments by emitting
        // a zero measurement on each. Instruments created before Start() may not fire
        // InstrumentPublished retroactively, so we trigger them explicitly.
        // TODO [WARNING]: MeterListener.Start() is called before the warm-up Add(0) calls. Static
        // instruments on PipelineTelemetry.Meter are likely already published before this class's
        // constructor runs; the warm-up pattern handles this. However, if PipelineTelemetry has
        // not been referenced yet in this process when the listener starts, InstrumentPublished may
        // not fire retroactively and the Add(0) calls emit with no listener subscribed. If metrics
        // appear missing, call _meterListener.RecordObservableInstruments() after Start() to force
        // a scan, or ensure PipelineTelemetry is referenced before Start().
        PipelineTelemetry.BrainUpdatesEmpty.Add(0);
        PipelineTelemetry.BrainUpdatesCommitted.Add(0);
        PipelineTelemetry.BrainFilesWritten.Add(0);

        // Reset after warm-up so tests start with a clean slate.
        // TODO [WARNING]: The warm-up + reset pattern here is fragile. If `_measurements = []` is moved or
        // removed, tests that do not reset inline will pick up the three zero-value warm-up measurements.
        // If the service ever emits a counter value of 0 after a refactor, it would be indistinguishable
        // from the warm-up zero in test output. All metric-asserting tests should reset `_measurements`
        // inline immediately before the call under test to avoid this dependency on constructor ordering.
        _measurements = [];
    }

    public void Dispose() => _meterListener.Dispose();

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

        _measurements = []; // ensure clean slate immediately before the real increment
        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _measurements.Should().Contain(m => m.Name == "brain.updates.empty" && m.Value == 1);
    }

    [Fact]
    public async Task SyncPostRunAsync_WhenNoChanges_DoesNotIncrementCommittedOrFilesWrittenCounters()
    {
        var run = CreateRun();
        _brainUpdateService
            .Setup(s => s.DetectChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)Array.Empty<string>());

        _measurements = [];
        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _measurements.Should().NotContain(m => m.Name == "brain.updates.committed");
        _measurements.Should().NotContain(m => m.Name == "brain.files.written");
    }

    [Fact]
    public async Task SyncPostRunAsync_WhenNoChanges_EmitsNoBrainChangesOutputLine()
    {
        var run = CreateRun();
        _brainUpdateService
            .Setup(s => s.DetectChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)Array.Empty<string>());

        // TODO [WARNING]: Missing _measurements = [] reset before invocation. This test does not
        // assert on metrics, but if a future edit adds a metric assertion here it will pick up
        // warm-up or prior-test measurements rather than only those from this call.
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

        _measurements = [];
        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _measurements.Should().Contain(m => m.Name == "brain.updates.committed" && m.Value == 1);
    }

    [Fact]
    public async Task SyncPostRunAsync_WhenChangesDetected_IncrementsBrainFilesWrittenByFileCount()
    {
        var run = CreateRun();
        SetupSuccessfulPush(changedFiles: ["a.md", "b.md"], filesCommitted: 2);

        _measurements = [];
        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _measurements.Should().Contain(m => m.Name == "brain.files.written" && m.Value == 2);
    }

    [Fact]
    public async Task SyncPostRunAsync_WhenChangesDetected_DoesNotIncrementBrainUpdatesEmptyCounter()
    {
        var run = CreateRun();
        SetupSuccessfulPush(changedFiles: ["lessons.md"], filesCommitted: 1);

        _measurements = [];
        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _measurements.Should().NotContain(m => m.Name == "brain.updates.empty");
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

        _measurements = [];
        await _sut.SyncPostRunAsync(run, _brainProvider.Object, CancellationToken.None);

        _measurements.Should().NotContain(m => m.Name == "brain.updates.committed");
        // On failed push changedFiles.Count > 0, so the service takes the commit branch and never
        // touches BrainUpdatesEmpty. Asserting absence guards against a regression that incorrectly
        // emits brain.updates.empty when a push fails (previous TODO [WARNING] from correctness review).
        _measurements.Should().NotContain(m => m.Name == "brain.updates.empty");
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
            .Returns(new BrainValidationResult { OperationLogUpdated = false }); // log.md NOT updated
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
        // TODO [WARNING]: Test only verifies AppendFallbackLogEntryAsync was called; does not assert
        // run.BrainUpdatesPushed or run.BrainFilesCommitted after the fallback path. A regression
        // where the fallback path commits but fails to set BrainUpdatesPushed = true would not be caught.
        // Also does not assert metric counters (brain.updates.committed should fire once on success).
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
            .Returns(new BrainValidationResult { OperationLogUpdated = true }); // log.md IS updated
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
