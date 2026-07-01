using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Aggregate service bundling execution-phase dependencies.
/// Reduces constructor parameter count on <see cref="PipelineOrchestrationService"/> per Seemann's Facade Service pattern.
/// </summary>
public sealed class PipelineExecutionFacade : IPipelineExecutionFacade
{
    public IAgentPhaseExecutor AgentExecution { get; }
    public IQualityGateExecutor QualityGates { get; }
    public IQualityGateValidator? QualityGateValidator { get; }
    public IBrainSyncService BrainSync { get; }

    public PipelineExecutionFacade(
        IAgentPhaseExecutor agentExecution,
        IQualityGateExecutor qualityGates,
        IQualityGateValidator? qualityGateValidator,
        IBrainSyncService brainSync)
    {
        ArgumentNullException.ThrowIfNull(agentExecution);
        ArgumentNullException.ThrowIfNull(qualityGates);
        ArgumentNullException.ThrowIfNull(brainSync);

        AgentExecution = agentExecution;
        QualityGates = qualityGates;
        QualityGateValidator = qualityGateValidator;
        BrainSync = brainSync;
    }
}
