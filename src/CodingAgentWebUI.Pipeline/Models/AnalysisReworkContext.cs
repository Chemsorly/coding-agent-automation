namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// In-memory context for a rework analysis run. Derived from <see cref="PipelineRun"/> fields at
/// call time and passed to <see cref="Services.Prompts.PromptBuilder.BuildAnalysisPrompt"/> so the
/// analysis agent knows it is operating on an existing PR and what (if anything) changed.
/// </summary>
/// <remarks>
/// This record is never serialized — it is created and consumed within a single call chain
/// (<c>RunSingleAnalysisAttemptAsync</c> → <c>BuildAnalysisPrompt</c>). No MessagePack attributes needed.
/// </remarks>
public sealed record AnalysisReworkContext(
    int PrNumber,
    string BranchName,
    IReadOnlyList<string> ForceResolvedFiles,
    bool HasReviewFeedback);
