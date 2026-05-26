using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Extracts issue dependency references from issue body text.
/// Recognizes patterns: "Blocked by #N", "Depends on #N", "Requires #N", "After #N".
/// Word boundary prevents false positives from words ending in keywords (e.g., "hereafter #123").
/// </summary>
public static class DependencyParser
{
    private static readonly Regex DependencyPattern = new(
        @"\b(?:blocked\s+by|depends\s+on|requires|after)\s+#(\d+)",
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
            if (int.TryParse(match.Groups[1].Value, out var issueNumber) && issueNumber > 0)
            {
                if (selfIdentifier.HasValue && issueNumber == selfIdentifier.Value)
                    continue;

                results.Add(issueNumber);
            }
        }

        return results.ToList();
    }
}
