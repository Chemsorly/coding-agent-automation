using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Sends cancellation messages to connected agents. Separated into its own interface
/// so that <see cref="Services.PipelineRunLifecycleService"/> can signal agents
/// without depending on the Orchestration project directly.
/// </summary>
public interface IAgentCancellationSender
{
    /// <summary>
    /// Sends a CancelJob message to the agent identified by <paramref name="agentId"/>.
    /// If the agent is not connected or the send fails, the method returns without throwing.
    /// </summary>
    Task SendCancelJobAsync(AgentId agentId, RunId runId, CancellationToken ct = default);
}
