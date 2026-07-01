namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Facade/Aggregate Service that bundles execution-phase dependencies for <see cref="Services.PipelineOrchestrationService"/>.
/// Groups services that participate in pipeline step execution (agent phases, quality gates, brain sync).
/// Registered as a singleton in DI.
/// </summary>
public interface IPipelineExecutionFacade
{
    /// <summary>Executes agent analysis/implementation/review phases.</summary>
    IAgentPhaseExecutor AgentExecution { get; }

    /// <summary>Runs quality gate checks (build, test, coverage).</summary>
    IQualityGateExecutor QualityGates { get; }

    /// <summary>Validates quality gate prerequisites (baseline checks). May be null if not configured.</summary>
    IQualityGateValidator? QualityGateValidator { get; }

    /// <summary>Handles brain repository sync operations (pull/push).</summary>
    IBrainSyncService BrainSync { get; }
}
