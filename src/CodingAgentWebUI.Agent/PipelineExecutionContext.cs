using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Bundles the parameters needed by <see cref="LocalPipelineExecutor.CreateStepContext"/> into a single object,
/// reducing the method's parameter count from 19 to 1 (plus CancellationToken).
/// </summary>
internal sealed record PipelineExecutionContext
{
    public required JobAssignmentMessage Job { get; init; }
    public required PipelineRun Run { get; init; }
    public required PipelineConfiguration Config { get; init; }
    public required IRepositoryProvider RepoProvider { get; init; }
    public required IAgentProvider AgentProvider { get; init; }
    public IRepositoryProvider? BrainProvider { get; init; }
    public BrainSyncService? BrainSync { get; init; }
    public IPipelineProvider? PipelineProvider { get; init; }
    public required OrchestratorProxy IssueOps { get; init; }
    public required HubConnection Connection { get; init; }
    public required PullRequestOrchestrator PrOrchestrator { get; init; }
    public required AgentPhaseExecutor AgentExecution { get; init; }
    public required QualityGateExecutor QualityGates { get; init; }
    public required CancellationTokenSource LocalCts { get; init; }
    public required PullRequestCreationContext PrContext { get; init; }
    public required Action<PipelineStep> TransitionTo { get; init; }
    public required Action<string> EmitOutputLine { get; init; }
    public required Action<QualityGateReport> ReportQualityGateResult { get; init; }
}
