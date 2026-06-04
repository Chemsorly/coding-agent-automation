using System.Text;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Formats PR conversation comments into the markdown context file
/// consumed by review agents.
/// </summary>
public static class PrConversationContextFormatter
{
    public static string Format(IReadOnlyList<PrConversationComment> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);

        var sb = new StringBuilder();
        sb.AppendLine("# PR Conversation Context");
        sb.AppendLine();

        var discussion = comments.Where(c => c.FilePath is null && c.IsResolved is null).ToList();
        var reviewThreads = comments.Where(c => c.FilePath is not null || c.IsResolved is not null).ToList();

        if (discussion.Count > 0)
        {
            sb.AppendLine("## Discussion Comments");
            sb.AppendLine();
            foreach (var comment in discussion)
            {
                sb.AppendLine($"### {FormatAttribution(comment)} @{comment.Author} ({comment.CreatedAt:yyyy-MM-dd HH:mm} UTC)");
                sb.AppendLine(comment.Body);
                sb.AppendLine();
            }
        }

        if (reviewThreads.Count > 0)
        {
            sb.AppendLine("## Review Thread Comments");
            sb.AppendLine();
            foreach (var comment in reviewThreads)
            {
                var resolved = comment.IsResolved == true ? " (RESOLVED)" : "";
                var location = comment.FilePath is not null
                    ? $" — {comment.FilePath}{(comment.Line.HasValue ? $":{comment.Line}" : "")}"
                    : "";
                sb.AppendLine($"### {FormatAttribution(comment)} @{comment.Author} ({comment.CreatedAt:yyyy-MM-dd HH:mm} UTC){location}{resolved}");
                sb.AppendLine(comment.Body);
                sb.AppendLine();
            }
        }

        if (discussion.Count == 0 && reviewThreads.Count == 0)
        {
            sb.AppendLine("No prior conversation or review comments found.");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    internal static string FormatAttribution(PrConversationComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);

        if (comment.IsBot)
            return "[BOT]";
        if (comment.IsAuthor)
            return "[HUMAN/AUTHOR]";
        return "[HUMAN]";
    }
}
