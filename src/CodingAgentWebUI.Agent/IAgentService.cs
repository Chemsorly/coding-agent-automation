using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Shared contract for observable agent state. Both <see cref="AgentWorkerService"/> (SignalR mode)
/// and <see cref="WorkItemAgentService"/> (K8s mode) implement this interface, enabling health
/// endpoints and monitoring to query agent status without depending on a specific service type.
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// Whether the agent is currently executing a job.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>
    /// The current pipeline step being executed, or null if idle.
    /// </summary>
    PipelineStep? CurrentStep { get; }

    /// <summary>
    /// Whether the hub connection to the orchestrator is active.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Cancels the currently running job, if any.
    /// </summary>
    void CancelCurrentJob();
}
