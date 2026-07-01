using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Aggregate service bundling PR completion and finalization dependencies.
/// Reduces constructor parameter count on <see cref="PipelineOrchestrationService"/> per Seemann's Facade Service pattern.
/// </summary>
public sealed class PipelineCompletionFacade : IPipelineCompletionFacade
{
    public PullRequestOrchestrator PrOrchestrator { get; }
    public PullRequestFinalizationService Finalization { get; }
    public FeedbackService FeedbackService { get; }
    public IPipelineRunHistoryService HistoryService { get; }

    public PipelineCompletionFacade(
        PullRequestOrchestrator prOrchestrator,
        PullRequestFinalizationService finalization,
        FeedbackService feedbackService,
        IPipelineRunHistoryService historyService)
    {
        ArgumentNullException.ThrowIfNull(prOrchestrator);
        ArgumentNullException.ThrowIfNull(finalization);
        ArgumentNullException.ThrowIfNull(feedbackService);
        ArgumentNullException.ThrowIfNull(historyService);

        PrOrchestrator = prOrchestrator;
        Finalization = finalization;
        FeedbackService = feedbackService;
        HistoryService = historyService;
    }
}
