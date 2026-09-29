using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Resolves title-based dependency references to issue dependency lines.
/// Maintains a title → (number, url, providerId) mapping built during sequential issue creation.
/// NOT thread-safe — designed for sequential use within a single step.
/// When duplicate normalized titles are registered, the first registration wins.
/// </summary>
public sealed class DependencyResolver
{
    private readonly record struct Registration(string IssueNumber, string IssueUrl, string IssueProviderId);

    private readonly Dictionary<string, Registration> _titleToRegistration = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a created issue's title → (number, url, providerId) mapping.
    /// If a title with the same normalized form is already registered, the registration is ignored
    /// (first-created wins).
    /// </summary>
    /// <param name="title">The issue title to register.</param>
    /// <param name="issueNumber">The issue number (e.g., "42").</param>
    /// <param name="issueUrl">The full URL of the created issue (e.g., "https://github.com/acme/api/issues/42").</param>
    /// <param name="issueProviderId">The provider config ID that the issue was created in.</param>
    public void Register(string title, string issueNumber, string issueUrl, string issueProviderId)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(issueNumber);
        ArgumentNullException.ThrowIfNull(issueUrl);
        ArgumentNullException.ThrowIfNull(issueProviderId);

        var normalized = title.Trim();

        // First-registered title wins — do not overwrite existing entries.
        _titleToRegistration.TryAdd(normalized, new Registration(issueNumber, issueUrl, issueProviderId));
    }

    /// <summary>
    /// Resolves dependency titles to "Depends on" lines.
    /// When the dependency was created in the same tracker as <paramref name="targetProviderId"/>,
    /// emits <c>Depends on #N</c>. Otherwise emits <c>Depends on {url}</c> so the full cross-tracker
    /// reference is preserved in the sub-issue body.
    /// Uses case-insensitive, whitespace-trimmed matching.
    /// Unresolved titles are logged and omitted.
    /// </summary>
    /// <param name="dependencyTitles">The list of dependency titles to resolve.</param>
    /// <param name="targetProviderId">The provider config ID of the issue being created.</param>
    /// <param name="logger">Logger for warning about unresolved dependencies.</param>
    /// <returns>A list of "Depends on" lines for resolved dependencies.</returns>
    public IReadOnlyList<string> Resolve(IReadOnlyList<string> dependencyTitles, string targetProviderId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dependencyTitles);
        ArgumentNullException.ThrowIfNull(targetProviderId);
        ArgumentNullException.ThrowIfNull(logger);

        if (dependencyTitles.Count == 0)
            return [];

        var results = new List<string>();

        foreach (var depTitle in dependencyTitles)
        {
            if (string.IsNullOrWhiteSpace(depTitle))
                continue;

            var normalized = depTitle.Trim();

            if (_titleToRegistration.TryGetValue(normalized, out var reg))
            {
                // Same tracker → short #N form; different tracker → full URL
                var line = string.Equals(reg.IssueProviderId, targetProviderId, StringComparison.Ordinal)
                    ? $"Depends on #{reg.IssueNumber}"
                    : $"Depends on {reg.IssueUrl}";
                results.Add(line);
            }
            else
            {
                logger.Warning(
                    "Unresolved dependency title: {DependencyTitle}. No matching previously-created issue found; omitting.",
                    depTitle);
            }
        }

        return results;
    }
}
