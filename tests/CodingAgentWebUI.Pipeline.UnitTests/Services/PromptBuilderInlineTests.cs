using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Prompts;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="PromptBuilder.BuildReviewPrompt"/> inline comment behavior.
/// Validates Req 7: Review Agent Prompt Enhancement.
/// </summary>
public class PromptBuilderInlineTests
{
    private static IssueDetail CreateIssue() => new()
    {
        Identifier = "42",
        Title = "Add feature X",
        Description = "Implement feature X as described.",
        Labels = new[] { "enhancement" }
    };

    private static ParsedIssue CreateParsedIssue() => new()
    {
        RequirementsSection = "Build the thing",
        AcceptanceCriteria = new[] { "It compiles", "Tests pass" }
    };

    private static string BuildPrompt(bool inlineCommentsEnabled)
    {
        var findingsPath = AgentWorkspacePaths.GetReviewFindingsFilePath("TestAgent");
        return PromptBuilder.BuildReviewPrompt(
            "Review this code carefully",
            CreateIssue(),
            CreateParsedIssue(),
            findingsPath,
            inlineCommentsEnabled: inlineCommentsEnabled);
    }

    [Fact]
    public void BuildReviewPrompt_InlineEnabled_AppendsOutputFormatSection()
    {
        var result = BuildPrompt(inlineCommentsEnabled: true);
        result.Should().Contain("## Output Format");
    }

    [Fact]
    public void BuildReviewPrompt_InlineDisabled_DoesNotContainOutputFormatSection()
    {
        var result = BuildPrompt(inlineCommentsEnabled: false);
        result.Should().NotContain("## Output Format");
    }

    [Fact]
    public void BuildReviewPrompt_InlineEnabled_ContainsFormatSpec()
    {
        var result = BuildPrompt(inlineCommentsEnabled: true);
        result.Should().Contain("[SEVERITY] path/to/file.ext:LINE");
    }

    [Fact]
    public void BuildReviewPrompt_InlineEnabled_ContainsExample()
    {
        var result = BuildPrompt(inlineCommentsEnabled: true);
        result.Should().Contain("[CRITICAL] src/Service.cs:42");
        result.Should().Contain("[WARNING] src/Controllers/UserController.cs:15");
    }

    [Fact]
    public void BuildReviewPrompt_InlineEnabled_ContainsSeverityOptions()
    {
        var result = BuildPrompt(inlineCommentsEnabled: true);
        result.Should().Contain("CRITICAL");
        result.Should().Contain("WARNING");
        result.Should().Contain("SUGGESTION");
    }

    [Fact]
    public void BuildReviewPrompt_InlineEnabled_ContainsNoLocationGuidance()
    {
        var result = BuildPrompt(inlineCommentsEnabled: true);
        result.Should().Contain("For findings without a specific file location:");
        result.Should().Contain("[WARNING] — General observation about architecture");
    }

    [Fact]
    public void BuildReviewPrompt_InlineEnabled_PreservesExistingContent()
    {
        var result = BuildPrompt(inlineCommentsEnabled: true);

        // Existing content should still be present
        result.Should().Contain("Review this code carefully");
        result.Should().Contain("Do NOT run git write commands");
        result.Should().Contain("Add feature X");
    }

    [Fact]
    public void BuildReviewPrompt_InlineDisabled_PreservesExistingContent()
    {
        var result = BuildPrompt(inlineCommentsEnabled: false);

        // Existing content should still be present
        result.Should().Contain("Review this code carefully");
        result.Should().Contain("Do NOT run git write commands");
        result.Should().Contain("Add feature X");
    }

    [Fact]
    public void BuildReviewPrompt_InlineEnabled_OutputFormatIsAtEnd()
    {
        var result = BuildPrompt(inlineCommentsEnabled: true);

        // The "## Output Format" section should appear after the issue context
        var outputFormatIndex = result.IndexOf("## Output Format", StringComparison.Ordinal);
        var issueContextIndex = result.IndexOf("Add feature X", StringComparison.Ordinal);

        outputFormatIndex.Should().BeGreaterThan(issueContextIndex,
            "structured output instructions should be appended after issue context");
    }

    [Fact]
    public void BuildReviewPrompt_InlineDisabled_PromptMatchesBaselineExactly()
    {
        // When disabled, the prompt should be identical to calling without the parameter
        var findingsPath = AgentWorkspacePaths.GetReviewFindingsFilePath("TestAgent");
        var withFalse = PromptBuilder.BuildReviewPrompt(
            "Review", CreateIssue(), CreateParsedIssue(), findingsPath, inlineCommentsEnabled: false);
        var withoutParam = PromptBuilder.BuildReviewPrompt(
            "Review", CreateIssue(), CreateParsedIssue(), findingsPath);

        withFalse.Should().Be(withoutParam);
    }
}
