using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Unit tests verifying that <c>brain.sync.skipped</c> is emitted with the correct reason tag
/// whenever the post-run brain sync gate in <see cref="PullRequestFinalizationService.RunPostPrSequenceAsync"/>
/// is skipped.
/// </summary>
public class BrainSyncSkippedMetricTests : IDisposable
{
    private readonly PullRequestFinalizationService _sut;
    private readonly Mock<Serilog.ILogger> _logger = new();
    private readonly TestMeterFactory _meterFactory = new();

    public BrainSyncSkippedMetricTests()
    {
        _sut = new PullRequestFinalizationService(_logger.Object, _meterFactory);
    }

    public void Dispose() => _meterFactory.Dispose();

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
        using var collector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "brain.sync.skipped");

        await _sut.RunPostPrSequenceAsync(
            BuildRequest(run, isDraft: true, brainProvider, brainSync),
            CancellationToken.None);

        collector.GetMeasurementSnapshot().Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("reason", "is_draft")));
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenBrainProviderNull_EmitsBrainSyncSkippedWithNoProviderReason()
    {
        var run = CreateRun();
        var brainSync = new Mock<IBrainSyncService>().Object;
        using var collector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "brain.sync.skipped");

        await _sut.RunPostPrSequenceAsync(
            BuildRequest(run, isDraft: false, brainProvider: null, brainSync),
            CancellationToken.None);

        collector.GetMeasurementSnapshot().Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("reason", "no_provider")));
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenBrainSyncNull_EmitsBrainSyncSkippedWithNoSyncServiceReason()
    {
        var run = CreateRun();
        var brainProvider = new Mock<IRepositoryProvider>().Object;
        using var collector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "brain.sync.skipped");

        await _sut.RunPostPrSequenceAsync(
            BuildRequest(run, isDraft: false, brainProvider, brainSync: null),
            CancellationToken.None);

        collector.GetMeasurementSnapshot().Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("reason", "no_sync_service")));
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenBrainReadOnlyTrue_EmitsBrainSyncSkippedWithReadOnlyReason()
    {
        var run = CreateRun();
        var brainProvider = new Mock<IRepositoryProvider>().Object;
        var brainSync = new Mock<IBrainSyncService>().Object;
        using var collector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "brain.sync.skipped");

        await _sut.RunPostPrSequenceAsync(
            BuildRequest(run, isDraft: false, brainProvider, brainSync, brainReadOnly: true),
            CancellationToken.None);

        collector.GetMeasurementSnapshot().Should().Contain(m =>
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
        using var collector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "brain.sync.skipped");

        await _sut.RunPostPrSequenceAsync(
            BuildRequest(run, isDraft: false, brainProvider, brainSync.Object),
            CancellationToken.None);

        collector.GetMeasurementSnapshot().Should().BeEmpty();
    }
}
