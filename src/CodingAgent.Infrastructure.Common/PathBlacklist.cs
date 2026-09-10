namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Pure path-prefix blacklist matching, shared by the pipeline (PipelineFormatting) and the
/// git providers (RepositoryGitOperations unstaging). Lives in Infrastructure.Common so both
/// Pipeline and Infrastructure.Providers can use it without Providers referencing Pipeline
/// (Spec 048 Phase 1 kept Infrastructure.Providers Pipeline-free).
/// </summary>
public static class PathBlacklist
{
    /// <summary>
    /// Returns true when <paramref name="filePath"/> is under any of the blacklisted prefixes.
    /// Matching is case-insensitive, backslash/forward-slash agnostic, and prefix-boundary aware
    /// (a prefix matches a path segment boundary or the whole path, never a partial segment).
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
