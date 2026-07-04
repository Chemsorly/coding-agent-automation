using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Tests verifying that AccumulateTokenUsage emits a phase tag when provided,
/// and that the analysis gate outcome counter emits correctly.
/// </summary>
[Collection("Metrics")]
public class PhaseTokenMetricsTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> _counters = [];
    private readonly ConcurrentBag<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> _doubles = [];

    public PhaseTokenMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            _counters.Add((instrument.Name, measurement, tags.ToArray()));
        });

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            _doubles.Add((instrument.Name, measurement, tags.ToArray()));
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    // ── Phase-tagged token metrics ──

    [Fact]
    public void AccumulateTokenUsage_WithPhase_EmitsPhaseTag()
    {
        var run = CreateRun("phase-tag-test");
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 200, OutputTokens = 100 }
        };

        run.AccumulateTokenUsage(result, phase: "codegen");

        var counter = _counters.Should().Contain(c => c.Name == "agent.tokens.used"
            && c.Tags.Contains(new KeyValuePair<string, object?>("pipeline.project_id", "phase-tag-test"))
            && c.Tags.Contains(new KeyValuePair<string, object?>("phase", "codegen")))
            .Which;
        counter.Value.Should().Be(300);
    }

    [Fact]
    public void AccumulateTokenUsage_WithPhase_CostAlsoGetsPhaseTag()
    {
        var run = CreateRun("phase-cost-test");
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
            Cost = 0.03m
        };

        run.AccumulateTokenUsage(result, phase: "analysis");

        _doubles.Should().Contain(h => h.Name == "agent.cost.usd"
            && h.Tags.Contains(new KeyValuePair<string, object?>("pipeline.project_id", "phase-cost-test"))
            && h.Tags.Contains(new KeyValuePair<string, object?>("phase", "analysis")));
    }

    [Fact]
    public void AccumulateTokenUsage_WithoutPhase_DoesNotEmitPhaseTag()
    {
        var run = CreateRun("no-phase-test");
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 50, OutputTokens = 25 }
        };

        run.AccumulateTokenUsage(result);

        var counter = _counters.Should().Contain(c => c.Name == "agent.tokens.used"
            && c.Tags.Contains(new KeyValuePair<string, object?>("pipeline.project_id", "no-phase-test")))
            .Which;
        counter.Tags.Should().NotContain(t => t.Key == "phase");
    }

    // ── Analysis gate outcome counter ──

    [Theory]
    [InlineData(AnalysisGateResult.Ready, "ready")]
    [InlineData(AnalysisGateResult.NotReady, "not_ready")]
    [InlineData(AnalysisGateResult.WontDo, "wont_do")]
    public void RecordAnalysisGateOutcome_EmitsCounterWithOutcomeTag(
        AnalysisGateResult outcome, string expectedTagValue)
    {
        var run = CreateRun($"gate-{expectedTagValue}");

        PipelineTelemetry.RecordAnalysisGateOutcome(outcome, run);

        _counters.Should().Contain(c => c.Name == "pipeline.analysis.gate_outcome"
            && c.Tags.Contains(new KeyValuePair<string, object?>("outcome", expectedTagValue))
            && c.Tags.Contains(new KeyValuePair<string, object?>("pipeline.project_id", $"gate-{expectedTagValue}")));
    }

    private static PipelineRun CreateRun(string projectId) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.Implementation,
        ProjectId = projectId,
        ProjectName = "TestProject"
    };
}
