using System.Text.RegularExpressions;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// A dependency reference extracted from an issue body.
/// Either a bare issue number in the same tracker (<see cref="NumberRef"/>)
/// or a full issue URL targeting a specific tracker (<see cref="UrlRef"/>).
/// </summary>
public abstract record DependencyRef; // NOSONAR S2094 — base of a closed record hierarchy; derived records carry the data

/// <summary>A numeric dependency reference (e.g., <c>#42</c>).</summary>
public sealed record NumberRef(int Number) : DependencyRef;

/// <summary>
/// A URL dependency reference (e.g., <c>https://github.com/acme/api/issues/40</c>).
/// Indicates the dependency lives in a specific external tracker.
/// </summary>
public sealed record UrlRef(string Url) : DependencyRef;

/// <summary>
/// Extracts issue dependency references from issue body text.
/// Recognizes patterns: "Blocked by #N", "Depends on #N", "Requires #N", "After #N".
/// Also handles identifiers without # prefix when they contain letters (e.g., "Depends on PROJ-123").
/// Also handles full GitHub and GitLab issue URLs (e.g., "Blocked by https://github.com/org/repo/issues/40").
/// Word boundary prevents false positives from words ending in keywords (e.g., "hereafter #123").
/// </summary>
public static class DependencyParser
{
    // Group 1: GitHub issue URL (https://github.com/{owner}/{repo}/issues/{number})
    // Group 2: GitLab issue URL (https://gitlab.com/{namespace}/{subgroup...}/{project}/-/issues/{number})
    // Group 3: #digits pattern
    // Group 4: alphanumeric identifier (e.g., PROJ-123)
    // The URL alternatives are matched before the short-form alternatives so a URL like
    // "https://github.com/acme/repo/issues/40" is captured by group 1 rather than being
    // partially matched by group 3 (which would only see the trailing "#40" if present).
    private static readonly Regex DependencyPattern = new(
        @"\b(?:blocked\s+by|depends\s+on|requires|after)\s+" +
        @"(?:" +
            @"(https://github\.com/[^/\s]+/[^/\s]+/issues/\d+)" +   // Group 1: GitHub issue URL
            @"|" +
            @"(https://gitlab\.com/[^/\s]+(?:/[^/\s]+)+/-/issues/\d+)" + // Group 2: GitLab issue URL
            @"|" +
            @"#(\d+)" +                                               // Group 3: #digits
            @"|" +
            @"([A-Za-z][\w-]*)" +                                     // Group 4: alpha identifier (e.g., PROJ-123)
        @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeout: TimeSpan.FromSeconds(2));

    /// <summary>
    /// Parses the issue body and returns unique dependency references.
    /// Returns numeric references (<see cref="NumberRef"/>) for <c>#N</c> patterns and
    /// URL references (<see cref="UrlRef"/>) for full GitHub/GitLab issue URLs.
    /// Returns an empty list on any error (including regex timeout on adversarial input).
    /// </summary>
    /// <param name="body">The issue body text (may be null or empty).</param>
    /// <param name="selfIdentifier">Optional issue number to exclude self-references (numeric only).</param>
    /// <returns>Unique dependency references found in the body.</returns>
    public static IReadOnlyList<DependencyRef> Parse(string? body, int? selfIdentifier = null)
    {
        if (string.IsNullOrEmpty(body))
            return Array.Empty<DependencyRef>();

        var seenNumbers = new HashSet<int>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<DependencyRef>();

        try
        {
            foreach (Match match in DependencyPattern.Matches(body))
                AddMatch(match, selfIdentifier, seenNumbers, seenUrls, results);
        }
        catch (RegexMatchTimeoutException)
        {
            // Adversarial input exceeded timeout — return whatever was collected so far.
            // An empty result means no dependencies are detected, which is safe (pessimistic: run proceeds).
        }

        return results;
    }

    /// <summary>
    /// Adds the dependency reference captured by <paramref name="match"/> to <paramref name="results"/>,
    /// skipping duplicates and self-references.
    /// </summary>
    private static void AddMatch(
        Match match,
        int? selfIdentifier,
        HashSet<int> seenNumbers,
        HashSet<string> seenUrls,
        List<DependencyRef> results)
    {
        if (match.Groups[1].Success)
        {
            // GitHub issue URL
            AddUrl(match.Groups[1].Value, seenUrls, results);
        }
        else if (match.Groups[2].Success)
        {
            // GitLab issue URL
            AddUrl(match.Groups[2].Value, seenUrls, results);
        }
        else if (match.Groups[3].Success)
        {
            // #digits
            AddNumber(match.Groups[3].Value, selfIdentifier, seenNumbers, results);
        }
        // Group 4 (alpha identifier like PROJ-123) is never a numeric dependency — skip.
    }

    private static void AddUrl(string url, HashSet<string> seenUrls, List<DependencyRef> results)
    {
        if (seenUrls.Add(url))
            results.Add(new UrlRef(url));
    }

    private static void AddNumber(string value, int? selfIdentifier, HashSet<int> seenNumbers, List<DependencyRef> results)
    {
        if (!int.TryParse(value, out var issueNumber) || issueNumber <= 0)
            return;
        if (selfIdentifier.HasValue && issueNumber == selfIdentifier.Value)
            return;
        if (seenNumbers.Add(issueNumber))
            results.Add(new NumberRef(issueNumber));
    }
}
