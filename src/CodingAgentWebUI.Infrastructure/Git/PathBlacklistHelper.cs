namespace CodingAgentWebUI.Infrastructure.Git;

/// <summary>
/// Utility for checking whether file paths match blacklisted prefixes.
/// Used by <see cref="RepositoryGitOperations"/> to unstage files matching
/// configured blacklist patterns during commit preparation.
/// </summary>
internal static class PathBlacklistHelper
{
    /// <summary>
    /// Checks whether a file path matches any of the blacklisted path prefixes.
    /// Matching is prefix-based, case-insensitive, and normalizes backslashes to forward slashes.
    /// </summary>
    public static bool IsPathBlacklisted(string filePath, IReadOnlyList<string> blacklistedPrefixes)
    {
        if (blacklistedPrefixes.Count == 0) return false;
        var normalized = filePath.Replace('\\', '/');
        foreach (var prefix in blacklistedPrefixes)
        {
            var normalizedPrefix = prefix.Replace('\\', '/').TrimEnd('/');
            if (normalized.StartsWith(normalizedPrefix + "/", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
