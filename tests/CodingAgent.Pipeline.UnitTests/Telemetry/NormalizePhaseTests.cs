using AwesomeAssertions;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Tests for <see cref="PipelineTelemetry.NormalizePhase"/>.
/// AC3: The new counters are exported by the API (unit tests for phase normalization).
/// </summary>
public class NormalizePhaseTests
{
    // ── Identity mappings ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("analysis", "analysis")]
    [InlineData("analysis_review", "analysis_review")]
    [InlineData("codegen", "codegen")]
    [InlineData("acceptance_criteria", "acceptance_criteria")]
    [InlineData("pr_description", "pr_description")]
    [InlineData("reflection", "reflection")]
    [InlineData("decomposition", "decomposition")]
    public void NormalizePhase_IdentityMappings_ReturnExpected(string input, string expected)
    {
        PipelineTelemetry.NormalizePhase(input).Should().Be(expected);
    }

    // ── review_{agent} collapse ────────────────────────────────────────────────

    [Theory]
    [InlineData("review_Correctness")]
    [InlineData("review_Security")]
    [InlineData("review_Performance")]
    [InlineData("review_anything")]
    [InlineData("review_")]
    public void NormalizePhase_ReviewAgent_CollapsesToReview(string input)
    {
        // TODO: [WARNING] There is no test for bare "review" (without underscore suffix). The
        // NormalizePhase switch does not have an explicit case for "review", so
        // NormalizePhase("review") falls through to "other". If "review" were ever used as a raw
        // phase key, it would be silently bucketed as "other" rather than "review". Add an
        // InlineData("review") case here if bare "review" should collapse to "review", or add a
        // separate test asserting it returns "other" to make the current behavior explicit.
        PipelineTelemetry.NormalizePhase(input).Should().Be("review");
    }

    [Fact]
    public void NormalizePhase_Fix_CollapsesToReview()
    {
        PipelineTelemetry.NormalizePhase("fix").Should().Be("review");
    }

    // ── decomposition variants collapse ───────────────────────────────────────

    [Theory]
    [InlineData("decomposition_analysis")]
    [InlineData("decomposition_review")]
    [InlineData("decomposition_refinement")]
    public void NormalizePhase_DecompositionVariants_CollapseToDecomposition(string input)
    {
        PipelineTelemetry.NormalizePhase(input).Should().Be("decomposition");
    }

    // ── fallback to other ─────────────────────────────────────────────────────

    [Fact]
    public void NormalizePhase_Null_ReturnsOther()
    {
        PipelineTelemetry.NormalizePhase(null).Should().Be("other");
    }

    [Fact]
    public void NormalizePhase_Empty_ReturnsOther()
    {
        PipelineTelemetry.NormalizePhase("").Should().Be("other");
    }

    [Theory]
    [InlineData("unknown_phase")]
    [InlineData("brain_sync")]
    [InlineData("setup")]
    [InlineData("warm_up")]
    public void NormalizePhase_UnknownPhase_ReturnsOther(string input)
    {
        PipelineTelemetry.NormalizePhase(input).Should().Be("other");
    }

    // ── RunPhases.All coverage ─────────────────────────────────────────────────

    [Fact]
    public void RunPhases_All_ContainsExactlyNinePhases()
    {
        PipelineTelemetry.RunPhases.All.Should().HaveCount(9);
    }

    [Fact]
    public void RunPhases_All_ContainsAllExpectedPhases()
    {
        PipelineTelemetry.RunPhases.All.Should().Contain([
            "analysis", "analysis_review", "codegen", "review",
            "acceptance_criteria", "pr_description", "reflection",
            "decomposition", "other"
        ]);
    }

    [Fact]
    public void NormalizePhase_ReturnsOnlyClosedSetValues()
    {
        // Every input that can naturally occur should return a value in RunPhases.All
        string[] inputs = [
            "analysis", "analysis_review", "codegen",
            "review_Correctness", "review_Security", "fix",
            "acceptance_criteria", "pr_description", "reflection",
            "decomposition", "decomposition_analysis", "decomposition_review", "decomposition_refinement",
            "unknown", null!, ""
        ];

        foreach (var input in inputs)
        {
            var result = PipelineTelemetry.NormalizePhase(input);
            PipelineTelemetry.RunPhases.All.Should().Contain(result,
                because: $"NormalizePhase(\"{input}\") returned \"{result}\" which is not in the closed set");
        }
    }
}
