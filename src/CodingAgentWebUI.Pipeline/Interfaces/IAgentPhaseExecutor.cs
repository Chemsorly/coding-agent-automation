using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Executes agent phases: analysis, code generation, and code review.
/// Abstraction over <c>AgentPhaseExecutor</c> for testability.
/// </summary>
public interface IAgentPhaseExecutor
{
    /// <summary>
    /// Executes the analysis phase. Returns true if the pipeline should continue, false if it should stop.
    /// </summary>
    Task<bool> ExecuteAnalysisPhaseAsync(
        AgentPhaseContext context,
        IReadOnlyList<IssueComment> issueComments,
        CancellationToken ct);

    /// <summary>
    /// Executes the code generation phase. Returns true if the pipeline should continue, false if it should stop.
    /// </summary>
    Task<bool> ExecuteCodeGenerationAsync(
        AgentPhaseContext context,
        CancellationToken ct,
        string? promptOverride = null);

    /// <summary>
    /// Executes the code review loop with multi-agent support.
    /// </summary>
    Task ExecuteCodeReviewAsync(
        AgentPhaseContext context,
        CancellationToken ct,
        IReadOnlyList<ReviewerConfiguration>? resolvedReviewerConfigs = null);

    /// <summary>
    /// Executes a follow-up prompt against a specific review agent, requesting reformatted output.
    /// Used by PostReviewFindingsStep to retry structured output extraction.
    /// This is a FRESH prompt (not a conversation resume) — the follow-up prompt contains
    /// the agent's original output + reformat instructions. No session tracking required.
    /// Returns the agent's response text, or empty string if the agent cannot be reached.
    /// </summary>
    /// <param name="context">The agent phase context (workspace, issue, config).</param>
    /// <param name="reviewerConfig">The reviewer configuration that was used during the review phase.</param>
    /// <param name="followUpPrompt">The follow-up prompt text requesting reformatted output.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> ExecuteFollowUpAsync(
        AgentPhaseContext context,
        ReviewerConfiguration reviewerConfig,
        string followUpPrompt,
        CancellationToken ct)
        => Task.FromResult(string.Empty);
}
