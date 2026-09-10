using CodingAgent.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Agent;

/// <summary>
/// Bundles the parameters needed by <see cref="PipelineExecutionContextBuilder"/> for PR creation into a single object,
/// reducing the method's parameter count from 14 to 5.
/// </summary>
internal sealed record PullRequestCreationContext
{
    public required IRepositoryProvider RepoProvider { get; init; }
    public required IAgentProvider AgentProvider { get; init; }
    public IRepositoryProvider? BrainProvider { get; init; }
    public BrainSyncService? BrainSync { get; init; }
    public required PipelineConfiguration Config { get; init; }
    public required OrchestratorProxy IssueOps { get; init; }
    public required JobAssignmentMessage Job { get; init; }
    public required PullRequestOrchestrator PrOrchestrator { get; init; }
    public required Action<string> EmitOutputLine { get; init; }

    /// <summary>
    /// Delegate for reporting step transitions during PR creation.
    /// Uses the awaited <c>InvokeAsync</c> path (not the fire-and-forget serialized path).
    /// </summary>
    public Func<PipelineStep, CancellationToken, Task>? ReportStepTransition { get; init; }
}
