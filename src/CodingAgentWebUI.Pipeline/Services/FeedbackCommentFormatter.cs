using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Formats issue-level feedback into a structured GitHub comment.
/// The comment is clearly labeled as agent feedback and includes an HTML marker
/// for programmatic identification.
/// </summary>
public static class FeedbackCommentFormatter
{
    /// <summary>
    /// Formats an <see cref="IssueFeedback"/> into a markdown comment suitable for posting on a GitHub issue.
    /// Returns null if the feedback has no Description (nothing meaningful to post).
    /// </summary>
    public static string? FormatComment(IssueFeedback? feedback)
    {
        if (feedback?.Description is null)
            return null;

        var builder = new System.Text.StringBuilder();

        // HTML marker for identification (must not be duplicated by analysis or gate-rejection comments)
        builder.AppendLine(CommentMarkers.IssueFeedback);
        builder.AppendLine(CommentMarkers.IssueFeedbackHeader);
        builder.AppendLine();

        // Category (if present)
        if (!string.IsNullOrWhiteSpace(feedback.Category))
        {
            builder.AppendLine($"**Category:** {SanitizeMarkdown(feedback.Category)}");
            builder.AppendLine();
        }

        // Description (always present at this point)
        builder.AppendLine(SanitizeMarkdown(feedback.Description));
        builder.AppendLine();

        // Affected files (if any)
        if (feedback.AffectedFiles.Count > 0)
        {
            builder.AppendLine("**Affected Files:**");
            foreach (var file in feedback.AffectedFiles)
            {
                builder.AppendLine($"- `{file}`");
            }
            builder.AppendLine();
        }

        // Human action needed (if present)
        if (!string.IsNullOrWhiteSpace(feedback.HumanActionNeeded))
        {
            builder.AppendLine($"**Action Needed:** {SanitizeMarkdown(feedback.HumanActionNeeded)}");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string SanitizeMarkdown(string value)
    {
        // Escape @mentions to prevent pinging GitHub users
        // Wrap in a way that prevents markdown interpretation
        return value
            .Replace("@", "@\u200B")  // Zero-width space breaks @mention parsing
            .Replace("<", "&lt;")     // Prevent HTML injection
            .Replace(">", "&gt;");
    }
}
