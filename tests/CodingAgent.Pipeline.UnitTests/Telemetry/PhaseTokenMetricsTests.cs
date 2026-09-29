using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Tests verifying that AccumulateTokenUsage correctly accumulates run-level and phase-level
/// token/cost data into <see cref="PipelineRun.Metrics"/> and <see cref="RunMetrics.PhaseBreakdown"/>.
/// Note: Per-issue #2978, agent.tokens.used and agent.cost.usd are removed. Token/cost counters
/// are now recorded API-side at terminal time via pipeline.run.tokens and pipeline.run.cost_usd.
/// </summary>
public class PhaseTokenMetricsTests
{
    // ── Phase-tagged token accumulation ──

    [Fact]
    public void AccumulateTokenUsage_WithPhase_PopulatesPhaseBreakdown()
    {
        var run = CreateRun("phase-tag-test");
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 200, OutputTokens = 100 }
        };

        run.AccumulateTokenUsage(result, phase: "codegen");

        run.Metrics.PhaseBreakdown.Should().ContainKey("codegen");
        run.Metrics.PhaseBreakdown["codegen"].Tokens.Should().Be(300);
        run.TotalTokens.Should().Be(300);
    }

    [Fact]
    public void AccumulateTokenUsage_WithPhaseAndCost_PopulatesCostInPhaseBreakdown()
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

        run.Metrics.PhaseBreakdown.Should().ContainKey("analysis");
        run.Metrics.PhaseBreakdown["analysis"].Cost.Should().Be(0.03m);
        run.TotalCost.Should().Be(0.03m);
    }

    [Fact]
    public void AccumulateTokenUsage_WithoutPhase_DoesNotPopulatePhaseBreakdown()
    {
        var run = CreateRun("no-phase-test");
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 50, OutputTokens = 25 }
        };

        run.AccumulateTokenUsage(result);

        run.Metrics.PhaseBreakdown.Should().BeEmpty("no phase key was specified");
        run.TotalTokens.Should().Be(75);
    }

    [Fact]
    public void AccumulateTokenUsage_NullResult_DoesNotUpdateRun()
    {
        var run = CreateRun("null-result-test");
        run.AccumulateTokenUsage((AgentResult?)null, phase: "analysis");

        run.TotalTokens.Should().Be(0);
        run.Metrics.PhaseBreakdown.Should().BeEmpty();
    }

    [Fact]
    public void AccumulateTokenUsage_NullUsage_DoesNotUpdateRun()
    {
        var run = CreateRun("null-usage-test");
        run.AccumulateTokenUsage((TokenUsage?)null, phase: "analysis");

        run.TotalTokens.Should().Be(0);
        run.Metrics.PhaseBreakdown.Should().BeEmpty();
    }

    [Fact]
    public void AccumulateTokenUsage_MultiplePhases_AccumulatesEachSeparately()
    {
        var run = CreateRun("multi-phase-test");

        run.AccumulateTokenUsage(new AgentResult { ExitCode = 0, OutputLines = [], Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 } }, phase: "analysis");
        run.AccumulateTokenUsage(new AgentResult { ExitCode = 0, OutputLines = [], Usage = new TokenUsage { InputTokens = 200, OutputTokens = 100 } }, phase: "codegen");
        run.AccumulateTokenUsage(new AgentResult { ExitCode = 0, OutputLines = [], Usage = new TokenUsage { InputTokens = 50, OutputTokens = 25 } }, phase: "analysis");

        run.TotalTokens.Should().Be(525);
        run.Metrics.PhaseBreakdown["analysis"].Tokens.Should().Be(225, "two analysis calls sum to 150+75=225");
        run.Metrics.PhaseBreakdown["codegen"].Tokens.Should().Be(300);
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
