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
        builder.AppendLine("<!-- agent:issue-feedback -->");
        builder.AppendLine("## 🤖 Agent Feedback — Issue Quality");
        builder.AppendLine();

        // Category (if present)
        if (!string.IsNullOrWhiteSpace(feedback.Category))
        {
            builder.AppendLine($"**Category:** {feedback.Category}");
            builder.AppendLine();
        }

        // Description (always present at this point)
        builder.AppendLine(feedback.Description);
        builder.AppendLine();

        // Affected files (if any)
        if (feedback.AffectedFiles.Count > 0)
        {
            builder.AppendLine("**Affected Files:**");
            foreach (var file in feedback.AffectedFiles)
            {
                builder.AppendLine($"- {file}");
            }
            builder.AppendLine();
        }

        // Human action needed (if present)
        if (!string.IsNullOrWhiteSpace(feedback.HumanActionNeeded))
        {
            builder.AppendLine($"**Action Needed:** {feedback.HumanActionNeeded}");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}
