using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Hub;

/// <summary>
/// Encapsulates the run-type-specific execution logic for a completed job.
/// Each implementation handles one completion path (regular or consolidation).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentJobLifecycleService"/> selects the appropriate strategy based on
/// <see cref="PipelineRun.IssueProviderConfigId"/> and calls <see cref="ExecuteAsync"/>.
/// </para>
/// <para>
/// Responsibilities deliberately NOT in this interface:
/// <list type="bullet">
/// <item><description>Agent-idle transition — owned by <see cref="AgentJobLifecycleService.HandleJobCompletedAsync"/> after strategy returns.</description></item>
/// <item><description>Post-completion bookkeeping (label swap, feedback comment) — owned by the caller; consolidation runs skip it via inline guard.</description></item>
/// </list>
/// </para>
/// </remarks>
internal interface IJobCompletionStrategy
{
    /// <summary>
    /// Executes the run-type-specific completion logic.
    /// Does not touch agent state — agent-idle transition is the caller's responsibility.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="run">The in-memory pipeline run being completed.</param>
    /// <param name="payload">The completion payload from the agent.</param>
    /// <param name="activity">
    /// The active <see cref="Activity"/> started in <see cref="AgentJobLifecycleService.HandleJobCompletedAsync"/>.
    /// Passed so the regular strategy can set telemetry tags. May be null if tracing is disabled.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task ExecuteAsync(JobId jobId, PipelineRun run, JobCompletionPayload payload,
                      Activity? activity, CancellationToken ct);
}
