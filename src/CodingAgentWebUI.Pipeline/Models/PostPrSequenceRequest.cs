using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups the 12 parameters of <see cref="Services.PullRequestFinalizationService.RunPostPrSequenceAsync"/>
/// into a single parameter object to satisfy S107.
/// </summary>
public sealed record PostPrSequenceRequest
{
    public required PipelineRun Run { get; init; }
    public required bool IsDraft { get; init; }
    public required IAgentProvider AgentProvider { get; init; }
    public required IRepositoryProvider RepoProvider { get; init; }
    public required PipelineConfiguration Config { get; init; }
    public required IBrainSyncService? BrainSync { get; init; }
    public required IRepositoryProvider? BrainProvider { get; init; }
    public required FeedbackService FeedbackService { get; init; }
    public required IPipelineRunHistoryService? HistoryService { get; init; }
    public required Action<string> EmitOutputLine { get; init; }
    public required Func<PipelineStep, Task> TransitionCallback { get; init; }
}
