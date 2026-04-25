namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Parses code review output for severity markers ([CRITICAL], [WARNING], [SUGGESTION]).
/// </summary>
public static class SeverityParser
{
    /// <summary>
    /// Counts occurrences of severity markers in agent output lines.
    /// Matching is case-insensitive.
    /// </summary>
    public static SeverityCounts Parse(IReadOnlyList<string> outputLines)
    {
        ArgumentNullException.ThrowIfNull(outputLines);

        int critical = 0, warning = 0, suggestion = 0;

        foreach (var line in outputLines)
        {
            critical += CountOccurrences(line, "[CRITICAL]");
            warning += CountOccurrences(line, "[WARNING]");
            suggestion += CountOccurrences(line, "[SUGGESTION]");
        }

        return new SeverityCounts(critical, warning, suggestion);
    }

    private static int CountOccurrences(string text, string marker)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += marker.Length;
        }
        return count;
    }
}

/// <summary>
/// Severity counts parsed from code review output.
/// </summary>
public sealed record SeverityCounts(int Critical, int Warning, int Suggestion);
