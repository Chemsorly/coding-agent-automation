using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>What a refactoring scan produced, for the run page.</summary>
[MessagePackObject]
public sealed record RefactoringScanOutcome
{
    /// <summary>The three Phase 1 detectors, in the order structural, correctness, design. Empty when Phase 1 did not run.</summary>
    [Key(0)] public IReadOnlyList<RefactoringDetectorOutcome> Detectors { get; init; } = [];
    /// <summary>Proposals after the adversarial review, before validation.</summary>
    [Key(1)] public int ProposalCount { get; init; }
    [Key(2)] public IReadOnlyList<CreatedIssueInfo> CreatedIssues { get; init; } = [];
    /// <summary>Proposals the deterministic validation dropped, with the reason.</summary>
    [Key(3)] public IReadOnlyList<RefactoringProposalNote> DroppedProposals { get; init; } = [];
    /// <summary>Proposals whose issue could not be created.</summary>
    [Key(4)] public IReadOnlyList<RefactoringProposalNote> UnfiledProposals { get; init; } = [];
    /// <summary>The commit the scan analyzed, or null when it could not be read.</summary>
    [Key(5)] public string? AnalyzedCommitSha { get; init; }
    [Key(6)] public int MaxIssues { get; init; }
    [Key(7)] public int HotspotLookbackDays { get; init; }
    [Key(8)] public bool ReviewEnabled { get; init; }
    [Key(9)] public bool AutoDispatch { get; init; }
}

/// <summary>How one Phase 1 detector of a refactoring scan did.</summary>
[MessagePackObject]
public sealed record RefactoringDetectorOutcome
{
    /// <summary>"Structural debt", "Correctness and hygiene" or "Design consistency".</summary>
    [Key(0)] public string Name { get; init; } = "";
    [Key(1)] public bool Succeeded { get; init; }
    /// <summary>Number of findings in the detector's findings file, or null when the file is missing or not valid JSON.</summary>
    [Key(2)] public int? FindingCount { get; init; }
}

/// <summary>A proposal that was not filed: dropped by validation (with a reason) or failed to be created.</summary>
[MessagePackObject]
public sealed record RefactoringProposalNote
{
    [Key(0)] public string Title { get; init; } = "";
    [Key(1)] public string? Category { get; init; }
    /// <summary>Why validation dropped the proposal; null for a proposal whose creation failed.</summary>
    [Key(2)] public string? Reason { get; init; }
}
