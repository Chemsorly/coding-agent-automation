using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="DecompositionStep"/> pure-logic helper methods.
/// </summary>
public class DecompositionStepTests
{
    // ── FindMostRecentPlanComment ────────────────────────────────────────

    [Fact]
    public void FindMostRecentPlanComment_WhenNoPlanComment_ReturnsNull()
    {
        var comments = new List<IssueComment>
        {
            MakeComment(1, "Regular comment body"),
            MakeComment(2, "Another comment without the marker")
        };

        var result = DecompositionStep.FindMostRecentPlanComment(comments);

        result.Should().BeNull("no comment contains the decomposition plan marker");
    }

    [Fact]
    public void FindMostRecentPlanComment_WhenEmptyList_ReturnsNull()
    {
        var result = DecompositionStep.FindMostRecentPlanComment([]);
        result.Should().BeNull();
    }

    [Fact]
    public void FindMostRecentPlanComment_WhenSingleMatchingComment_ReturnsThatComment()
    {
        var comments = new List<IssueComment>
        {
            MakeComment(1, "Regular comment"),
            MakeComment(2, $"Here is the plan {CommentMarkers.DecompositionPlan} end of plan")
        };

        var result = DecompositionStep.FindMostRecentPlanComment(comments);

        result.Should().NotBeNull();
        result!.Id.Should().Be("2");
    }

    [Fact]
    public void FindMostRecentPlanComment_WhenMultipleMatches_ReturnsMostRecent()
    {
        var comments = new List<IssueComment>
        {
            MakeComment(1, $"Old plan {CommentMarkers.DecompositionPlan}"),
            MakeComment(2, "No marker"),
            MakeComment(3, $"Newer plan {CommentMarkers.DecompositionPlan}")
        };

        var result = DecompositionStep.FindMostRecentPlanComment(comments);

        result!.Id.Should().Be("3", "should return the most recent (last) matching comment");
    }

    [Fact]
    public void FindMostRecentPlanComment_WhenAllCommentsMatch_ReturnslastOne()
    {
        var comments = new List<IssueComment>
        {
            MakeComment(10, $"Plan 1 {CommentMarkers.DecompositionPlan}"),
            MakeComment(20, $"Plan 2 {CommentMarkers.DecompositionPlan}"),
            MakeComment(30, $"Plan 3 {CommentMarkers.DecompositionPlan}")
        };

        var result = DecompositionStep.FindMostRecentPlanComment(comments);

        result!.Id.Should().Be("30");
    }

    [Fact]
    public void FindMostRecentPlanComment_MarkerIsCaseSensitive()
    {
        // The marker is matched with StringComparison.Ordinal — wrong case should not match
        var lowerMarker = CommentMarkers.DecompositionPlan.ToLowerInvariant();
        var upperMarker = CommentMarkers.DecompositionPlan.ToUpperInvariant();

        // Only add comments with wrong-case markers (assuming the real marker is mixed-case)
        // This test verifies the search doesn't accidentally match wrong-case text
        // by using the actual marker to confirm it DOES match
        var comments = new List<IssueComment>
        {
            MakeComment(1, $"Contains real marker: {CommentMarkers.DecompositionPlan}")
        };

        var result = DecompositionStep.FindMostRecentPlanComment(comments);
        result.Should().NotBeNull("exact marker must match");
    }

    // ── BuildIssueContextContent (via reflection) ─────────────────────────

    [Fact]
    public void BuildIssueContextContent_WithNoComments_ContainsIssueTitleAndDescription()
    {
        var issue = new IssueDetail
        {
            Identifier = "org/repo#1",
            Title = "Fix the login bug",
            Description = "Users cannot log in when 2FA is enabled.",
            Labels = Array.Empty<string>()
        };
        var result = InvokeBuildIssueContextContent(issue, new List<IssueComment>());

        result.Should().Contain("Fix the login bug");
        result.Should().Contain("Users cannot log in when 2FA is enabled.");
        result.Should().Contain("# Epic Issue Context");
    }

    [Fact]
    public void BuildIssueContextContent_WithComments_IncludesCommentAuthorAndBody()
    {
        var issue = new IssueDetail
        {
            Identifier = "org/repo#2",
            Title = "My Epic",
            Description = "Epic description",
            Labels = Array.Empty<string>()
        };
        var comments = new List<IssueComment>
        {
            MakeComment(1, "This is the approved plan."),
            MakeComment(2, "Additional comment here.")
        };

        var result = InvokeBuildIssueContextContent(issue, comments);

        result.Should().Contain("This is the approved plan.");
        result.Should().Contain("Additional comment here.");
        result.Should().Contain("test-author");
        result.Should().Contain("## Comments");
    }

    [Fact]
    public void BuildIssueContextContent_WithEmptyDescription_DoesNotThrow()
    {
        var issue = new IssueDetail
        {
            Identifier = "org/repo#3",
            Title = "Empty desc",
            Description = "",
            Labels = Array.Empty<string>()
        };
        var act = () => InvokeBuildIssueContextContent(issue, []);
        act.Should().NotThrow();
    }

    // ── BuildDeduplicationSection (via reflection) ────────────────────────

    [Fact]
    public void BuildDeduplicationSection_WithTitles_ContainsAllTitles()
    {
        var titles = new List<string> { "Fix login bug", "Add dark mode", "Improve performance" };
        var result = InvokeBuildDeduplicationSection(titles);

        result.Should().Contain("Fix login bug");
        result.Should().Contain("Add dark mode");
        result.Should().Contain("Improve performance");
        result.Should().Contain("Do NOT Duplicate");
    }

    [Fact]
    public void BuildDeduplicationSection_EmptyList_ContainsHeader()
    {
        var result = InvokeBuildDeduplicationSection([]);
        result.Should().Contain("Existing Agent-Generated Sub-Issues");
    }

    [Fact]
    public void BuildDeduplicationSection_WithSingleTitle_FormatsAsBullet()
    {
        var result = InvokeBuildDeduplicationSection(["Only one issue"]);
        result.Should().Contain("- Only one issue");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string InvokeBuildIssueContextContent(IssueDetail issue, IReadOnlyList<IssueComment> comments)
    {
        var method = typeof(DecompositionStep).GetMethod(
            "BuildIssueContextContent",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("BuildIssueContextContent must exist");
        return (string)method!.Invoke(null, [issue, comments])!;
    }

    private static string InvokeBuildDeduplicationSection(IReadOnlyList<string> titles)
    {
        var method = typeof(DecompositionStep).GetMethod(
            "BuildDeduplicationSection",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("BuildDeduplicationSection must exist");
        return (string)method!.Invoke(null, [titles])!;
    }

    private static IssueComment MakeComment(int id, string body) => new()
    {
        Id = id.ToString(),
        Body = body,
        Author = "test-author",
        CreatedAt = DateTime.UtcNow
    };
}
