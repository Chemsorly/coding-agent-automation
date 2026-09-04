namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Result of merging the base branch into the current branch.
/// </summary>
public sealed class MergeResult
{
    public required bool Success { get; init; }
    public required bool HasConflicts { get; init; }
    public IReadOnlyList<string> ConflictFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When true, conflicts were force-resolved by accepting the incoming (base/main) version.
    /// The branch's changes for these files were discarded. The agent must re-implement
    /// its changes on top of the current main state.
    /// </summary>
    public bool ForceResolved { get; init; }
}
