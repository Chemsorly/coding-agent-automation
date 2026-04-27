namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Result of merging the base branch into the current branch.
/// </summary>
public sealed class MergeResult
{
    public required bool Success { get; init; }
    public required bool HasConflicts { get; init; }
    public IReadOnlyList<string> ConflictFiles { get; init; } = Array.Empty<string>();
}
