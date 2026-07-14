using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline;

/// <summary>
/// Utility for computing and extracting body hashes embedded in analysis comments.
/// Used by staleness detection to determine if an issue's description has changed
/// since the analysis was produced.
/// </summary>
public static partial class AnalysisBodyHash
{
    /// <summary>
    /// Computes a 12-character lowercase hex hash of the given body text.
    /// Input is normalized (trimmed) before hashing. Null is treated as empty string.
    /// </summary>
    /// <param name="body">The issue body text to hash.</param>
    /// <returns>A deterministic 12-character lowercase hex string.</returns>
    public static string Compute(string? body)
    {
        var normalized = (body ?? "").Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    /// <summary>
    /// Extracts the body hash from an analysis comment's HTML marker.
    /// Returns null if the marker is not present (e.g., legacy comments).
    /// </summary>
    /// <param name="commentBody">The full body of the analysis comment.</param>
    /// <returns>The 12-character hex hash, or null if no marker found.</returns>
    public static string? Extract(string commentBody)
    {
        var match = HashPattern().Match(commentBody);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"<!-- agent:analysis-body-hash:([a-f0-9]{12}) -->", RegexOptions.Compiled)]
    private static partial Regex HashPattern();
}
