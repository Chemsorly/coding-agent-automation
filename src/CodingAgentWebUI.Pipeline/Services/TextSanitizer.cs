namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Shared text sanitization utilities for agent-produced content before GitHub API calls.
/// Extracted from RefactoringExecutor for reuse across decomposition, refactoring, and other pipelines.
/// </summary>
public static class TextSanitizer
{
    private const int MaxTitleLength = 200;

    /// <summary>
    /// Sanitizes a title for GitHub issue creation.
    /// Strips newlines, trims whitespace, truncates to 200 characters.
    /// </summary>
    public static string SanitizeTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var sanitized = title
            .Replace("\r", "")
            .Replace("\n", " ")
            .Trim();
        if (sanitized.Length > MaxTitleLength)
            sanitized = sanitized[..MaxTitleLength].TrimEnd();
        return sanitized;
    }

    /// <summary>
    /// Escapes markdown-sensitive characters to prevent injection in GitHub issues.
    /// Breaks @-mentions with zero-width space, escapes HTML open angle brackets.
    /// GitHub renders <c>&gt;</c> safely in markdown (blockquotes), so only <c>&lt;</c> needs escaping.
    /// </summary>
    public static string SanitizeMarkdown(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value
            .Replace("@", "@\u200B")  // Zero-width space breaks @mention parsing
            .Replace("<", "&lt;");    // Prevent HTML injection
    }
}
