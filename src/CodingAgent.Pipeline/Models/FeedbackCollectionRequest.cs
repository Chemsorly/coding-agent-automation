using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Groups the inputs of <see cref="Services.FeedbackService.CollectFeedbackCoreAsync"/>
/// into a single parameter object to satisfy S107.
/// </summary>
internal sealed record FeedbackCollectionRequest
{
    /// <summary>The run whose <see cref="PipelineRun.Feedback"/> is populated.</summary>
    public required PipelineRun Run { get; init; }

    /// <summary>Agent provider used to execute the feedback prompt.</summary>
    public required IAgentProvider AgentProvider { get; init; }

    /// <summary>History service used to load previous feedback categories; may be null.</summary>
    public required IPipelineRunHistoryService? HistoryService { get; init; }

    /// <summary>Builds the feedback prompt from the previously used harness/issue categories.</summary>
    public required Func<(IReadOnlyList<string> HarnessCategories, IReadOnlyList<string> IssueCategories), string> PromptFactory { get; init; }

    /// <summary>The run outcome the feedback describes.</summary>
    public required FeedbackOutcome Outcome { get; init; }

    /// <summary>Timeout for the feedback agent call, in seconds.</summary>
    public required int FeedbackTimeoutSeconds { get; init; }

    /// <summary>Receives agent output lines.</summary>
    public required Action<string> EmitOutputLine { get; init; }
}
