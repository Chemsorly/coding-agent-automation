using System.Text;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Writes the comments of an epic into its context file, for both decomposition steps. Comments by verified authors
/// go under "## Comments" as they are. Comments by unverified authors go under "## Unverified input", normalized,
/// after the verified ones. The steps differ only in the heading of a comment and in the rule above "## Comments".
/// </summary>
internal static class EpicContextComments
{
    /// <summary>
    /// Appends the comments to <paramref name="sb"/>. A comment with <see cref="IssueComment.IsVerified"/> null counts
    /// as verified.
    /// </summary>
    /// <param name="heading">The heading line of one comment, without its line break.</param>
    /// <param name="dividerBeforeComments">Whether a "---" line comes before "## Comments".</param>
    /// <returns>Whether at least one comment is by an unverified author.</returns>
    public static bool Append(
        StringBuilder sb, IReadOnlyList<IssueComment> comments, Func<IssueComment, string> heading, bool dividerBeforeComments)
    {
        var verified = comments.Where(c => c.IsVerified != false).ToList();
        var unverified = comments.Where(c => c.IsVerified == false).ToList();

        if (verified.Count > 0)
        {
            if (dividerBeforeComments)
            {
                sb.AppendLine("---");
                sb.AppendLine();
            }

            sb.AppendLine("## Comments");
            sb.AppendLine();
            verified.ForEach(c => AppendComment(sb, heading(c), c.Body));
        }

        if (unverified.Count > 0)
        {
            UnverifiedInputText.AppendSectionStart(sb);
            unverified.ForEach(c => AppendComment(sb, heading(c), AgentTextNormalizer.Normalize(c.Body)));
        }

        return unverified.Count > 0;
    }

    private static void AppendComment(StringBuilder sb, string heading, string body)
    {
        sb.AppendLine(heading);
        sb.AppendLine();
        sb.AppendLine(body);
        sb.AppendLine();
    }
}
