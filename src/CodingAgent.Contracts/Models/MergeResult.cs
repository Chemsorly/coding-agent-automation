namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Result of merging the base branch into the current branch.
/// </summary>
public sealed class MergeResult
{
    public required bool Success { get; init; }
    public required bool HasConflicts { get; init; }
    public IReadOnlyList<string> ConflictFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When true, conflicts were force-resolved by keeping the base (main) version: main is
    /// authoritative. The branch's conflicting changes to these files were dropped, and so was a
    /// branch rename of a file main changed (both paths are listed). The agent must re-apply what
    /// the issue still needs on top of the current main state.
    /// </summary>
    public bool ForceResolved { get; init; }

    /// <summary>Merge base of the branch and the base branch before the rebase, when there was one.</summary>
    public string? MergeBaseSha { get; init; }

    /// <summary>Branch head before the rebase, so the dropped changes stay reachable for <c>git diff</c>.</summary>
    public string? PreviousHeadSha { get; init; }

    /// <summary>Base branch head the branch was rebased onto.</summary>
    public string? BaseHeadSha { get; init; }

    /// <summary>
    /// For each force-resolved file: the branch's dropped change and main's change since the
    /// merge base. Empty when nothing was force-resolved or there was no merge base.
    /// </summary>
    public IReadOnlyList<ForceResolvedFileContext> ForceResolvedContext { get; init; } = Array.Empty<ForceResolvedFileContext>();
}

/// <summary>
/// What a force-resolved rebase dropped from one file, so a rework agent can decide what to
/// re-apply on top of main.
/// </summary>
public sealed record ForceResolvedFileContext
{
    /// <summary>Repository-relative path of the file.</summary>
    public required string Path { get; init; }

    /// <summary>The branch's whole change to the file since the merge base (dropped), as a unified diff.</summary>
    public required string BranchChange { get; init; }

    /// <summary>Main's change to the file since the merge base (kept), as a unified diff.</summary>
    public required string BaseChange { get; init; }

    /// <summary>Main's commits that touched the file since the merge base, newest first, as "sha subject".</summary>
    public IReadOnlyList<string> BaseCommits { get; init; } = Array.Empty<string>();
}
