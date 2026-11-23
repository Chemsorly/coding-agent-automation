using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="IssueCommentCaps"/>: the limit applies per kind of author, and a list without unverified
/// comments gives what <c>Take</c> and <c>TakeLast</c> gave before.
/// </summary>
public class IssueCommentCapsTests
{
    private static IssueComment Comment(string id, bool? verified) => new()
    {
        Id = id,
        Author = "someone",
        Body = $"body {id}",
        CreatedAt = DateTime.UnixEpoch,
        IsVerified = verified
    };

    private static List<IssueComment> Comments(int count, bool? verified, string prefix) =>
        Enumerable.Range(1, count).Select(i => Comment($"{prefix}{i}", verified)).ToList();

    [Fact]
    public void TakeFirstPerKind_AllVerified_EqualsTake()
    {
        var comments = Comments(60, verified: null, "c");

        var result = IssueCommentCaps.TakeFirstPerKind(comments, 50);

        result.Select(c => c.Id).Should().Equal(comments.Take(50).Select(c => c.Id));
    }

    [Fact]
    public void TakeLastPerKind_AllVerified_EqualsTakeLast()
    {
        var comments = Comments(60, verified: null, "c");

        var result = IssueCommentCaps.TakeLastPerKind(comments, 50);

        result.Select(c => c.Id).Should().Equal(comments.TakeLast(50).Select(c => c.Id));
    }

    [Fact]
    public void TakeFirstPerKind_MixedKinds_KeepsLimitOfEachInOriginalOrder()
    {
        // Interleaved: u1 v1 u2 v2 u3 v3 u4 v4 u5 v5 u6 v6
        var comments = Enumerable.Range(1, 6)
            .SelectMany(i => new[] { Comment($"u{i}", false), Comment($"v{i}", true) })
            .ToList();

        var result = IssueCommentCaps.TakeFirstPerKind(comments, 3);

        result.Select(c => c.Id).Should().Equal("u1", "v1", "u2", "v2", "u3", "v3");
    }

    [Fact]
    public void TakeLastPerKind_MixedKinds_KeepsLimitOfEachInOriginalOrder()
    {
        // 5 unverified, then 4 verified: the unverified ones must not push the verified ones out.
        var comments = Comments(5, verified: false, "u").Concat(Comments(4, verified: true, "v")).ToList();

        var result = IssueCommentCaps.TakeLastPerKind(comments, 3);

        result.Select(c => c.Id).Should().Equal("u3", "u4", "u5", "v2", "v3", "v4");
    }
}
