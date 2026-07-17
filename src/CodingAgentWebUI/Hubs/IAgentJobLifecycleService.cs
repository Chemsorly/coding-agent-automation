using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Encapsulates job-lifecycle business logic extracted from AgentHub.Pipeline.cs.
/// The hub delegates to this service after resolving SignalR-specific context
/// (connection ID → agent, job ID validation via [RequiresActiveJob]).
/// </summary>
public interface IAgentJobLifecycleService
{
    /// <summary>
    /// Handles job acceptance: transitions agent to Busy, WorkItem to Running.
    /// </summary>
    Task HandleJobAcceptedAsync(JobId jobId, AgentEntry? agent, CancellationToken ct);

    /// <summary>
    /// Handles job rejection: retry logic, re-queue or permanent failure, label swap, agent idle transition.
    /// </summary>
    Task HandleJobRejectedAsync(JobId jobId, AgentEntry? agent, string reason, CancellationToken ct);

    /// <summary>
    /// Handles job completion: consolidation branching, lifecycle manager call, defensive cleanup,
    /// agent idle transition, post-completion label swap + feedback comment.
    /// </summary>
    Task HandleJobCompletedAsync(JobId jobId, AgentEntry? agent, JobCompletionPayload payload, CancellationToken ct);

    /// <summary>
    /// Handles step transition: timestamp clamping, high-water mark advancement, metadata application,
    /// progress persistence, UI notification.
    /// </summary>
    void HandleStepTransition(JobId jobId, PipelineStep step, DateTimeOffset timestamp, Dictionary<string, string>? metadata);
}
