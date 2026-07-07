namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Pending job awaiting dispatch to an available agent.
/// </summary>
public sealed record PendingJob
{
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

    // --- Consolidation-specific (Legacy mode queueing) ---

    /// <summary>The consolidation run type. When set, this PendingJob represents a consolidation job rather than a pipeline job.</summary>
    public ConsolidationRunType? ConsolidationRunType { get; init; }

    /// <summary>Template ID for template-scoped consolidation runs.</summary>
    public string? ConsolidationTemplateId { get; init; }

    /// <summary>Workspace path for the consolidation run.</summary>
    public string? ConsolidationWorkspacePath { get; init; }

    /// <summary>Whether this pending job is a consolidation job (convenience check).</summary>
    public bool IsConsolidation => ConsolidationRunType.HasValue;
}
