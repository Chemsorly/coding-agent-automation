using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Extracts issue dependency references from issue body text.
/// Recognizes patterns: "Blocked by #N", "Depends on #N", "Requires #N", "After #N".
/// Also handles identifiers without # prefix when they contain letters (e.g., "Depends on PROJ-123").
/// Word boundary prevents false positives from words ending in keywords (e.g., "hereafter #123").
/// </summary>
public static class DependencyParser
{
    // Matches either #digits or an identifier starting with a letter (e.g., PROJ-123)
    private static readonly Regex DependencyPattern = new(
        @"\b(?:blocked\s+by|depends\s+on|requires|after)\s+(?:#(\d+)|([A-Za-z][\w-]*))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses the issue body and returns unique issue numbers referenced as dependencies.
    /// </summary>
    /// <param name="body">The issue body text (may be null or empty).</param>
    /// <param name="selfIdentifier">Optional issue number to exclude self-references.</param>
    /// <returns>Unique issue numbers referenced as dependencies.</returns>
    public static IReadOnlyList<int> Parse(string? body, int? selfIdentifier = null)
    {
        if (string.IsNullOrEmpty(body))
            return Array.Empty<int>();

        var results = new HashSet<int>();

        foreach (Match match in DependencyPattern.Matches(body))
        {
            // Group 1: #digits pattern; Group 2: alphanumeric identifier
            var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (int.TryParse(value, out var issueNumber) && issueNumber > 0)
            {
                if (selfIdentifier.HasValue && issueNumber == selfIdentifier.Value)
                    continue;

                results.Add(issueNumber);
            }
        }

        return results.ToList();
    }
}
