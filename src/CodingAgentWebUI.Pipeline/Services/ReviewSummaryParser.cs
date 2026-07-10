namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Parses the output of the review summary agent into structured change summary and verdict fields.
/// Returns (null, null) for any malformed, empty, or missing output — non-fatal by design.
/// </summary>
internal static class ReviewSummaryParser
{
    private const string ChangeSummaryHeading = "## Change Summary";
    private const string ReviewVerdictHeading = "## Review Verdict";

    /// <summary>
    /// Extracts the Change Summary and Review Verdict sections from agent output.
    /// </summary>
    /// <returns>A tuple of (changeSummary, verdictSummary). Either or both may be null.</returns>
    internal static (string? ChangeSummary, string? VerdictSummary) Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (null, null);

        var changeSummary = ExtractSection(output, ChangeSummaryHeading);
        var verdictSummary = ExtractSection(output, ReviewVerdictHeading);

        return (changeSummary, verdictSummary);
    }

    private static string? ExtractSection(string text, string heading)
    {
        var headingIndex = text.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (headingIndex < 0)
            return null;

        // Start after the heading line
        var contentStart = text.IndexOf('\n', headingIndex);
        if (contentStart < 0)
            return null;

        contentStart++; // Skip the newline

        // Find the next ## heading (end of this section)
        var nextHeadingIndex = text.IndexOf("\n## ", contentStart, StringComparison.Ordinal);
        var sectionContent = nextHeadingIndex >= 0
            ? text[contentStart..nextHeadingIndex]
            : text[contentStart..];

        var trimmed = sectionContent.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
