using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups the 17 service/orchestrator parameters shared by all
/// <see cref="Services.Steps.PipelineStepContext"/> factory methods to satisfy S107.
/// Passed to <c>ForOrchestrator</c>, <c>ForAgent</c>, and <c>CreateBase</c>.
/// </summary>
public sealed record PipelineStepContextServices
{
    public required PipelineRun Run { get; init; }
    public required PipelineConfiguration Config { get; init; }
    public required IRepositoryProvider RepoProvider { get; init; }
    public required IAgentProvider AgentProvider { get; init; }
    public required IRepositoryProvider? BrainProvider { get; init; }
    public required IPipelineProvider? PipelineProvider { get; init; }
    public required CancellationTokenSource? Cts { get; init; }
    public required IConfigurationStore ConfigStore { get; init; }
    public required IPipelineCallbacks Callbacks { get; init; }
    public required IAgentIssueOperations IssueOps { get; init; }
    public required IAgentPhaseExecutor AgentExecution { get; init; }
    public required IQualityGateExecutor QualityGates { get; init; }
    public required IBrainSyncService? BrainSync { get; init; }
    public required PullRequestOrchestrator PrOrchestrator { get; init; }
    public required Serilog.ILogger Logger { get; init; }
    public required IQualityGateValidator? QualityGateValidator { get; init; }
}
