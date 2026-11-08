using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for <see cref="IssueComment.HasVerifiedMarker"/>: a marker counts unless the provider reported the
/// author as outside the repository. A null <see cref="IssueComment.IsVerified"/> (an older API) counts.
/// </summary>
public class IssueCommentMarkerTests
{
    private static IssueComment MakeComment(string body, bool? isVerified) => new()
    {
        Author = "alice",
        Body = body,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Id = "1",
        IsVerified = isVerified
    };

    [Theory]
    [InlineData(true, true)]
    [InlineData(null, true)]
    [InlineData(false, false)]
    public void HasVerifiedMarker_FollowsIsVerified(bool? isVerified, bool expected)
    {
        var comment = MakeComment($"{CommentMarkers.DecompositionPlan}\nthe plan", isVerified);

        comment.HasVerifiedMarker(CommentMarkers.DecompositionPlan).Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    [InlineData(false)]
    public void HasVerifiedMarker_WithoutTheMarker_IsFalse(bool? isVerified)
    {
        var comment = MakeComment("an ordinary comment", isVerified);

        comment.HasVerifiedMarker(CommentMarkers.DecompositionPlan).Should().BeFalse();
    }
}
