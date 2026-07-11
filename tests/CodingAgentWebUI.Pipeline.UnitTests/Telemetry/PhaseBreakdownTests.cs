using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Tests verifying that AccumulateTokenUsage populates the PhaseBreakdown dictionary correctly.
/// </summary>
[Collection("Metrics")]
public class PhaseBreakdownTests : IDisposable
{
    private readonly MeterListener _listener = new();

    public PhaseBreakdownTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((_, _, _, _) => { });
        _listener.SetMeasurementEventCallback<double>((_, _, _, _) => { });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void AccumulateTokenUsage_SamePhase_SumsTokensAndCost()
    {
        var run = CreateRun();
        var result1 = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
            Cost = 0.10m
        };
        var result2 = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 200, OutputTokens = 100 },
            Cost = 0.20m
        };

        run.AccumulateTokenUsage(result1, phase: "codegen");
        run.AccumulateTokenUsage(result2, phase: "codegen");

        run.Metrics.PhaseBreakdown.Should().ContainKey("codegen");
        var phase = run.Metrics.PhaseBreakdown["codegen"];
        phase.Tokens.Should().Be(450); // (100+50) + (200+100)
        phase.Cost.Should().Be(0.30m);
    }

    [Fact]
    public void AccumulateTokenUsage_DifferentPhases_SeparateEntries()
    {
        var run = CreateRun();
        var result1 = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
            Cost = 0.05m
        };
        var result2 = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 200, OutputTokens = 100 },
            Cost = 0.15m
        };

        run.AccumulateTokenUsage(result1, phase: "analysis");
        run.AccumulateTokenUsage(result2, phase: "codegen");

        run.Metrics.PhaseBreakdown.Should().HaveCount(2);
        run.Metrics.PhaseBreakdown.Should().ContainKey("analysis");
        run.Metrics.PhaseBreakdown.Should().ContainKey("codegen");
        run.Metrics.PhaseBreakdown["analysis"].Tokens.Should().Be(150);
        run.Metrics.PhaseBreakdown["analysis"].Cost.Should().Be(0.05m);
        run.Metrics.PhaseBreakdown["codegen"].Tokens.Should().Be(300);
        run.Metrics.PhaseBreakdown["codegen"].Cost.Should().Be(0.15m);
    }

    [Fact]
    public void AccumulateTokenUsage_WithoutPhase_DoesNotPopulateBreakdown()
    {
        var run = CreateRun();
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
            Cost = 0.05m
        };

        run.AccumulateTokenUsage(result);

        run.Metrics.PhaseBreakdown.Should().BeEmpty();
    }

    [Fact]
    public void AccumulateTokenUsage_NullCost_AccumulatesTokensOnly()
    {
        var run = CreateRun();
        var result1 = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
            Cost = 0.10m
        };
        var result2 = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 200, OutputTokens = 100 },
            Cost = null
        };

        run.AccumulateTokenUsage(result1, phase: "analysis");
        run.AccumulateTokenUsage(result2, phase: "analysis");

        var phase = run.Metrics.PhaseBreakdown["analysis"];
        phase.Tokens.Should().Be(450);
        phase.Cost.Should().Be(0.10m); // Preserves existing cost when new cost is null
    }

    [Fact]
    public void AccumulateTokenUsage_BothCallsNullCost_ProducesZeroCost()
    {
        // TODO: Known design choice — null+null coerces to 0m rather than preserving null ("no data").
        // Display remains correct since FormatCost treats both null and 0m as "—".
        var run = CreateRun();
        var result1 = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 50, OutputTokens = 25 },
            Cost = null
        };
        var result2 = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
            Cost = null
        };

        run.AccumulateTokenUsage(result1, phase: "codegen");
        run.AccumulateTokenUsage(result2, phase: "codegen");

        var phase = run.Metrics.PhaseBreakdown["codegen"];
        phase.Tokens.Should().Be(225);
        // After AddOrUpdate: (null ?? 0m) + (null ?? 0m) = 0m
        phase.Cost.Should().Be(0m);
    }

    private static PipelineRun CreateRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.Implementation,
        ProjectId = "phase-breakdown-test",
        ProjectName = "TestProject"
    };
}
