namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Parses the AI-generated review summary output into change summary and verdict sections.
/// Non-fatal: returns (null, null) on malformed or missing output.
/// </summary>
public static class ReviewSummaryParser
{
    private const string ChangeSummaryHeading = "## Change Summary";
    private const string ReviewVerdictHeading = "## Review Verdict";

    /// <summary>
    /// Parses the agent output for <c>## Change Summary</c> and <c>## Review Verdict</c> sections.
    /// Returns (null, null) if the output is empty, null, or does not contain the expected headings.
    /// </summary>
    public static (string? ChangeSummary, string? VerdictSummary) Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (null, null);

        var changeSummary = ExtractSection(output, ChangeSummaryHeading);
        var verdictSummary = ExtractSection(output, ReviewVerdictHeading);

        // If neither section is found, treat as malformed
        if (changeSummary is null && verdictSummary is null)
            return (null, null);

        return (changeSummary, verdictSummary);
    }

    /// <summary>
    /// Truncates text at the last sentence boundary (period followed by space or end) before the max length.
    /// Appends "..." if truncated.
    /// </summary>
    public static string? TruncateAtSentenceBoundary(string? text, int maxLength = 500)
    {
        if (text is null || text.Length <= maxLength)
            return text;

        // Find the last ". " before maxLength, or last "." at the boundary
        var searchSpace = text.AsSpan(0, maxLength);
        var lastSentenceEnd = -1;

        for (var i = searchSpace.Length - 1; i >= 0; i--)
        {
            if (searchSpace[i] == '.')
            {
                // Accept ". " or "." at end of search space
                if (i + 1 < searchSpace.Length && searchSpace[i + 1] == ' ')
                {
                    lastSentenceEnd = i + 1; // Include the period
                    break;
                }
                else if (i == searchSpace.Length - 1)
                {
                    lastSentenceEnd = i + 1;
                    break;
                }
            }
        }

        if (lastSentenceEnd > 0)
            return text[..lastSentenceEnd] + "...";

        // No sentence boundary found — hard truncate at maxLength
        return text[..maxLength] + "...";
    }

    private static string? ExtractSection(string output, string heading)
    {
        var headingIndex = output.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (headingIndex < 0)
            return null;

        // Start after the heading line
        var contentStart = output.IndexOf('\n', headingIndex);
        if (contentStart < 0)
            return null;

        contentStart++; // Skip the newline

        // Find the next ## heading or end of string
        var nextHeading = output.IndexOf("\n##", contentStart, StringComparison.Ordinal);
        var content = nextHeading >= 0
            ? output[contentStart..nextHeading]
            : output[contentStart..];

        var trimmed = content.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
