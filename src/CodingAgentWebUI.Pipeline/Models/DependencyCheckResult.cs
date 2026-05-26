namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Result of checking whether an issue's dependencies are all satisfied.
/// </summary>
public sealed record DependencyCheckResult
{
    /// <summary>True if all dependencies are satisfied (or no dependencies exist).</summary>
    public required bool IsReady { get; init; }

    /// <summary>Issue numbers that are still open (blocking dispatch).</summary>
    public required IReadOnlyList<int> BlockedBy { get; init; }

    /// <summary>Total number of dependency references found in the issue body.</summary>
    public required int TotalDependencies { get; init; }

    /// <summary>Convenience factory for issues with no dependencies.</summary>
    public static DependencyCheckResult NoDependencies { get; } = new()
    {
        IsReady = true,
        BlockedBy = Array.Empty<int>(),
        TotalDependencies = 0
    };
}
