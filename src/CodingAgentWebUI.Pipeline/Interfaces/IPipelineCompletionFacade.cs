using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Facade/Aggregate Service that bundles post-execution completion dependencies for <see cref="PipelineOrchestrationService"/>.
/// Groups services involved in PR creation, finalization, feedback, and run history.
/// Registered as a singleton in DI.
/// </summary>
public interface IPipelineCompletionFacade
{
    /// <summary>Orchestrates pull request creation and finalization.</summary>
    PullRequestOrchestrator PrOrchestrator { get; }

    /// <summary>Runs post-PR-creation sequences (brain push, feedback, workspace cleanup).</summary>
    PullRequestFinalizationService Finalization { get; }

    /// <summary>Generates and posts agent feedback.</summary>
    FeedbackService FeedbackService { get; }

    /// <summary>Manages pipeline run history (persist, query, workspace cleanup).</summary>
    IPipelineRunHistoryService HistoryService { get; }
}
