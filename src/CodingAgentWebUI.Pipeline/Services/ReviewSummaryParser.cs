namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Parses the review summary agent's output into change summary and verdict sections.
/// Returns null for either field when the expected heading is missing or empty.
/// </summary>
internal static class ReviewSummaryParser
{
    private const string ChangeSummaryHeading = "## Change Summary";
    private const string ReviewVerdictHeading = "## Review Verdict";

    /// <summary>
    /// Parses the agent output for <c>## Change Summary</c> and <c>## Review Verdict</c> sections.
    /// </summary>
    /// <param name="agentOutput">Raw text output from the summary agent.</param>
    /// <returns>A tuple of (changeSummary, verdictSummary). Either or both may be null if the heading is missing or content is empty.</returns>
    public static (string? ChangeSummary, string? VerdictSummary) Parse(string? agentOutput)
    {
        if (string.IsNullOrWhiteSpace(agentOutput))
            return (null, null);

        var changeSummary = ExtractSection(agentOutput, ChangeSummaryHeading);
        var verdictSummary = ExtractSection(agentOutput, ReviewVerdictHeading);

        return (changeSummary, verdictSummary);
    }

    /// <summary>
    /// Extracts text between the given heading and the next ## heading (or end of string).
    /// Returns null if the heading is not found or the content is empty after trimming.
    /// </summary>
    // TODO: Verify heading is at a line boundary (position 0 or preceded by \n) to avoid
    // false-matching if the heading text appears mid-line in echoed context.
    private static string? ExtractSection(string text, string heading)
    {
        var headingIndex = text.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (headingIndex < 0)
            return null;

        var contentStart = headingIndex + heading.Length;

        // Find the next ## heading after this one
        // TODO: Handle edge case where "## " immediately follows without a newline separator.
        var nextHeadingIndex = text.IndexOf("\n## ", contentStart, StringComparison.Ordinal);
        if (nextHeadingIndex < 0)
            nextHeadingIndex = text.IndexOf("\r\n## ", contentStart, StringComparison.Ordinal);

        var content = nextHeadingIndex >= 0
            ? text[contentStart..nextHeadingIndex]
            : text[contentStart..];

        var trimmed = content.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Truncates text at the last sentence boundary (". ") before the max length, appending "..." if truncated.
    /// If no sentence boundary is found, truncates at the character limit.
    /// </summary>
    /// <param name="text">Text to truncate.</param>
    /// <param name="maxLength">Maximum character length (default 500).</param>
    /// <returns>The original text if within limit, otherwise truncated text with "..." suffix.</returns>
    // TODO: Collapse internal newlines (\r\n, \n) to spaces before returning, since callers embed
    // the result inline in markdown (e.g., "**Changes**: {text}") and newlines break bold formatting.
    public static string? TruncateAtSentenceBoundary(string? text, int maxLength = 500)
    {
        if (text is null)
            return null;

        if (text.Length <= maxLength)
            return text;

        // Find the last ". " before the limit
        var searchRange = text[..maxLength];
        var lastSentenceEnd = searchRange.LastIndexOf(". ", StringComparison.Ordinal);

        if (lastSentenceEnd > 0)
        {
            // Include the period itself
            return text[..(lastSentenceEnd + 1)] + "...";
        }

        // No sentence boundary found — hard truncate
        return text[..maxLength] + "...";
    }
}
