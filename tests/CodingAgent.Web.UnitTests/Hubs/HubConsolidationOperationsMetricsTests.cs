using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Metric-specific tests for <see cref="HubConsolidationOperations"/>:
/// BrainReviewLines counter and BrainReviewRefusals counter (spec 054, 8A.1, T11, T18, T23).
/// Isolated in [Collection("Metrics")] to prevent counter cross-talk.
/// </summary>
[Collection("Metrics")]
public sealed class HubConsolidationOperationsMetricsTests
{
    private readonly ConsolidationBadgeService _badgeService = new();

    private static ModelFetchService MakeModelFetch() =>
        new(new AgentRegistryService(Mock.Of<ILogger>()),
            Mock.Of<IAgentCommunication>(), Mock.Of<ILogger>());

    private static PipelineRun MakeRun(string runId) => new()
    {
        RunId = runId,
        IssueIdentifier = new IssueIdentifier("consol:brain-metrics"),
        IssueTitle = "Brain Metrics",
        IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
        RepoProviderConfigId = "repo-e2e"
    };

    private static ConsolidationJobResult MakeResult(
        string jobId, bool success, BrainConsolidationOutcome? brainOutcome = null, string? refusalReason = null)
        => new ConsolidationJobResult
        {
            JobId = jobId, Success = success,
            Summary = success ? "ok" : "refused",
            BrainOutcome = brainOutcome,
            BrainReviewRefusalReason = refusalReason,
            ErrorMessage = success ? null : "refused"
        };

    private HubConsolidationOperations MakeSut(ILogger logger, Mock<IOrchestratorRunService> runService, Mock<IRunLifecycleManager> lm)
        => new(MakeModelFetch(), _badgeService, Mock.Of<IChangeNotifier>(), lm.Object, runService.Object, logger);

    private static (ILogger Logger, CaptureSink Sink) MakeLogger()
    {
        var sink = new CaptureSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        return (logger, sink);
    }

    // ── BrainReviewLines counter ──────────────────────────────────────────

