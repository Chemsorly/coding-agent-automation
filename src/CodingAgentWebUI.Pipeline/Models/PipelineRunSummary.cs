namespace CodingAgentWebUI.Pipeline.Models;

public sealed class PipelineRunSummary
{
    public required string RunId { get; init; }
    public required string IssueIdentifier { get; init; }
    public required string IssueTitle { get; init; }
    public required PipelineStep FinalStep { get; init; }

    [Obsolete("Use StartedAtOffset for timezone-safe comparisons")]
    public DateTime StartedAt { get; init; }

    [Obsolete("Use CompletedAtOffset for timezone-safe comparisons")]
    public DateTime? CompletedAt { get; init; }

    /// <summary>Timezone-safe shadow of <see cref="StartedAt"/>. Set alongside the original property.</summary>
    public DateTimeOffset StartedAtOffset { get; init; }

    /// <summary>Timezone-safe shadow of <see cref="CompletedAt"/>. Set alongside the original property.</summary>
    public DateTimeOffset? CompletedAtOffset { get; init; }

    public int RetryCount { get; init; }
    public string? PullRequestUrl { get; init; }

    /// <summary>Discriminates implementation vs review runs.</summary>
    public PipelineRunType RunType { get; init; } = PipelineRunType.Implementation;

    /// <summary>PR URL for review runs (the PR being reviewed).</summary>
    public string? ReviewPrUrl { get; init; }

    /// <summary>Review agents that ran during this review run.</summary>
    public IReadOnlyList<string> CodeReviewAgentsRun { get; init; } = [];

    /// <summary>Critical finding count from code review.</summary>
    public int CodeReviewCriticalCount { get; init; }

    /// <summary>Warning finding count from code review.</summary>
    public int CodeReviewWarningCount { get; init; }

    /// <summary>Suggestion finding count from code review.</summary>
    public int CodeReviewSuggestionCount { get; init; }

    /// <summary>Model configured for the agent provider used in this run.</summary>
    public string? ModelName { get; init; }

    /// <summary>Whether a brain repository was used for this run.</summary>
    public bool BrainRepoUsed { get; init; }

    /// <summary>Whether brain updates were pushed successfully.</summary>
    public bool BrainUpdatesPushed { get; init; }

    /// <summary>Which agent executed this run, or null for local runs.</summary>
    public string? AgentId { get; init; }

    /// <summary>How this run was initiated: "manual" or "loop".</summary>
    public string InitiatedBy { get; init; } = "manual";

    /// <summary>Analysis gate recommendation, or null if no assessment was produced.</summary>
    public AnalysisGateResult? AnalysisRecommendation { get; init; }

    /// <summary>Whether this run was a rework of an existing PR.</summary>
    public bool IsRework { get; init; }

    /// <summary>Why the run failed, or null if it did not fail.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Structured feedback collected from the agent after this run.</summary>
    public RunFeedback? Feedback { get; init; }

    /// <summary>Accumulated total tokens across all agent invocations.</summary>
    public long TotalTokens { get; init; }

    /// <summary>Accumulated total cost (USD), or null if no cost data available.</summary>
    public decimal? TotalCost { get; init; }

    /// <summary>Number of sub-issues successfully created during the Decomposition phase.</summary>
    public int DecompositionSubIssuesCreated { get; init; }

    /// <summary>Total number of sub-issues attempted during the Decomposition phase.</summary>
    public int DecompositionSubIssuesAttempted { get; init; }

    /// <summary>Project display name for run history table column.</summary>
    public string? ProjectName { get; init; }

    /// <summary>
    /// For decomposition runs: whether the epic was polled from the project-level
    /// EpicIssueProviderId ("project-level") or the template's own IssueProviderId ("template-level").
    /// Null for non-decomposition runs.
    /// </summary>
    public string? DecompositionSource { get; init; }
}
