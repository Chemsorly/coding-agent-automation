using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Formats issue-level feedback into a structured GitHub comment.
/// The comment is clearly labeled as agent feedback and includes an HTML marker
/// for programmatic identification.
/// </summary>
public static class FeedbackCommentFormatter
{
    private const string CategoryLabel = "**Category:**";
    private const string AffectedFilesLabel = "**Affected Files:**";
    private const string ActionNeededLabel = "**Action Needed:**";

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
        builder.AppendLine("## 🤖 Agent Feedback — Issue Quality");
        builder.AppendLine();

        // Category (if present)
        if (!string.IsNullOrWhiteSpace(feedback.Category))
        {
            builder.AppendLine($"{CategoryLabel} {SanitizeMarkdown(feedback.Category)}");
            builder.AppendLine();
        }

        // Description (always present at this point)
        builder.AppendLine(SanitizeMarkdown(feedback.Description));
        builder.AppendLine();

        // Affected files (if any)
        if (feedback.AffectedFiles.Count > 0)
        {
            builder.AppendLine(AffectedFilesLabel);
            foreach (var file in feedback.AffectedFiles)
            {
                builder.AppendLine($"- `{file}`");
            }
            builder.AppendLine();
        }

        // Human action needed (if present)
        if (!string.IsNullOrWhiteSpace(feedback.HumanActionNeeded))
        {
            builder.AppendLine($"{ActionNeededLabel} {SanitizeMarkdown(feedback.HumanActionNeeded)}");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Reads back the category and description of a comment written by <see cref="FormatComment"/> as one
    /// line, <c>"category: description"</c>, cut to <paramref name="maxLength"/> characters. Returns null when
    /// the comment has no description.
    /// </summary>
    public static string? ReadSummary(string commentBody, int maxLength = 500)
    {
        ArgumentNullException.ThrowIfNull(commentBody);

        string? category = null;
        var description = new System.Text.StringBuilder();
        foreach (var rawLine in commentBody.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line == CommentMarkers.IssueFeedback || line.StartsWith("## ", StringComparison.Ordinal))
                continue;
            if (line.StartsWith(AffectedFilesLabel, StringComparison.Ordinal) || line.StartsWith(ActionNeededLabel, StringComparison.Ordinal))
                break;
            if (line.StartsWith(CategoryLabel, StringComparison.Ordinal))
            {
                category = Unescape(line[CategoryLabel.Length..].Trim());
                continue;
            }

            description.Append(line).Append(' ');
        }

        var text = Unescape(description.ToString().Trim());
        if (text.Length == 0)
            return null;
        if (text.Length > maxLength)
            text = text[..maxLength].TrimEnd() + "…";

        return string.IsNullOrEmpty(category) ? text : $"{category}: {text}";
    }

    private static string Unescape(string value) => value
        .Replace("@\u200B", "@")
        .Replace("&lt;", "<")
        .Replace("&gt;", ">");

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
