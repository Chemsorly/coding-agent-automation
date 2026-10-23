using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Agent → Orchestrator: Completion payload for a consolidation run.
/// Reported back via SignalR when the consolidation job finishes.
/// </summary>
[MessagePackObject]
public sealed class ConsolidationJobResult
{
    [Key(0)]
    public required string JobId { get; init; }

    [Key(1)]
    public required bool Success { get; init; }

    [Key(2)]
    public string? Summary { get; init; }

    [Key(3)]
    public string? ErrorMessage { get; init; }

    /// <summary>For refactoring: the issues created from parsed proposals.</summary>
    [Key(4)]
    public IReadOnlyList<CreatedIssueInfo>? CreatedIssues { get; init; }

    // Key(5) is retired (was HarnessSuggestions). Do not reuse.

    /// <summary>Token usage from the discriminator review agent call.</summary>
    [Key(6)]
    public TokenUsage? ReviewTokenUsage { get; init; }

    /// <summary>Token usage from the refinement agent call.</summary>
    [Key(7)]
    public TokenUsage? RefinementTokenUsage { get; init; }

    /// <summary>Token usage from the brain consolidation diff summary call (brain phase only).</summary>
    [Key(8)]
    public TokenUsage? DiffSummaryTokenUsage { get; init; }

    /// <summary>
    /// Typed failure category. Set to <see cref="FailureReason.ExitCodeFailure"/> when
    /// the agent process exits with a non-zero code. Null for success or untyped failures.
    /// </summary>
    [Key(9)]
    public FailureReason? FailureCategory { get; init; }

    /// <summary>Tokens of all agent calls of the run (input, output and reasoning). 0 from agents older than this field.</summary>
    [Key(10)]
    public long TotalTokens { get; set; }

    /// <summary>Cost in USD of the run's agent calls, or null when the provider reports no cost.</summary>
    [Key(11)]
    public decimal? TotalCost { get; set; }

    /// <summary>Tokens read from the prompt cache.</summary>
    [Key(12)]
    public long CacheReadTokens { get; set; }

    /// <summary>Tokens written to the prompt cache.</summary>
    [Key(13)]
    public long CacheWriteTokens { get; set; }

    /// <summary>For a refactoring scan: what it produced. Null for the other kinds.</summary>
    [Key(14)]
    public RefactoringScanOutcome? ScanOutcome { get; set; }

    /// <summary>For a brain consolidation that pushed its changes: what it changed and pushed. Null otherwise.</summary>
    [Key(15)]
    public BrainConsolidationOutcome? BrainOutcome { get; set; }

    /// <summary>
    /// The agent-side per-phase usage. In-process only: <c>WorkItemExecutorRouter</c> copies it into the HTTP
    /// completion's <see cref="JobCompletionPayload.PhaseBreakdown"/>; it is not part of the hub message.
    /// </summary>
    [IgnoreMember]
    public IReadOnlyDictionary<string, PhaseUsagePayload>? PhaseBreakdown { get; set; }
}

/// <summary>
/// Information about a GitHub issue created during a consolidation run (e.g., refactoring proposals).
/// Distinct from <see cref="CreatedIssueResult"/> which is the return type of <c>IIssueProvider.CreateIssueAsync</c>.
/// </summary>
[MessagePackObject]
public sealed record CreatedIssueInfo
{
    [Key(0)]
    public required string Identifier { get; init; }

    [Key(1)]
    public required string Title { get; init; }

    [Key(2)]
    public required string Url { get; init; }

    [Key(3)]
    public string? Category { get; init; }
}
