using System.Text.RegularExpressions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Classifies the cause of an MCP or web tool call failure from the error text.
/// The classifier never returns <see cref="McpFailureCategory.Unknown"/>; only a provider that has
/// no error text (e.g. Kiro) uses that value directly.
/// </summary>
public static partial class McpFailureClassifier
{
    /// <summary>
    /// Classifies an MCP or web tool call failure based on the error text.
    /// Returns <see cref="McpFailureCategory.Other"/> for null, empty, or whitespace text.
    /// Returns <see cref="McpFailureCategory.Other"/> on <see cref="RegexMatchTimeoutException"/>.
    /// Auth is checked before Connection (first match wins).
    /// </summary>
    public static McpFailureCategory Classify(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
            return McpFailureCategory.Other;

        try
        {
            if (AuthRegex().IsMatch(errorText)) return McpFailureCategory.Auth;
            if (ConnectionRegex().IsMatch(errorText)) return McpFailureCategory.Connection;
        }
        catch (RegexMatchTimeoutException)
        {
            return McpFailureCategory.Other;
        }

        return McpFailureCategory.Other;
    }

    [GeneratedRegex(
        @"\b(401|403)\b|unauthori[sz]ed|forbidden|invalid[ _-]?token|token (has )?expired|authentication (failed|required)",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AuthRegex();

    [GeneratedRegex(
        @"timed? ?out|timeout|ECONNREFUSED|ECONNRESET|ENOTFOUND|EAI_AGAIN|ETIMEDOUT|socket hang up|connection (closed|refused|reset)|not connected|-3200[01]\b|\b(502|503|504)\b|bad gateway|service unavailable|gateway timeout",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConnectionRegex();
}
