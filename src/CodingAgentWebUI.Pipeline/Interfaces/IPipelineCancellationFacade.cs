namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Facade/Aggregate Service that bundles cancellation-coordination dependencies for <see cref="Services.PipelineOrchestrationService"/>.
/// Groups services used during graceful shutdown to cancel active agent runs and release dedup guards.
/// Registered as a singleton in DI.
/// </summary>
public interface IPipelineCancellationFacade
{
    /// <summary>Guards against duplicate issue dispatch. Null when deduplication is not configured.</summary>
    IJobDeduplicationGuard? DedupGuard { get; }

    /// <summary>Sends cancel signals to remote agents. Null when agent cancellation is not configured.</summary>
    IAgentCancellationSender? AgentCancellation { get; }
}
