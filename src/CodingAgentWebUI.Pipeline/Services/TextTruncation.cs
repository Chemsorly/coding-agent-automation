namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Shared text truncation utilities for rendering summaries in formatters.
/// </summary>
internal static class TextTruncation
{
    /// <summary>
    /// Truncates text at the last sentence boundary (". " pattern) before <paramref name="maxLength"/>.
    /// Returns the original text unchanged if within limit. Appends "..." when truncation occurs.
    /// </summary>
    // TODO: [REV-04] Parameter declared as non-nullable `string` but the null guard returns text as-is when null
    //   (via null-forgiving callers). Consider changing signature to `string? text` with return type `string?`,
    //   or return string.Empty for null/empty input to align the nullability contract with actual behavior.
    internal static string TruncateAtSentenceBoundary(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;

        // Look for the last ". " within the allowed range
        var searchRange = text[..maxLength];
        var lastSentenceEnd = searchRange.LastIndexOf(". ", StringComparison.Ordinal);

        // Only use sentence boundary if it produces a meaningful truncation (> 20% of max)
        if (lastSentenceEnd > maxLength / 5)
        {
            return text[..(lastSentenceEnd + 1)] + "...";
        }

        // No suitable sentence boundary — hard truncate
        return text[..maxLength] + "...";
    }
}
