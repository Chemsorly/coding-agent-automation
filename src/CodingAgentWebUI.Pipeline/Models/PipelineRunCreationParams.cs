namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups the parameters for <see cref="PipelineRun"/> factory methods, eliminating
/// positional parameter risks in the shared <c>CreateCore</c> construction path.
/// </summary>
/// <remarks>
/// Uses <c>sealed class</c> (not <c>record</c>) because records synthesize equality over all
/// properties — misleading for a parameter bag that contains <see cref="IReadOnlyList{T}"/>
/// (which would use reference equality in the synthesized <c>Equals</c>).
/// </remarks>
public sealed class PipelineRunCreationParams
{
    /// <summary>Unique run identifier.</summary>
    public required string RunId { get; init; }

    /// <summary>Issue identifier (e.g., "owner/repo#123"). Uses string to match factory method signatures; implicit conversion to <see cref="Models.IssueIdentifier"/> occurs in CreateCore.</summary>
    public required string IssueIdentifier { get; init; }

    /// <summary>Issue title for display.</summary>
    public required string IssueTitle { get; init; }

    /// <summary>Provider config ID for issue operations.</summary>
    public required string IssueProviderConfigId { get; init; }

    /// <summary>Provider config ID for repository operations.</summary>
    public required string RepoProviderConfigId { get; init; }

    /// <summary>Run type discriminator. Defaults to <see cref="PipelineRunType.Implementation"/>.</summary>
    public PipelineRunType RunType { get; init; } = PipelineRunType.Implementation;

    /// <summary>Optional explicit start time. When null, defaults to <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>How this run was initiated: "manual" or "loop".</summary>
    public string InitiatedBy { get; init; } = "manual";

    /// <summary>Agent ID assigned to this run, or null.</summary>
    public string? AgentId { get; init; }

    /// <summary>Agent provider config ID, or null.</summary>
    public string? AgentProviderConfigId { get; init; }

    /// <summary>Brain repository provider config ID, or null.</summary>
    public string? BrainProviderConfigId { get; init; }

    /// <summary>PR branch name for review runs.</summary>
    public string? ReviewPrBranchName { get; init; }

    /// <summary>PR target branch for review runs.</summary>
    public string? ReviewPrTargetBranch { get; init; }

    /// <summary>PR URL for review runs.</summary>
    public string? ReviewPrUrl { get; init; }

    /// <summary>PR description for review runs.</summary>
    public string? ReviewPrDescription { get; init; }

    /// <summary>PR author username for review runs.</summary>
    public string? ReviewPrAuthor { get; init; }

    /// <summary>Pre-fetched linked issue details for review runs.</summary>
    public IReadOnlyList<LinkedIssueContext>? LinkedIssueContexts { get; init; }

    /// <summary>Decomposition source indicator ("project-level" or "template-level"), or null.</summary>
    public string? DecompositionSource { get; init; }
}
