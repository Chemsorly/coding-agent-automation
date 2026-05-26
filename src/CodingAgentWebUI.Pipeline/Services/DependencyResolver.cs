using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Resolves title-based dependency references to GitHub issue number format.
/// Maintains a title→number mapping built during sequential issue creation.
/// NOT thread-safe — designed for sequential use within a single step.
/// When duplicate normalized titles are registered, the first registration wins.
/// </summary>
public sealed class DependencyResolver
{
    private readonly Dictionary<string, string> _titleToNumber = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a created issue's title→number mapping.
    /// If a title with the same normalized form is already registered, the registration is ignored
    /// (first-created wins).
    /// </summary>
    /// <param name="title">The issue title to register.</param>
    /// <param name="issueNumber">The issue number (e.g., "42").</param>
    public void Register(string title, string issueNumber)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(issueNumber);

        var normalized = title.Trim();

        // First-registered title wins — do not overwrite existing entries.
        _titleToNumber.TryAdd(normalized, issueNumber);
    }

    /// <summary>
    /// Resolves dependency titles to "Depends on #N" lines.
    /// Uses case-insensitive, whitespace-trimmed matching.
    /// Unresolved titles are logged and omitted.
    /// </summary>
    /// <param name="dependencyTitles">The list of dependency titles to resolve.</param>
    /// <param name="logger">Logger for warning about unresolved dependencies.</param>
    /// <returns>A list of "Depends on #N" lines for resolved dependencies.</returns>
    public IReadOnlyList<string> Resolve(IReadOnlyList<string> dependencyTitles, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dependencyTitles);
        ArgumentNullException.ThrowIfNull(logger);

        if (dependencyTitles.Count == 0)
            return [];

        var results = new List<string>();

        foreach (var depTitle in dependencyTitles)
        {
            if (string.IsNullOrWhiteSpace(depTitle))
                continue;

            var normalized = depTitle.Trim();

            if (_titleToNumber.TryGetValue(normalized, out var issueNumber))
            {
                results.Add($"Depends on #{issueNumber}");
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
