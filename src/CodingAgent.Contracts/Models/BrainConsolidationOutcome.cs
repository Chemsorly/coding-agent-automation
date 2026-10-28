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
    /// <summary>Web link of the pushed commit (absolute https only), or null when the provider cannot build one.</summary>
    [Key(5)] public string? PushedCommitUrl { get; init; }
    /// <summary>1 when the push went straight through; 2 or more when it was merged onto newer brain commits first.</summary>
    [Key(6)] public int PushAttempts { get; init; }
    /// <summary>The brain's base branch the commit was pushed to.</summary>
    [Key(7)] public string? BaseBranch { get; init; }

    // ── Brain v2 fields (spec 054, req 3.5, task 3.5) — additive from Key(8) ──

    /// <summary>Wire-compat: additive.</summary>
    [Key(8)] public int Promoted { get; init; }
    /// <summary>Wire-compat: additive.</summary>
    [Key(9)] public int Appended { get; init; }
    /// <summary>Wire-compat: additive.</summary>
    [Key(10)] public int Retired { get; init; }
    /// <summary>Wire-compat: additive.</summary>
    [Key(11)] public int MissedAgain { get; init; }
    /// <summary>Lines promoted, appended, retired or missed-again; capped so the serialized outcome is ≤ 64 KB. Wire-compat: additive.</summary>
    [Key(12)] public IReadOnlyList<BrainReviewLine> Lines { get; init; } = [];
    /// <summary>True when Lines was truncated to fit 64 KB. Wire-compat: additive.</summary>
    [Key(13)] public bool LinesTruncated { get; init; }
    /// <summary>Checks: patterns that recur but have not been promoted (mechanical fixes). Wire-compat: additive.</summary>
    [Key(14)] public IReadOnlyList<BrainReviewCheck> Checks { get; init; } = [];
    /// <summary>Identified flaky tests with the repositories that encountered them. Wire-compat: additive.</summary>
    [Key(15)] public IReadOnlyList<BrainFlakyTest> FlakyTests { get; init; } = [];
    /// <summary>Wire-compat: additive.</summary>
    [Key(16)] public int InfrastructureFailures { get; init; }
    /// <summary>Wire-compat: additive.</summary>
    [Key(17)] public int CiStatusErrors { get; init; }
    /// <summary>Wire-compat: additive.</summary>
    [Key(18)] public int Timeouts { get; init; }
    /// <summary>True when the v1 archive move was performed. Wire-compat: additive.</summary>
    [Key(19)] public bool ArchivedV1 { get; init; }
    /// <summary>Wire-compat: additive.</summary>
    [Key(20)] public int InboxFilesRead { get; init; }
    /// <summary>Wire-compat: additive.</summary>
    [Key(21)] public int InboxFilesMalformed { get; init; }
    /// <summary>Human commits seen and tagged user. Wire-compat: additive.</summary>
    [Key(22)] public int HumanCommits { get; init; }
    /// <summary>Inbox files remaining after reading 100 (D25). Wire-compat: additive.</summary>
    [Key(23)] public int InboxFilesLeft { get; init; }
}

/// <summary>A single line entry in the brain review outcome (spec 054, task 3.5).</summary>
[MessagePackObject]
public sealed record BrainReviewLine
{
    [Key(0)] public string Action { get; init; } = "";  // promoted | appended | retired | missed_again
    [Key(1)] public string Scope { get; init; } = "";
    [Key(2)] public string File { get; init; } = "";
    [Key(3)] public string Text { get; init; } = "";
}

/// <summary>A mechanical-fix check from the brain review (spec 054, task 3.5).</summary>
[MessagePackObject]
public sealed record BrainReviewCheck
{
    [Key(0)] public string Cause { get; init; } = "";
    [Key(1)] public IReadOnlyList<string> Repositories { get; init; } = [];
    [Key(2)] public string SuggestedHome { get; init; } = "";  // quality_gate | setup_step | steering
}

/// <summary>A flaky test identified during a brain review (spec 054, task 3.5).</summary>
[MessagePackObject]
public sealed record BrainFlakyTest
{
    [Key(0)] public string Name { get; init; } = "";
    [Key(1)] public IReadOnlyList<string> Repositories { get; init; } = [];
    [Key(2)] public int Count { get; init; }
}
