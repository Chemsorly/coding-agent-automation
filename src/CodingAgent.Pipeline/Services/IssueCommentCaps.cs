using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Caps a comment list per kind, so that comments by unverified authors cannot push verified ones out.
/// A null <see cref="IssueComment.IsVerified"/> comes from an older API and counts as verified.
/// </summary>
public static class IssueCommentCaps
{
    /// <summary>Keeps the first <paramref name="limit"/> comments of each kind, in the given order.</summary>
    public static IReadOnlyList<IssueComment> TakeFirstPerKind(IReadOnlyList<IssueComment> comments, int limit) =>
        Merge(comments,
            comments.Where(c => c.IsVerified != false).Take(limit),
            comments.Where(c => c.IsVerified == false).Take(limit));

    /// <summary>Keeps the last <paramref name="limit"/> comments of each kind, in the given order.</summary>
    public static IReadOnlyList<IssueComment> TakeLastPerKind(IReadOnlyList<IssueComment> comments, int limit) =>
        Merge(comments,
            comments.Where(c => c.IsVerified != false).TakeLast(limit),
            comments.Where(c => c.IsVerified == false).TakeLast(limit));

    private static IReadOnlyList<IssueComment> Merge(
        IReadOnlyList<IssueComment> comments, IEnumerable<IssueComment> verified, IEnumerable<IssueComment> unverified)
    {
        var kept = verified.Concat(unverified).ToHashSet();
        return comments.Where(kept.Contains).ToList().AsReadOnly();
    }
}
