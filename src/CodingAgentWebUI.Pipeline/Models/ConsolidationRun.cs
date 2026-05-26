namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// The type of consolidation loop being executed.
/// </summary>
public enum ConsolidationRunType
{
    BrainConsolidation,
    RefactoringDetection,
    HarnessSuggestions
}

/// <summary>
/// The execution status of a consolidation run.
/// </summary>
public enum ConsolidationRunStatus
{
    Running,
    Succeeded,
    Failed,
    Queued,
    Cancelled
}

/// <summary>
/// The result of a consolidation dispatch attempt.
/// </summary>
public enum ConsolidationDispatchResult
{
    Dispatched,
    Queued,
    Failed
}

/// <summary>
/// A single execution of a consolidation loop, tracking its type, timing, and outcome.
/// Persisted to config/pipeline/consolidation-runs/{RunId}.json.
/// </summary>
public sealed class ConsolidationRun
{
    public required string RunId { get; init; }
    public required ConsolidationRunType Type { get; init; }

    /// <summary>Null for harness suggestions (global scope).</summary>
    public string? TemplateId { get; init; }

    /// <summary>Template display name, or "Global" for harness suggestions.</summary>
    public string? TemplateName { get; init; }

    public required DateTime StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; set; }
    public ConsolidationRunStatus Status { get; set; }
    public string? Summary { get; set; }

    /// <summary>
    /// Total token count from review, refinement, and diff summary agent calls.
    /// Summed from <see cref="ConsolidationJobResult.ReviewTokenUsage"/>,
    /// <see cref="ConsolidationJobResult.RefinementTokenUsage"/>, and
    /// <see cref="ConsolidationJobResult.DiffSummaryTokenUsage"/>.
    /// </summary>
    public long TotalTokens { get; set; }

    /// <summary>
    /// Required agent labels persisted for restart rehydration of queued runs.
    /// </summary>
    public IReadOnlyList<string>? QueuedRequiredLabels { get; set; }
}

/// <summary>
/// Represents a consolidation job waiting in the queue for dispatch.
/// </summary>
public sealed record PendingConsolidationJob
{
    public required string RunId { get; init; }
    public required ConsolidationRunType Type { get; init; }
    public string? TemplateId { get; init; }
    public required string WorkspacePath { get; init; }
    public IReadOnlyList<string> RequiredLabels { get; init; } = [];
    public required DateTimeOffset EnqueuedAt { get; init; }
    public int RetryCount { get; set; }

    /// <summary>
    /// For HarnessSuggestions: the timestamp to use when regenerating feedback data at dispatch time.
    /// </summary>
    public DateTime? FeedbackSinceUtc { get; init; }
}
