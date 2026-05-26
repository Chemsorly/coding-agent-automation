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
/// Result of a consolidation dispatch attempt.
/// </summary>
public enum ConsolidationDispatchResult
{
    /// <summary>Job was dispatched to an idle agent immediately.</summary>
    Dispatched,
    /// <summary>No idle agent available; job was enqueued for later dispatch.</summary>
    Queued,
    /// <summary>Dispatch failed (e.g., token vending error).</summary>
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
    /// Required agent labels persisted when the run is queued, so label-based routing
    /// survives application restart during rehydration.
    /// </summary>
    public IReadOnlyList<string>? QueuedRequiredLabels { get; set; }
}
