using AwesomeAssertions;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Tests for <see cref="PipelineTelemetry.NormalizeRunPhase"/> — verifies that raw phase names
/// from <see cref="CodingAgent.Pipeline.Models.RunMetrics.PhaseBreakdown"/> are mapped to
/// the closed set required by the API-side pipeline.run.* counters.
/// </summary>
public class RunPhaseNormalizationTests
{
    [Theory]
    [InlineData("analysis", "analysis")]
    [InlineData("Analysis", "analysis")]
    [InlineData("ANALYSIS", "analysis")]
    [InlineData("analysis_review", "analysis_review")]
    [InlineData("analysisreview", "analysis_review")]
    [InlineData("codegen", "codegen")]
    [InlineData("code_gen", "codegen")]
    [InlineData("code generation", "codegen")]
    [InlineData("Code Generation", "codegen")]
    [InlineData("review", "review")]
    [InlineData("code_review", "review")]
    [InlineData("fix", "review")]
    [InlineData("quality_gate", "quality_gate")]
    [InlineData("qgc_retry_agent", "quality_gate")]
    [InlineData("acceptance_criteria", "acceptance_criteria")]
    [InlineData("acceptancecriteria", "acceptance_criteria")]
    [InlineData("pr_description", "pr_description")]
    [InlineData("prdescription", "pr_description")]
    [InlineData("reflection", "reflection")]
    [InlineData("decomposition", "decomposition")]
    [InlineData("decomposition_review", "decomposition")]
    [InlineData("decompositionreview", "decomposition")]
    [InlineData("decomposition_analysis", "decomposition")]
    [InlineData("decomposition_refinement", "decomposition")]
    public void NormalizeRunPhase_KnownPhase_ReturnsExpectedValue(string raw, string expected)
    {
        PipelineTelemetry.NormalizeRunPhase(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("review_correctness")]
    [InlineData("review_security")]
    [InlineData("review_performance")]
    [InlineData("Review_Correctness")]
    [InlineData("review correctness")]
    [InlineData("review_summary")]
    [InlineData("follow_up_Security Reviewer")]
    public void NormalizeRunPhase_ReviewSubPhase_CollapsesToReview(string raw)
    {
        PipelineTelemetry.NormalizeRunPhase(raw).Should().Be("review",
            $"per-reviewer names like '{raw}' must collapse into 'review'");
    }

    [Theory]
    [InlineData("unknown_phase")]
    [InlineData("unknown")]
    [InlineData("warmup")]
    [InlineData("postpr")]
    [InlineData("random_phase")]
    [InlineData("")]
    public void NormalizeRunPhase_UnknownOrEmpty_ReturnsOther(string raw)
    {
        PipelineTelemetry.NormalizeRunPhase(raw).Should().Be("other");
    }

    [Fact]
    public void NormalizeRunPhase_Null_ReturnsOther()
    {
        PipelineTelemetry.NormalizeRunPhase(null).Should().Be("other");
    }

    [Fact]
    public void RunPhases_All_ContainsExactClosedSet()
    {
        var expected = new[]
        {
            "analysis", "analysis_review", "codegen", "review", "quality_gate",
            "acceptance_criteria", "pr_description", "reflection", "decomposition", "other"
        };

        PipelineTelemetry.RunPhases.All.Should().BeEquivalentTo(expected,
            "closed set must exactly match the issue spec");
    }

    [Fact]
    public void RunPhases_AllValues_AreSnakeCase()
    {
        foreach (var phase in PipelineTelemetry.RunPhases.All)
        {
            phase.Should().MatchRegex("^[a-z][a-z0-9_]*$",
                $"phase '{phase}' must be snake_case (lowercase, underscores only)");
        }
    }

    [Fact]
    public void RunProviders_All_ContainsExpectedValues()
    {
        PipelineTelemetry.RunProviders.All.Should().BeEquivalentTo(
            new[] { "kiro", "opencode", "claude", "unknown" });
    }

    [Fact]
    public void RunProviders_Constants_HaveExpectedValues()
    {
        PipelineTelemetry.RunProviders.Kiro.Should().Be("kiro");
        PipelineTelemetry.RunProviders.OpenCode.Should().Be("opencode");
        PipelineTelemetry.RunProviders.Claude.Should().Be("claude");
        PipelineTelemetry.RunProviders.Unknown.Should().Be("unknown");
    }

    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("KIRO", "kiro")]
    [InlineData("opencode", "opencode")]
    [InlineData("rogue", "unknown")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void NormalizeRunProvider_MapsToClosedSet(string? raw, string expected)
    {
        PipelineTelemetry.NormalizeRunProvider(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("api", "api")]
    [InlineData("Subscription", "subscription")]
    [InlineData("free", "unknown")]
    [InlineData(null, "unknown")]
    public void NormalizeBillingMode_MapsToClosedSet(string? raw, string expected)
    {
        PipelineTelemetry.NormalizeBillingMode(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("five_hour", "five_hour")]
    [InlineData("SEVEN_DAY", "seven_day")]
    [InlineData("next_month", "other")]
    [InlineData(null, "other")]
    public void RateLimitTags_NormalizeWindow_MapsToClosedSet(string? raw, string expected)
    {
        PipelineTelemetry.RateLimitTags.NormalizeWindow(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("allowed_warning", "allowed_warning")]
    [InlineData("throttled", "other")]
    public void RateLimitTags_NormalizeStatus_MapsToClosedSet(string? raw, string expected)
    {
        PipelineTelemetry.RateLimitTags.NormalizeStatus(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("Quality gate retry agent (attempt 1)", "quality_gate")]
    [InlineData("Pre-PR cleanup agent", "quality_gate")]
    [InlineData("Final QG retry agent", "quality_gate")]
    [InlineData("Post-PR CI retry agent", "quality_gate")]
    [InlineData("Code generation agent", "codegen")]
    [InlineData("Code gen (attempt 2)", "codegen")]
    [InlineData("Analysis agent session", "analysis")]
    [InlineData("Analysis phase", "analysis")]
    [InlineData("Code review agent", "review")]
    [InlineData("Follow-up for reviewer #1", "review")]
    [InlineData("Review summary agent", "review")]
    [InlineData("Acceptance criteria validation", "acceptance_criteria")]
    [InlineData("Decomposition agent", "decomposition")]
    [InlineData("Some unrecognized description", "other")]
    public void NormalizePhaseDescription_MapsDescriptionToRunPhase(string description, string expectedPhase)
    {
        PipelineTelemetry.NormalizePhaseDescription(description).Should().Be(expectedPhase);
        PipelineTelemetry.RunPhases.All.Should().Contain(expectedPhase);
    }

    [Fact]
    public void NormalizePhaseDescription_ThrowsOnEmptyString()
    {
        var act = () => PipelineTelemetry.NormalizePhaseDescription(string.Empty);
        act.Should().Throw<ArgumentException>("empty string is not a valid phase description");
    }

    [Theory]
    [InlineData("stall_kill", "stall_kill")]
    [InlineData("PROCESS_DEATH", "process_death")]
    [InlineData("process_timeout", "process_timeout")]
    [InlineData("rogue", null)]
    [InlineData(null, null)]
    public void AgentStallKinds_Normalize_MapsToClosedSetOrNull(string? raw, string? expected)
    {
        PipelineTelemetry.AgentStallKinds.Normalize(raw).Should().Be(expected);
    }
}
