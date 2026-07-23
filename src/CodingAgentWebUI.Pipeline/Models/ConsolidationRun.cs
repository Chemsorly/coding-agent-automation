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

    public required DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
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
    /// Required agent labels resolved at enqueue time. Persisted to enable restart rehydration
    /// of queued runs without re-resolving provider configs.
    /// </summary>
    public IReadOnlyList<string>? QueuedRequiredLabels { get; set; }

    /// <summary>
    /// Project display name for the owning project (resolved from template → project at trigger time).
    /// Null for global consolidation runs (no owning project).
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// When true, created refactoring issues will receive both <c>agent:generated</c> and
    /// <c>agent:next</c> labels, immediately dispatching them for agent execution.
    /// Defaults to <c>false</c> for backward compatibility with old persisted runs.
    /// </summary>
    public bool AutoDispatch { get; init; }
}
