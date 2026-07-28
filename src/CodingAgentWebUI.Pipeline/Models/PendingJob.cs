namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Pending job awaiting dispatch to an available agent.
/// </summary>
public sealed record PendingJob
{
    /// <summary>
    /// The WorkItem ID (DB modes only). Null in legacy/in-memory mode.
    /// Used by the UI to cancel pending items via <see cref="IWorkDistributor.CancelJobAsync"/>.
    /// </summary>
    public string? WorkItemId { get; init; }

    public required string IssueIdentifier { get; init; }
    public string? IssueTitle { get; init; }
    public required string IssueProviderId { get; init; }
    public required string RepoProviderId { get; init; }
    public string? BrainProviderId { get; init; }
    public string? PipelineProviderId { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public required string InitiatedBy { get; init; }
    public IReadOnlyList<string> RequiredLabels { get; init; } = [];
    public PipelineRunType RunType { get; init; } = PipelineRunType.Implementation;
    public string? PrBranchName { get; init; }
    public string? PrDescription { get; init; }
    public string? PrUrl { get; init; }
    public string? PrTargetBranch { get; init; }
    public string? PrAuthor { get; init; }

    /// <summary>The project that owns this template. Set at poll time, used at dispatch time for settings resolution.</summary>
    public PipelineProject? Project { get; init; }

    /// <summary>
    /// For decomposition runs: whether the epic was polled from the project-level
    /// EpicIssueProviderId ("project-level") or the template's own IssueProviderId ("template-level").
    /// Null for non-decomposition runs.
    /// </summary>
    public string? DecompositionSource { get; init; }

    /// <summary>The WorkItem task type. Used as the primary discriminator for consolidation jobs.</summary>
    public WorkItemTaskType TaskType { get; init; } = WorkItemTaskType.Implementation;

    // --- Consolidation-specific (Legacy mode queueing) ---

    /// <summary>The consolidation run type. When set, this PendingJob represents a consolidation job rather than a pipeline job.</summary>
    public ConsolidationRunType? ConsolidationRunType { get; init; }

    /// <summary>Template ID for template-scoped consolidation runs.</summary>
    public string? ConsolidationTemplateId { get; init; }

    /// <summary>Workspace path for the consolidation run.</summary>
    public string? ConsolidationWorkspacePath { get; init; }

    /// <summary>
    /// When true, created refactoring issues will receive both <c>agent:generated</c> and
    /// <c>agent:next</c> labels. Propagated from <see cref="JobDistributionRequest.AutoDispatch"/>.
    /// Defaults to <c>false</c> for backward compatibility.
    /// </summary>
    public bool AutoDispatch { get; init; }

    /// <summary>
    /// Number of consecutive failed dispatch attempts.
    /// Incremented by <c>JobQueueDrainService</c> when <c>TryDispatchToAgentAsync</c> fails for a
    /// consolidation job. Persists across drain cycles because <c>ReEnqueue</c> puts the same
    /// object reference back into the <c>ConcurrentQueue</c>. Defaults to 0.
    /// Not applicable to non-consolidation jobs (implementation, review, decomposition).
    /// </summary>
    /// <remarks>
    /// NOTE: This is a mutable property on a <c>sealed record</c>. It participates in
    /// compiler-generated equality/hash-code, so two <c>PendingJob</c> instances representing
    /// the same issue but with different <c>RetryCount</c> values will compare as unequal.
    /// This is acceptable because <c>PendingJob</c> instances in the queue are compared by
    /// reference identity only; the dedup dictionary uses composite string keys. Do NOT use
    /// <c>with { }</c> copy-construction on queued <c>PendingJob</c> objects — the copy will
    /// reset <c>RetryCount</c> to 0, discarding the accumulated retry count.
    /// </remarks>
    public int RetryCount { get; set; }

    /// <summary>
    /// Whether this pending job is a consolidation job.
    /// Uses TaskType as the primary discriminator (stored on the WorkItem row, always reliable),
    /// with ConsolidationRunType.HasValue as a secondary indicator for legacy in-memory mode.
    /// </summary>
    public bool IsConsolidation => TaskType == WorkItemTaskType.Consolidation || ConsolidationRunType.HasValue;
}
