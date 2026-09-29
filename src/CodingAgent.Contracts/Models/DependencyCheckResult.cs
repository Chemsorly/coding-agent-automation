namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Result of checking whether an issue's dependencies are all satisfied.
/// </summary>
public sealed record DependencyCheckResult
{
    /// <summary>True if all dependencies are satisfied (or no dependencies exist).</summary>
    public required bool IsReady { get; init; }

    /// <summary>Issue numbers that are still open (blocking dispatch).</summary>
    public required IReadOnlyList<int> BlockedBy { get; init; }

    /// <summary>
    /// Full issue URLs that are still open or unresolvable (blocking dispatch).
    /// Populated when cross-tracker dependencies are found via <see cref="IDependencyChecker"/>
    /// URL routing. Empty for same-tracker numeric dependencies.
    /// </summary>
    public IReadOnlyList<string> BlockedByUrls { get; init; } = Array.Empty<string>();

    /// <summary>Total number of dependency references found in the issue body.</summary>
    public required int TotalDependencies { get; init; }

    /// <summary>Convenience factory for issues with no dependencies.</summary>
    public static DependencyCheckResult NoDependencies { get; } = new()
    {
        IsReady = true,
        BlockedBy = Array.Empty<int>(),
        BlockedByUrls = Array.Empty<string>(),
        TotalDependencies = 0
    };
}
