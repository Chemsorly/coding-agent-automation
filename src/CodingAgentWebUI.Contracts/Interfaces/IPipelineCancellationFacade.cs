namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Facade/Aggregate Service that bundles cancellation-coordination dependencies for <see cref="Services.PipelineOrchestrationService"/>.
/// Groups services used during graceful shutdown to cancel active agent runs.
/// Registered as a singleton in DI.
/// </summary>
/// <remarks>
/// Now holds a single member after the in-memory dedup guard was removed — deduplication is owned by
/// the partial unique index on WorkItems plus the IsIssueBeingProcessed check at dispatch time.
/// A one-member facade earns nothing; consider injecting <see cref="IAgentCancellationSender"/>
/// directly into PipelineOrchestrationService and deleting this pair.
/// </remarks>
public interface IPipelineCancellationFacade
{
    /// <summary>Sends cancel signals to remote agents. Null when agent cancellation is not configured.</summary>
    IAgentCancellationSender? AgentCancellation { get; }
}
