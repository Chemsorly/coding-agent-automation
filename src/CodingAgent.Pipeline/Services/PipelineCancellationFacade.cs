using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Aggregate service bundling cancellation-coordination dependencies.
/// Reduces constructor parameter count on <see cref="PipelineOrchestrationService"/> per Seemann's Facade Service pattern.
/// </summary>
public sealed class PipelineCancellationFacade : IPipelineCancellationFacade
{
    public IAgentCancellationSender? AgentCancellation { get; }

    public PipelineCancellationFacade(IAgentCancellationSender? agentCancellation)
    {
        AgentCancellation = agentCancellation;
    }
}
