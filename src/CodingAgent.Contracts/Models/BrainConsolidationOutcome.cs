using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>What a brain consolidation changed and pushed, for the run page.</summary>
[MessagePackObject]
public sealed record BrainConsolidationOutcome
{
    [Key(0)] public int FilesModified { get; init; }
    [Key(1)] public int EntriesMerged { get; init; }
    [Key(2)] public int ContradictionsResolved { get; init; }
    [Key(3)] public int EntriesPruned { get; init; }
    /// <summary>The commit pushed to the brain's base branch, or null when it could not be read.</summary>
    [Key(4)] public string? PushedCommitSha { get; init; }
    /// <summary>Web link of the pushed commit, or null when the provider cannot build one.</summary>
    [Key(5)] public string? PushedCommitUrl { get; init; }
    /// <summary>1 when the push went straight through; 2 or more when it was merged onto newer brain commits first.</summary>
    [Key(6)] public int PushAttempts { get; init; }
    /// <summary>The brain's base branch the commit was pushed to.</summary>
    [Key(7)] public string? BaseBranch { get; init; }
}