    [Fact]
    public async Task HandleConsolidation_BrainOutcomeWithLines_RecordsBrainReviewLinesPerLine()
    {
        var runId = $"brain-lines-{Guid.NewGuid():N}";
        var run = MakeRun(runId);
        var runService = new Mock<IOrchestratorRunService>();
        runService.Setup(s => s.GetRun(new RunId(runId))).Returns(run);
        var lm = new Mock<IRunLifecycleManager>();
        lm.Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>(), null, null))
          .ReturnsAsync((PipelineRun?)null);

        var (logger, _) = MakeLogger();
        var sut = MakeSut(logger, runService, lm);

        var outcome = new BrainConsolidationOutcome
        {
            Promoted = 2, Appended = 1,
            Lines =
            [
                new BrainReviewLine { Action = "promoted", Scope = "repository", File = "f1.md", Text = "x" },
                new BrainReviewLine { Action = "promoted", Scope = "technology", File = "f2.md", Text = "y" },
                new BrainReviewLine { Action = "appended", Scope = "general",    File = "f3.md", Text = "z" },
                new BrainReviewLine { Action = "retired",  Scope = "not_a_scope", File = "f4.md", Text = "w" },
            ]
        };

        var measurements = new ConcurrentBag<(string Action, string Scope)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName && instrument.Name == "pipeline.brain.review.lines")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string action = "", scope = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "action") action = tag.Value?.ToString() ?? "";
                if (tag.Key == "scope") scope = tag.Value?.ToString() ?? "";
            }
            measurements.Add((action, scope));
        });
        listener.Start();

        await sut.HandleConsolidationCompleteAsync(MakeResult(runId, true, brainOutcome: outcome), null);
        listener.Dispose();

        measurements.Should().HaveCount(4, "one BrainReviewLines.Add per line");
        measurements.Should().ContainSingle(m => m.Action == "promoted" && m.Scope == "repository");
        measurements.Should().ContainSingle(m => m.Action == "promoted" && m.Scope == "technology");
        measurements.Should().ContainSingle(m => m.Action == "appended" && m.Scope == "general");
        measurements.Should().ContainSingle(m => m.Action == "retired" && m.Scope == "other",
            "an unrecognized scope must be mapped to 'other'");
    }

    [Fact]
    public async Task HandleConsolidation_BrainOutcome_LogsReviewRecordedInformation()
    {
        // T23: assert the Information log using real Serilog, not Mock<ILogger>.Verify
        var runId = $"brain-log-{Guid.NewGuid():N}";
        var run = MakeRun(runId);
        var runService = new Mock<IOrchestratorRunService>();
        runService.Setup(s => s.GetRun(new RunId(runId))).Returns(run);
        var lm = new Mock<IRunLifecycleManager>();
        lm.Setup(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>(), null, null))
          .ReturnsAsync((PipelineRun?)null);

        var (logger, sink) = MakeLogger();
        var sut = MakeSut(logger, runService, lm);
        var outcome = new BrainConsolidationOutcome { Promoted = 2, Appended = 1, Lines = [] };

        await sut.HandleConsolidationCompleteAsync(MakeResult(runId, true, brainOutcome: outcome), null);

        sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Information &&
            e.MessageTemplate.Text.Contains("Brain review recorded"),
            "Information log 'Brain review recorded' must be emitted (spec 054 8A.1)");
    }

    // ── BrainReviewRefusals counter ───────────────────────────────────────

    [Fact]
    public async Task HandleConsolidation_KnownRefusalReason_RecordsReasonAsIs()
    {
        // T18: known reasons are recorded verbatim
        var runId = $"brain-refusal-known-{Guid.NewGuid():N}";
        var run = MakeRun(runId);
        var runService = new Mock<IOrchestratorRunService>();
        runService.Setup(s => s.GetRun(new RunId(runId))).Returns(run);
        var lm = new Mock<IRunLifecycleManager>();
        lm.Setup(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), null))
          .ReturnsAsync((PipelineRun?)null);

        var (logger, _) = MakeLogger();
        var sut = MakeSut(logger, runService, lm);

        var reasons = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName && instrument.Name == "pipeline.brain.review.refusals")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "reason") reasons.Add(tag.Value?.ToString() ?? "");
        });
        listener.Start();

        await sut.HandleConsolidationCompleteAsync(
            MakeResult(runId, false, refusalReason: BrainReviewRefusalReasons.SummaryInvalid), null);
        listener.Dispose();

        reasons.Should().ContainSingle(r => r == BrainReviewRefusalReasons.SummaryInvalid,
            "known refusal reason must be recorded as-is (T18)");
    }

    [Fact]
    public async Task HandleConsolidation_UnknownRefusalReason_RecordsOtherAndLogsWarning()
    {
        // T18 + T23: unknown reason → "other"; Warning log emitted
        var runId = $"brain-refusal-other-{Guid.NewGuid():N}";
        var run = MakeRun(runId);
        var runService = new Mock<IOrchestratorRunService>();
        runService.Setup(s => s.GetRun(new RunId(runId))).Returns(run);
        var lm = new Mock<IRunLifecycleManager>();
        lm.Setup(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), null))
          .ReturnsAsync((PipelineRun?)null);

        var (logger, sink) = MakeLogger();
        var sut = MakeSut(logger, runService, lm);

        var reasons = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName && instrument.Name == "pipeline.brain.review.refusals")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "reason") reasons.Add(tag.Value?.ToString() ?? "");
        });
        listener.Start();

        await sut.HandleConsolidationCompleteAsync(
            MakeResult(runId, false, refusalReason: "totally-unknown-reason"), null);
        listener.Dispose();

        reasons.Should().ContainSingle(r => r == "other", "unknown reasons map to 'other' (T18)");

        // T23: Warning log via real Serilog
        sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Warning &&
            e.MessageTemplate.Text.Contains("Brain review refused"),
            "Warning log 'Brain review refused' must be emitted (spec 054 8A.1)");
    }
}

/// <summary>
/// Simple in-memory Serilog sink (T23: capture log events in tests without Mock).
/// </summary>
internal sealed class HubMetricsCaptureSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();
    public IReadOnlyCollection<LogEvent> Events => _events;
    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}

// Alias for the CaptureSink used in the tests above
internal sealed class CaptureSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();
    public IReadOnlyCollection<LogEvent> Events => _events;
    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}
