using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups the 10 parameters of <see cref="Interfaces.IDispatchOrchestrationService.PrepareDistributionRequestAsync"/>
/// into a single parameter object to satisfy S107.
/// </summary>
public sealed record ImplementationDispatchOrchestrationRequest
{
    /// <summary>The issue to dispatch.</summary>
    public required string IssueIdentifier { get; init; }

    /// <summary>Issue provider config ID.</summary>
    public required ProviderConfigId IssueProviderId { get; init; }

    /// <summary>Repository provider config ID.</summary>
    public required ProviderConfigId RepoProviderId { get; init; }

    /// <summary>Optional brain provider config ID.</summary>
    public string? BrainProviderId { get; init; }

    /// <summary>Optional pipeline provider config ID.</summary>
    public string? PipelineProviderId { get; init; }

    /// <summary>Who initiated the dispatch.</summary>
    public required string InitiatedBy { get; init; }

    /// <summary>The project context for this dispatch.</summary>
    public required PipelineProject Project { get; init; }

    /// <summary>Work item task type (default: Implementation).</summary>
    public WorkItemTaskType TaskType { get; init; } = WorkItemTaskType.Implementation;

    /// <summary>Pipeline run type (default: Implementation).</summary>
    public PipelineRunType RunType { get; init; } = PipelineRunType.Implementation;
}

/// <summary>
/// Groups the 10 parameters of <see cref="Interfaces.IDispatchOrchestrationService.PrepareDecompositionDistributionRequestAsync"/>
/// into a single parameter object to satisfy S107.
/// </summary>
public sealed record DecompositionDispatchOrchestrationRequest
{
    /// <summary>Epic issue identifier.</summary>
    public required string EpicIdentifier { get; init; }

    /// <summary>Epic issue title.</summary>
    public required string EpicTitle { get; init; }

    /// <summary>Decomposition phase type (DecompositionAnalysis or Decomposition).</summary>
    public required PipelineRunType PhaseType { get; init; }

    /// <summary>Issue provider config ID.</summary>
    public required ProviderConfigId IssueProviderId { get; init; }

    /// <summary>Repository provider config ID.</summary>
    public required ProviderConfigId RepoProviderId { get; init; }

    /// <summary>Optional brain provider config ID.</summary>
    public string? BrainProviderId { get; init; }

    /// <summary>Who initiated the dispatch.</summary>
    public required string InitiatedBy { get; init; }

    /// <summary>The project context for this dispatch.</summary>
    public required PipelineProject Project { get; init; }

    /// <summary>Optional decomposition source (e.g., epic issue URL).</summary>
    public string? DecompositionSource { get; init; }
}
