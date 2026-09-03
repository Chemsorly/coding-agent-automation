using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Unit tests verifying that <c>brain.sync.skipped</c> is emitted with the correct reason tag
/// whenever the post-run brain sync gate in <see cref="PullRequestFinalizationService.RunPostPrSequenceAsync"/>
/// is skipped. These tests address the CRITICAL finding that post-run brain metrics were always absent
/// in Prometheus: <c>brain.sync.skipped</c> now fires on every finalized run where the sync is skipped,
/// making the skip reason diagnosable without Loki queries.
/// </summary>
[Collection("Metrics")]
public class BrainSyncSkippedMetricTests : IDisposable
{
    private readonly PullRequestFinalizationService _sut;
    private readonly Mock<Serilog.ILogger> _logger = new();
    private readonly MeterListener _listener = new();
    private ConcurrentBag<(string Name, KeyValuePair<string, object?>[] Tags)> _measurements = [];

    public BrainSyncSkippedMetricTests()
    {
        _sut = new PullRequestFinalizationService(_logger.Object);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            _measurements.Add((instrument.Name, tags.ToArray()));
        });
        _listener.Start();

        // Warm-up: reference the instrument before any test emits, in case the static counter
        // was published before Start() and InstrumentPublished won't fire retroactively.
        PipelineTelemetry.BrainSyncSkipped.Add(0);
        _measurements = [];
    }

    public void Dispose() => _listener.Dispose();

    private static PipelineRun CreateRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "owner/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"skip-metric-{Guid.NewGuid():N}"),
        PullRequestNumber = "99"
    };

    private PostPrSequenceRequest BuildRequest(PipelineRun run, bool isDraft,
        IRepositoryProvider? brainProvider, IBrainSyncService? brainSync,
        bool brainReadOnly = false)
    {
        var agentProvider = new Mock<IAgentProvider>();
        agentProvider
            .Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":3,"category":"test","comment":"ok"}}"""] });

        var historyService = new Mock<IPipelineRunHistoryService>();
        historyService
            .Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);

        return new PostPrSequenceRequest
        {
            Run = run,
            IsDraft = isDraft,
            AgentProvider = agentProvider.Object,
            RepoProvider = new Mock<IRepositoryProvider>().Object,
            Config = new PipelineConfiguration
            {
                AgentTimeout = TimeSpan.FromMinutes(1),
                BrainReadOnly = brainReadOnly
            },
            BrainProvider = brainProvider,
            BrainSync = brainSync,
            FeedbackService = new FeedbackService(_logger.Object),
            HistoryService = historyService.Object,
            EmitOutputLine = _ => { },
            TransitionCallback = _ => Task.CompletedTask
        };
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenIsDraftTrue_EmitsBrainSyncSkippedWithIsDraftReason()
    {
        var run = CreateRun();
        var brainProvider = new Mock<IRepositoryProvider>().Object;
        var brainSync = new Mock<IBrainSyncService>().Object;

        _measurements = [];
        await _sut.RunPostPrSequenceAsync(
            BuildRequest(run, isDraft: true, brainProvider, brainSync),
            CancellationToken.None);

        _measurements.Should().Contain(m =>
            m.Name == "brain.sync.skipped" &&
            m.Tags.Contains(new KeyValuePair<string, object?>("reason", "is_draft")));
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenBrainProviderNull_EmitsBrainSyncSkippedWithNoProviderReason()
    {
        var run = CreateRun();
        var brainSync = new Mock<IBrainSyncService>().Object;

        _measurements = [];
        await _sut.RunPostPrSequenceAsync(
            BuildRequest(run, isDraft: false, brainProvider: null, brainSync),
            CancellationToken.None);

        _measurements.Should().Contain(m =>
            m.Name == "brain.sync.skipped" &&
            m.Tags.Contains(new KeyValuePair<string, object?>("reason", "no_provider")));
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenBrainSyncNull_EmitsBrainSyncSkippedWithNoSyncServiceReason()
    {
        var run = CreateRun();
        var brainProvider = new Mock<IRepositoryProvider>().Object;

        _measurements = [];
        await _sut.RunPostPrSequenceAsync(
            BuildRequest(run, isDraft: false, brainProvider, brainSync: null),
            CancellationToken.None);

        _measurements.Should().Contain(m =>
            m.Name == "brain.sync.skipped" &&
            m.Tags.Contains(new KeyValuePair<string, object?>("reason", "no_sync_service")));
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenBrainReadOnlyTrue_EmitsBrainSyncSkippedWithReadOnlyReason()
    {
        var run = CreateRun();
        var brainProvider = new Mock<IRepositoryProvider>().Object;
        var brainSync = new Mock<IBrainSyncService>().Object;

        _measurements = [];
        await _sut.RunPostPrSequenceAsync(
            BuildRequest(run, isDraft: false, brainProvider, brainSync, brainReadOnly: true),
            CancellationToken.None);

        _measurements.Should().Contain(m =>
            m.Name == "brain.sync.skipped" &&
            m.Tags.Contains(new KeyValuePair<string, object?>("reason", "read_only")));
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenBrainSyncExecutes_DoesNotEmitBrainSyncSkipped()
    {
        // Verifies brain.sync.skipped is NOT emitted when all gate conditions are met
        // and SyncPostRunAsync actually runs.
        var run = CreateRun();
        var brainProvider = new Mock<IRepositoryProvider>().Object;
        var brainSync = new Mock<IBrainSyncService>();
        brainSync
            .Setup(b => b.SyncPostRunAsync(
                It.IsAny<PipelineRun>(), It.IsAny<IRepositoryProvider>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        _measurements = [];
        await _sut.RunPostPrSequenceAsync(
            BuildRequest(run, isDraft: false, brainProvider, brainSync.Object),
            CancellationToken.None);

        _measurements.Should().NotContain(m => m.Name == "brain.sync.skipped");
    }
}
