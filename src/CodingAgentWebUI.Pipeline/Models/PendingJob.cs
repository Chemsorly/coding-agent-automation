namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Pending job awaiting dispatch to an available agent.
/// </summary>
public sealed record PendingJob
{
    // The WorkItem ID is null when not yet claimed by the Job Controller
    public string? WorkItemId { get; init; }

    public required IssueIdentifier IssueIdentifier { get; init; }
    public string? IssueTitle { get; init; }
    public required ProviderConfigId IssueProviderId { get; init; }
    public required ProviderConfigId RepoProviderId { get; init; }
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

    // --- Consolidation-specific ---

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
    /// Number of times the drain service has attempted to dispatch this consolidation job to an agent.
    /// Incremented on each dispatch failure. When this reaches <c>PipelineConfiguration.MaxConsolidationDispatchRetries</c>
    /// (default 5), the job is discarded and the <c>ConsolidationRun</c> transitions to <c>Failed</c>.
    /// Irrelevant for non-consolidation jobs (always 0).
    /// </summary>
    public int ConsolidationDispatchAttempt { get; init; }

    /// <summary>
    /// Intra-queue dispatch priority. Higher values are dispatched first.
    /// Mirrors <see cref="PendingWorkItemDto.PriorityWeight"/>. Defaults to 0.
    /// Range: 0–1000.
    /// </summary>
    public int PriorityWeight { get; init; }

    /// <summary>
    /// Whether this pending job is a consolidation job.
    /// <see cref="RunType"/> is the single reliable discriminator — set to
    /// <see cref="PipelineRunType.Consolidation"/> by <c>DbPendingWorkQuery.ResolveRunType</c>.
    /// </summary>
    public bool IsConsolidation => RunType == PipelineRunType.Consolidation;
}
