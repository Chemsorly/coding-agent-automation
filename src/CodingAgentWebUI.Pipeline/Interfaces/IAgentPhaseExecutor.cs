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
}
