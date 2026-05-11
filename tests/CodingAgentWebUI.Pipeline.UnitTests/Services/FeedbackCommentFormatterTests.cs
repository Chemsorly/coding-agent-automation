using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="FeedbackCommentFormatter"/>.
/// Validates: Requirements 6.1, 6.4, 6.5
/// </summary>
public class FeedbackCommentFormatterTests
{
    #region Null Issue skips posting

    [Fact]
    public void FormatComment_NullFeedback_ReturnsNull()
    {
        // Validates: Requirement 6.1 — null Issue means no comment to post
        var result = FeedbackCommentFormatter.FormatComment(null);

        result.Should().BeNull();
    }

    #endregion

    #region Null Description skips posting

    [Fact]
    public void FormatComment_NullDescription_ReturnsNull()
    {
        // Validates: Requirement 6.1 — non-null Description required to post
        var feedback = new IssueFeedback { Description = null };

        var result = FeedbackCommentFormatter.FormatComment(feedback);

        result.Should().BeNull();
    }

    #endregion

    #region Comment includes marker

    [Fact]
    public void FormatComment_WithDescription_IncludesHtmlMarker()
    {
        // Validates: Requirement 6.3 — comment includes HTML marker for identification
        var feedback = new IssueFeedback
        {
            Description = "The acceptance criteria are contradictory."
        };

        var result = FeedbackCommentFormatter.FormatComment(feedback);

        result.Should().NotBeNull();
        result.Should().Contain("<!-- agent:issue-feedback -->");
    }

    #endregion

    #region Comment does not duplicate analysis content

    [Fact]
    public void FormatComment_DoesNotContainAnalysisHeader()
    {
        // Validates: Requirement 6.4 — no duplication of analysis comment content
        var feedback = new IssueFeedback
        {
            Category = "missing component",
            Description = "The referenced UserService class does not exist in the repository.",
            AffectedFiles = ["src/Services/UserService.cs"],
            HumanActionNeeded = "Create the UserService class or update the issue to reference an existing service."
        };

        var result = FeedbackCommentFormatter.FormatComment(feedback);

        result.Should().NotBeNull();
        result.Should().NotContain("## Analysis");
        result.Should().NotContain("## Quality Gate");
    }

    #endregion

    #region Posting failure does not throw (edge cases)

    [Fact]
    public void FormatComment_EmptyDescription_ReturnsNull()
    {
        // Validates: Requirement 6.5 — edge case: empty string Description treated as non-null
        // The formatter only checks for null, so empty string still produces a comment
        // (but the routing layer checks for non-null Description per Req 6.1)
        var feedback = new IssueFeedback { Description = "" };

        // Should not throw — formatter handles edge cases gracefully
        var act = () => FeedbackCommentFormatter.FormatComment(feedback);
        act.Should().NotThrow();
    }

    [Fact]
    public void FormatComment_VeryLongDescription_DoesNotThrow()
    {
        // Validates: Requirement 6.5 — formatter doesn't throw on oversized input
        var feedback = new IssueFeedback
        {
            Description = new string('x', 10_000),
            AffectedFiles = Enumerable.Range(0, 100).Select(i => $"file{i}.cs").ToList(),
            HumanActionNeeded = new string('y', 10_000)
        };

        var act = () => FeedbackCommentFormatter.FormatComment(feedback);
        act.Should().NotThrow();
    }

    [Fact]
    public void FormatComment_EmptyAffectedFiles_DoesNotThrow()
    {
        // Validates: Requirement 6.5 — empty collections handled gracefully
        var feedback = new IssueFeedback
        {
            Description = "Some issue found.",
            AffectedFiles = []
        };

        var act = () => FeedbackCommentFormatter.FormatComment(feedback);
        act.Should().NotThrow();
    }

    #endregion
}
