using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Abstract base record containing the 6 properties shared between
/// <see cref="AgentPhaseContext"/> and <see cref="QualityGateContext"/>.
/// Changes to common pipeline context properties are made once here.
/// </summary>
public abstract record PipelineContextBase
{
    /// <summary>The active pipeline run.</summary>
    public required PipelineRun Run { get; init; }

    /// <summary>Pipeline configuration for timeouts, prompts, etc.</summary>
    public required PipelineConfiguration Config { get; init; }

    /// <summary>The agent provider for executing prompts.</summary>
    public required IAgentProvider AgentProvider { get; init; }

    /// <summary>Issue operations (post comments, swap labels).</summary>
    public required IAgentIssueOperations IssueOps { get; init; }

    /// <summary>Pipeline lifecycle callbacks.</summary>
    public required IPipelineCallbacks Callbacks { get; init; }

    /// <summary>Orchestrator-level cancellation token source (for coordinated cancellation).</summary>
    public CancellationTokenSource? OrchestratorCts { get; init; }
}
