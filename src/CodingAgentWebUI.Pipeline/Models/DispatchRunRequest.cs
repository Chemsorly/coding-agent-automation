using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups the provider and identity parameters shared by
/// <see cref="Interfaces.IDispatchRunCreator.CreateDispatchedRunAsync"/> and
/// <see cref="Interfaces.IDispatchRunCreator.ReserveRunIdAsync"/>.
/// Eliminates the excessive positional parameter lists (S107) on those interface methods.
/// </summary>
public sealed record DispatchRunRequest
{
    /// <summary>Provider config ID for issue operations.</summary>
    public required ProviderConfigId IssueProviderId { get; init; }

    /// <summary>Provider config ID for repository operations.</summary>
    public required ProviderConfigId RepoProviderId { get; init; }

    /// <summary>Issue identifier being dispatched.</summary>
    public required string IssueIdentifier { get; init; }

    /// <summary>Provider config ID for the agent that will execute the run.</summary>
    public required ProviderConfigId AgentProviderId { get; init; }

    /// <summary>Specific agent instance ID, or null to let the scheduler pick.</summary>
    public string? AgentId { get; init; }

    /// <summary>Brain/LLM provider config ID override, or null to use the agent default.</summary>
    public string? BrainProviderId { get; init; }

    /// <summary>Pipeline provider config ID override, or null.</summary>
    public string? PipelineProviderId { get; init; }

    /// <summary>Tag identifying who triggered this dispatch (e.g., "dispatch", "loop", "manual").</summary>
    public string InitiatedBy { get; init; } = "dispatch";

    /// <summary>Run type for the dispatched run. Only used by <c>CreateDispatchedRunAsync</c>.</summary>
    public PipelineRunType RunType { get; init; } = PipelineRunType.Implementation;
}
