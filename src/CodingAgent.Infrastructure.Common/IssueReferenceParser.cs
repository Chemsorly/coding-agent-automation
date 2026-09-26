using System.Text.RegularExpressions;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Extracts issue references from PR/MR title and description text.
/// Provides four parsing modes:
/// <list type="bullet">
///   <item><see cref="ParseClosingKeywords"/> — base closing keywords (Closes/Fixes/Resolves #N, GitLab-compatible base forms)</item>
///   <item><see cref="ParseAllClosingKeywords"/> — all closing keyword forms from both GitLab and GitHub (base forms + closed/fixed/resolved/close/fix/resolve + GH-N in keyword context)</item>
///   <item><see cref="ParseIssueReferences"/> — all GitHub patterns (closing keywords with all verb forms, GH-N, cross-repo, simple #N)</item>
///   <item><see cref="ParseIssueUrls"/> — full GitHub issue URLs (https://github.com/owner/repo/issues/N)</item>
/// </list>
/// </summary>
public static class IssueReferenceParser
{
    // GitHub closing keywords: close/closes/closed, fix/fixes/fixed, resolve/resolves/resolved + #N or GH-N
    private static readonly Regex GitHubClosingKeywordPattern = new(
        @"(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+(?:#|GH-)(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeout: TimeSpan.FromSeconds(2));

    // GitLab closing keywords: Closes/Fixes/Resolves + #N only (base forms)
    private static readonly Regex GitLabClosingKeywordPattern = new(
        @"(?:Closes|Fixes|Resolves)\s+#(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeout: TimeSpan.FromSeconds(2));

    // Cross-repo references: owner/repo#N
    private static readonly Regex CrossRepoPattern = new(
        @"[\w\-\.]+/[\w\-\.]+#(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeout: TimeSpan.FromSeconds(2));

    // GH-N references
    private static readonly Regex GhPattern = new(
        @"\bGH-(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeout: TimeSpan.FromSeconds(2));

    // Simple #N references (not preceded by &, word chars, or /)
    private static readonly Regex SimpleHashPattern = new(
        @"(?<![&\w/])#(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeout: TimeSpan.FromSeconds(2));

    // Full GitHub issue URLs: https://github.com/owner/repo/issues/N
    // Deliberately does NOT match /pull/N, /commit/, /tree/, /blob/, or raw.githubusercontent.com
    private static readonly Regex GitHubIssueUrlPattern = new(
        @"https?://(?:www\.)?github\.com/[\w\-\.]+/[\w\-\.]+/issues/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeout: TimeSpan.FromSeconds(2));

    /// <summary>
    /// Parses text for base closing keyword patterns (Closes/Fixes/Resolves #N) and adds
    /// matched issue numbers to the provided set. Used by GitLab provider.
    /// </summary>
    public static void ParseClosingKeywords(string? text, HashSet<string> issueNumbers)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            foreach (Match match in GitLabClosingKeywordPattern.Matches(text))
            {
                issueNumbers.Add(match.Groups[1].Value);
            }
        }
        catch (RegexMatchTimeoutException) { /* adversarial input — return partial results */ }
    }

    /// <summary>
    /// Parses text for all closing keyword forms from both GitLab and GitHub patterns and adds
    /// matched issue numbers to the provided set.
    /// <para>
    /// Covers: <c>Closes/Fixes/Resolves #N</c> (GitLab base forms) and all GitHub verb forms
    /// (<c>close/closes/closed</c>, <c>fix/fixes/fixed</c>, <c>resolve/resolves/resolved</c>)
    /// with <c>#N</c> or <c>GH-N</c> in the keyword context.
    /// </para>
    /// <para>
    /// Does NOT match standalone <c>GH-N</c>, cross-repo references (<c>owner/repo#N</c>),
    /// or plain <c>#N</c> mentions — use <see cref="ParseIssueReferences"/> for those.
    /// </para>
    /// </summary>
    public static void ParseAllClosingKeywords(string? text, HashSet<string> issueNumbers)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            foreach (Match match in GitLabClosingKeywordPattern.Matches(text))
                issueNumbers.Add(match.Groups[1].Value);
        }
        catch (RegexMatchTimeoutException) { /* adversarial input — return partial results */ }

        try
        {
            foreach (Match match in GitHubClosingKeywordPattern.Matches(text))
                issueNumbers.Add(match.Groups[1].Value);
        }
        catch (RegexMatchTimeoutException) { /* adversarial input — return partial results */ }
    }

    /// <summary>
    /// Parses text for all GitHub issue reference patterns and adds found issue numbers to the set.
    /// Recognizes: #N, owner/repo#N, GH-N, closes/fixes/resolves #N (all verb forms, case-insensitive).
    /// </summary>
    public static void ParseIssueReferences(string? text, HashSet<string> issueNumbers)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            foreach (Match match in GitHubClosingKeywordPattern.Matches(text))
                issueNumbers.Add(match.Groups[1].Value);
        }
        catch (RegexMatchTimeoutException) { /* regex timeout — skip this pattern */ }

        try
        {
            foreach (Match match in CrossRepoPattern.Matches(text))
                issueNumbers.Add(match.Groups[1].Value);
        }
        catch (RegexMatchTimeoutException) { /* regex timeout — skip this pattern */ }

        try
        {
            foreach (Match match in GhPattern.Matches(text))
                issueNumbers.Add(match.Groups[1].Value);
        }
        catch (RegexMatchTimeoutException) { /* regex timeout — skip this pattern */ }

        try
        {
            foreach (Match match in SimpleHashPattern.Matches(text))
                issueNumbers.Add(match.Groups[1].Value);
        }
        catch (RegexMatchTimeoutException) { /* regex timeout — skip this pattern */ }
    }

    /// <summary>
    /// Parses text for GitHub issue URL references and adds matched issue numbers to the set.
    /// Recognizes: <c>https://github.com/owner/repo/issues/N</c> (HTTP and HTTPS, optional <c>www.</c> prefix).
    /// <para>
    /// Does NOT match pull request URLs (<c>/pull/N</c>), commit URLs (<c>/commit/</c>),
    /// tree/blob links, or <c>raw.githubusercontent.com</c> URLs.
    /// </para>
    /// </summary>
    public static void ParseIssueUrls(string? text, HashSet<string> issueNumbers)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            foreach (Match match in GitHubIssueUrlPattern.Matches(text))
                issueNumbers.Add(match.Groups[1].Value);
        }
        catch (RegexMatchTimeoutException) { /* adversarial input — return partial results */ }
    }
}
