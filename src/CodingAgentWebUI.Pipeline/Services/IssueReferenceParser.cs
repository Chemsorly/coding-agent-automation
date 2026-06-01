using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Extracts issue references from PR/MR title and description text.
/// Provides two parsing modes:
/// <list type="bullet">
///   <item><see cref="ParseClosingKeywords"/> — base closing keywords (Closes/Fixes/Resolves #N)</item>
///   <item><see cref="ParseIssueReferences"/> — all GitHub patterns (closing keywords with all verb forms, GH-N, cross-repo, simple #N)</item>
/// </list>
/// </summary>
public static class IssueReferenceParser
{
    // GitHub closing keywords: close/closes/closed, fix/fixes/fixed, resolve/resolves/resolved + #N or GH-N
    private static readonly Regex GitHubClosingKeywordPattern = new(
        @"(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+(?:#|GH-)(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // GitLab closing keywords: Closes/Fixes/Resolves + #N only (base forms)
    private static readonly Regex GitLabClosingKeywordPattern = new(
        @"(?:Closes|Fixes|Resolves)\s+#(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Cross-repo references: owner/repo#N
    private static readonly Regex CrossRepoPattern = new(
        @"[\w\-\.]+/[\w\-\.]+#(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // GH-N references
    private static readonly Regex GhPattern = new(
        @"\bGH-(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Simple #N references (not preceded by &, word chars, or /)
    private static readonly Regex SimpleHashPattern = new(
        @"(?<![&\w/])#(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses text for base closing keyword patterns (Closes/Fixes/Resolves #N) and adds
    /// matched issue numbers to the provided set. Used by GitLab provider.
    /// </summary>
    public static void ParseClosingKeywords(string? text, HashSet<string> issueNumbers)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (Match match in GitLabClosingKeywordPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }
    }

    /// <summary>
    /// Parses text for all GitHub issue reference patterns and adds found issue numbers to the set.
    /// Recognizes: #N, owner/repo#N, GH-N, closes/fixes/resolves #N (all verb forms, case-insensitive).
    /// </summary>
    public static void ParseIssueReferences(string? text, HashSet<string> issueNumbers)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (Match match in GitHubClosingKeywordPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }

        foreach (Match match in CrossRepoPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }

        foreach (Match match in GhPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }

        foreach (Match match in SimpleHashPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }
    }
}
