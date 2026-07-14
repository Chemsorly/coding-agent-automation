namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Parses the output of the review summary agent into change summary and verdict sections.
/// Returns (null, null) on malformed or empty output — never throws.
/// </summary>
internal static class ReviewSummaryParser
{
    private const string ChangeSummaryHeading = "## Change Summary";
    private const string ReviewVerdictHeading = "## Review Verdict";

    /// <summary>
    /// Parses agent output for <c>## Change Summary</c> and <c>## Review Verdict</c> sections.
    /// Returns both as trimmed strings, or null for either/both if the heading is missing.
    /// </summary>
    public static (string? ChangeSummary, string? VerdictSummary) Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (null, null);

        var changeSummary = ExtractSection(output, ChangeSummaryHeading);
        var verdictSummary = ExtractSection(output, ReviewVerdictHeading);

        return (changeSummary, verdictSummary);
    }

    /// <summary>
    /// Truncates text to at most <paramref name="maxLength"/> characters, breaking at the
    /// last sentence boundary (period followed by space or end-of-string) before the limit.
    /// Appends "..." if truncated.
    /// </summary>
    public static string? TruncateAtSentenceBoundary(string? text, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        if (text.Length <= maxLength)
            return text;

        // TODO: Sentence boundary detection uses ". " which produces false positives on abbreviations
        // and decimal numbers (e.g., "v2.0 of the API" truncates mid-sentence). Consider a heuristic
        // that skips periods preceded by single uppercase letters or digits. In practice LLM-generated
        // summaries of 2-3 sentences rarely exceed 500 chars so truncation seldom triggers.
        // Find last ". " before the limit, or last "." at end of substring
        var searchArea = text.AsSpan(0, maxLength);
        var lastSentenceEnd = -1;

        for (var i = searchArea.Length - 1; i >= 0; i--)
        {
            if (searchArea[i] == '.')
            {
                // Accept ". " or "." at end of search area
                if (i + 1 < searchArea.Length && searchArea[i + 1] == ' ')
                {
                    lastSentenceEnd = i + 1; // Include the period
                    break;
                }
                else if (i == searchArea.Length - 1)
                {
                    lastSentenceEnd = i + 1;
                    break;
                }
            }
        }

        if (lastSentenceEnd > 0)
            return text[..lastSentenceEnd] + "...";

        // No sentence boundary found — hard-truncate at maxLength
        return text[..maxLength] + "...";
    }

    private static string? ExtractSection(string output, string heading)
    {
        var headingIndex = output.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (headingIndex < 0)
            return null;

        // Skip past the heading line
        var contentStart = output.IndexOf('\n', headingIndex);
        if (contentStart < 0)
            return null;
        contentStart++; // skip the newline

        // Find the next ## heading or end of string
        var nextHeadingIndex = output.IndexOf("\n## ", contentStart, StringComparison.Ordinal);
        var contentEnd = nextHeadingIndex >= 0 ? nextHeadingIndex : output.Length;

        var content = output[contentStart..contentEnd].Trim();
        return string.IsNullOrEmpty(content) ? null : content;
    }
}
