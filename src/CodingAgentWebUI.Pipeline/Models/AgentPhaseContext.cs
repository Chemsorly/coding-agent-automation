using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Bundles the common parameters needed by all agent phase execution methods.
/// Replaces the 10-13 positional parameters on <see cref="IAgentPhaseExecutor"/> methods.
/// </summary>
public sealed record AgentPhaseContext
{
    /// <summary>The active pipeline run.</summary>
    public required PipelineRun Run { get; init; }

    /// <summary>Pipeline configuration for timeouts, prompts, etc.</summary>
    public required PipelineConfiguration Config { get; init; }

    /// <summary>The agent provider for executing prompts.</summary>
    public required IAgentProvider AgentProvider { get; init; }

    /// <summary>The issue being worked on.</summary>
    public required IssueDetail Issue { get; init; }

    /// <summary>Parsed issue with structured requirements/acceptance criteria.</summary>
    public required ParsedIssue ParsedIssue { get; init; }

    /// <summary>Issue operations (post comments, swap labels).</summary>
    public required IAgentIssueOperations IssueOps { get; init; }

    /// <summary>Pipeline lifecycle callbacks.</summary>
    public required IPipelineCallbacks Callbacks { get; init; }

    /// <summary>Orchestrator-level cancellation token source (for coordinated cancellation).</summary>
    public CancellationTokenSource? OrchestratorCts { get; init; }
}
