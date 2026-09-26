namespace CodingAgent.Pipeline.Models;

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
    /// <summary>
    /// The run has been accepted and is waiting for a free agent slot to be dispatched.
    /// This is the initial status of every newly triggered consolidation run.
    /// Runs in this state are eligible for restart rehydration by
    /// <c>ConsolidationRetryBackgroundService</c>.
    /// </summary>
    // TODO [WARNING]: This enum value was added as part of an issue whose stated scope was a
    // two-line test-attribute fix ([Collection("Metrics")]). Inserting Queued between Failed
    // (ordinal 2) and Cancelled (ordinal 3) is safe because the JSON serializer uses string
    // names (JsonStringEnumConverter), but the change is outside the scope of issue #3020 and
    // was not independently reviewed. Verify correctness in a standalone feature review.
    Queued,
    Cancelled,
    /// <summary>
    /// The work item has been successfully submitted to the unified dispatch queue as
    /// <c>Pending</c> (i.e. <see cref="DistributionResult.Queued"/> was true on success).
    /// The WorkItem already exists in the database — the Scheduler's
    /// <c>WorkItemDispatchLoop</c> will pick it up and create the K8s Job when
    /// capacity is available.
    /// <para>
    /// This state is intentionally excluded from
    /// <c>ConsolidationService.RehydrateQueuedRunsAsync</c> so the retry background
    /// service does not re-dispatch it (which would produce an idempotent 409 each cycle).
    /// The run will be transitioned to <c>Running</c> by the agent drain service when the
    /// K8s Job starts.
    /// </para>
    /// </summary>
    Pending
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
    // TODO [WARNING]: This property was added as part of an issue whose stated scope was a
    // two-line test-attribute fix ([Collection("Metrics")]). The change is functionally correct
    // (nullable, backward-compatible, used only in ConsolidationDispatcher.DispatchRunAsync)
    // but is outside the scope of issue #3020 and was not independently reviewed. Verify
    // correctness and migration story (existing persisted rows without this field) in a
    // standalone feature review.
    public IReadOnlyList<string>? QueuedRequiredLabels { get; set; }

    /// <summary>
    /// Project display name for the owning project (resolved from template → project at trigger time).
    /// Null for global consolidation runs (no owning project).
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// GUID of the owning PipelineProject, resolved from template membership at trigger time.
    /// Null for global consolidation runs (no owning project).
    /// </summary>
    // TODO [WARNING]: ProjectId is intended to be "baked at trigger time" and immutable thereafter,
    // but the public setter is wider than that invariant requires. The analogous ProjectName also uses
    // a public setter (consistent with existing pattern), and UpdateRunAsync does not currently
    // modify ProjectId, so there is no active regression. If the design shifts toward stricter
    // immutability, consider narrowing to `init` or removing the setter alongside ProjectName.
    public string? ProjectId { get; set; }

    /// <summary>
    /// When true, created refactoring issues will receive both <c>agent:generated</c> and
    /// <c>agent:next</c> labels, immediately dispatching them for agent execution.
    /// Defaults to <c>false</c> for backward compatibility with old persisted runs.
    /// </summary>
    public bool AutoDispatch { get; init; }

    /// <summary>
    /// The ID of the agent assigned to execute this consolidation run.
    /// Populated at dispatch time by <c>ConsolidationDispatchService.DispatchToAgentAsync</c>.
    /// Null for queued runs and runs dispatched before this field was introduced.
    /// </summary>
    // TODO: Consider narrowing to `internal set` — the public setter allows any deserializer
    // (including crafted JSON payloads via consolidation run endpoints) to overwrite AgentId.
    // If external mutation is never intentional, restrict the setter for defence-in-depth.
    public string? AgentId { get; set; }

    /// <summary>
    /// W3C traceparent captured when the run was first triggered.
    /// Persisted in the JSONB blob so it survives restarts and can be used during rehydration
    /// to link the worker's spans back to the originating trace even after a process restart.
    /// Null for runs created before this field was introduced.
    /// </summary>
    public string? TraceParent { get; set; }
}
