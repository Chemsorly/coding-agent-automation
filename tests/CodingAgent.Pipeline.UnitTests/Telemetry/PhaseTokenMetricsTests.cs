using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Tests verifying that AccumulateTokenUsage correctly updates PhaseBreakdown and run totals,
/// and that the analysis gate outcome counter emits correctly.
/// Note: agent.tokens.used and agent.cost.usd counters have been removed (issue #2978).
/// Token/cost metrics are now emitted at terminal time by the API (pipeline.run.tokens, etc.).
/// </summary>
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
    public void AccumulateTokenUsage_WithPhase_UpdatesPhaseBreakdown()
    {
        // agent.tokens.used counter removed — AccumulateTokenUsage now updates PhaseBreakdown
        // without emitting a counter. The API emits pipeline.run.tokens at terminal time.
        var run = CreateRun("phase-tag-test");
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 200, OutputTokens = 100 }
        };

        run.AccumulateTokenUsage(result, phase: "codegen");

        run.TotalTokens.Should().Be(300);
        run.Metrics.PhaseBreakdown.Should().ContainKey("codegen");
        run.Metrics.PhaseBreakdown["codegen"].Tokens.Should().Be(300);
    }

    [Fact]
    public void AccumulateTokenUsage_WithPhase_CostUpdatesPhaseBreakdown()
    {
        // agent.cost.usd counter removed — cost now updates PhaseBreakdown and run.TotalCost.
        var run = CreateRun("phase-cost-test");
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
            Cost = 0.03m
        };

        run.AccumulateTokenUsage(result, phase: "analysis");

        run.TotalCost.Should().Be(0.03m);
        run.Metrics.PhaseBreakdown.Should().ContainKey("analysis");
        run.Metrics.PhaseBreakdown["analysis"].Cost.Should().Be(0.03m);
    }

    [Fact]
    public void AccumulateTokenUsage_WithoutPhase_DoesNotUpdatePhaseBreakdown()
    {
        var run = CreateRun("no-phase-test");
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 50, OutputTokens = 25 }
        };

        run.AccumulateTokenUsage(result);

        run.TotalTokens.Should().Be(75);
        run.Metrics.PhaseBreakdown.Should().BeEmpty();
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

        PipelineTelemetry.RecordAnalysisGateOutcome(outcome, run.RunType, run.ProjectId, run.ProjectName);

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
