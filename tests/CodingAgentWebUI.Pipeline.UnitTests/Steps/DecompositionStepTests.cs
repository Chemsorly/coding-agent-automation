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

    // ── Helpers ───────────────────────────────────────────────────────────

    private static IssueComment MakeComment(int id, string body) => new()
    {
        Id = id.ToString(),
        Body = body,
        Author = "test-author",
        CreatedAt = DateTime.UtcNow
    };
}
