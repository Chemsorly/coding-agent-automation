namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Prefix-based path matching for blacklisted paths.
/// Paths are normalized to forward slashes and compared case-insensitively.
/// </summary>
public static class BlacklistMatcher
{
    /// <summary>
    /// Returns true if the given relative path matches any blacklisted prefix.
    /// A prefix like ".github" matches ".github/workflows/ci.yml" but not ".githubx".
    /// </summary>
    public static bool IsBlacklisted(string relativePath, IReadOnlyList<string> blacklistedPrefixes)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(blacklistedPrefixes);

        if (blacklistedPrefixes.Count == 0)
            return false;

        var normalized = NormalizePath(relativePath);

        foreach (var prefix in blacklistedPrefixes)
        {
            var normalizedPrefix = prefix.Replace('\\', '/').TrimEnd('/');
            if (normalized.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(normalizedPrefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Filters a list of paths, returning only those that match the blacklist.
    /// </summary>
    public static IReadOnlyList<string> FindBlacklistedPaths(
        IEnumerable<string> paths, IReadOnlyList<string> blacklistedPrefixes)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(blacklistedPrefixes);

        if (blacklistedPrefixes.Count == 0)
            return Array.Empty<string>();

        return paths.Where(p => IsBlacklisted(p, blacklistedPrefixes)).ToList();
    }

    /// <summary>
    /// Normalizes a path by replacing backslashes with forward slashes
    /// and collapsing any ".." segments to prevent traversal bypasses.
    /// </summary>
    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');

        // Collapse "." and ".." segments to prevent traversal bypasses
        if (normalized.Contains("..") || normalized.Contains("./"))
        {
            var segments = normalized.Split('/');
            var stack = new Stack<string>();
            foreach (var segment in segments)
            {
                if (segment == ".." && stack.Count > 0 && stack.Peek() != "..")
                    stack.Pop();
                else if (segment is not ("." or ""))
                    stack.Push(segment);
            }
            normalized = string.Join("/", stack.Reverse());
        }

        return normalized;
    }
}
