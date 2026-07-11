using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for PhaseNameFormatter.HumanizePhase.
/// </summary>
public class PhaseNameFormatterTests
{
    [Theory]
    [InlineData("review_Correctness", "Review: Correctness")]
    [InlineData("review_Security", "Review: Security")]
    [InlineData("review_Performance", "Review: Performance")]
    public void HumanizePhase_ReviewPrefix_FormatsCorrectly(string input, string expected)
    {
        PhaseNameFormatter.HumanizePhase(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("follow_up_Security", "Follow-up: Security")]
    [InlineData("follow_up_Correctness", "Follow-up: Correctness")]
    public void HumanizePhase_FollowUpPrefix_FormatsCorrectly(string input, string expected)
    {
        PhaseNameFormatter.HumanizePhase(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("acceptance_criteria", "Acceptance criteria")]
    [InlineData("pr_description", "Pr description")]
    [InlineData("decomposition_analysis", "Decomposition analysis")]
    public void HumanizePhase_PlainPhase_TitleCasesAndReplacesUnderscores(string input, string expected)
    {
        PhaseNameFormatter.HumanizePhase(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("fix", "Fix")]
    [InlineData("analysis", "Analysis")]
    [InlineData("codegen", "Codegen")]
    [InlineData("reflection", "Reflection")]
    [InlineData("decomposition", "Decomposition")]
    public void HumanizePhase_SingleWord_TitleCases(string input, string expected)
    {
        PhaseNameFormatter.HumanizePhase(input).Should().Be(expected);
    }
}
