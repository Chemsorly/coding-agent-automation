using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups the 16 parameters of <see cref="Services.PullRequestFinalizationService.RunFullPrCreationAsync"/>
/// into a single parameter object to satisfy S107.
/// </summary>
public sealed record PrCreationRequest
{
    public required PipelineRun Run { get; init; }
    public required QualityGateReport Report { get; init; }
    public required bool IsDraft { get; init; }
    public required PullRequestOrchestrator PrOrchestrator { get; init; }
    public required IRepositoryProvider RepoProvider { get; init; }
    public required IAgentProvider AgentProvider { get; init; }
    public required IRepositoryProvider? BrainProvider { get; init; }
    public required IBrainSyncService? BrainSync { get; init; }
    public required PipelineConfiguration Config { get; init; }
    public required IssueDetail? Issue { get; init; }
    public required IReadOnlyList<IssueComment>? IssueComments { get; init; }
    public required FeedbackService FeedbackService { get; init; }
    public required IPipelineRunHistoryService? HistoryService { get; init; }
    public required Action<string> EmitOutputLine { get; init; }
    public required Func<PipelineStep, Task> TransitionCallback { get; init; }
}
