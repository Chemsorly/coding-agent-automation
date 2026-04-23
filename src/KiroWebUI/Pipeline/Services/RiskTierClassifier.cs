using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Classifies code review risk tier based on diff size and security-sensitive paths.
/// </summary>
public static class RiskTierClassifier
{
    public const string Skip = "skip";
    public const string Standard = "standard";
    public const string Full = "full";

    /// <summary>
    /// Determines the code review tier for a set of changes.
    /// Returns "full" if any changed file matches a security path,
    /// "skip" if the diff is below the skip threshold, or "standard" otherwise.
    /// When <paramref name="riskTiers"/> is null, returns "standard" (current behavior).
    /// </summary>
    public static string Classify(
        CodeReviewRiskTiers? riskTiers,
        int fileCount,
        int lineCount,
        IReadOnlyList<string> changedFilePaths)
    {
        if (riskTiers is null) return Standard;

        // Security paths always force full review
        if (riskTiers.SecurityPaths is { Count: > 0 })
        {
            foreach (var filePath in changedFilePaths)
            {
                if (MatchesSecurityPath(filePath, riskTiers.SecurityPaths))
                    return Full;
            }
        }

        // Check skip threshold
        if (riskTiers.Skip is { } skip
            && fileCount <= skip.MaxFiles
            && lineCount <= skip.MaxLines)
        {
            return Skip;
        }

        return Standard;
    }

    /// <summary>
    /// Returns the security paths matched by the given changed files, for logging.
    /// </summary>
    public static IReadOnlyList<string> GetMatchedSecurityPaths(
        IReadOnlyList<string>? securityPaths,
        IReadOnlyList<string> changedFilePaths)
    {
        if (securityPaths is not { Count: > 0 }) return Array.Empty<string>();

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in changedFilePaths)
        {
            var normalized = filePath.Replace('\\', '/');
            foreach (var secPath in securityPaths)
            {
                if (normalized.Contains(secPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    matched.Add(secPath);
            }
        }
        return matched.ToArray();
    }

    private static bool MatchesSecurityPath(string filePath, IReadOnlyList<string> securityPaths)
    {
        var normalized = filePath.Replace('\\', '/');
        foreach (var secPath in securityPaths)
        {
            if (normalized.Contains(secPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
