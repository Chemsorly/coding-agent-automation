namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Abstraction for dispatching pipeline jobs to remote agents.
/// When configured, <see cref="Services.PipelineLoopService"/> dispatches issues
/// to agents instead of executing them locally via <c>StartPipelineAsync</c>.
/// </summary>
public interface IJobDispatcher
{
    /// <summary>
    /// Attempts to dispatch an issue to an available agent.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the issue was dispatched or enqueued successfully;
    /// <c>false</c> if the issue is already being processed or queued.
    /// </returns>
    Task<bool> TryDispatchAsync(
        string issueIdentifier,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        CancellationToken ct,
        string? issueTitle = null);

    /// <summary>
    /// Whether any agents are registered and available for dispatch.
    /// When <c>false</c>, the loop should fall back to local execution.
    /// </summary>
    bool HasRegisteredAgents { get; }

    /// <summary>
    /// Whether the given issue is already being processed or queued.
    /// </summary>
    bool IsIssueBeingProcessedOrQueued(string issueIdentifier);
}
