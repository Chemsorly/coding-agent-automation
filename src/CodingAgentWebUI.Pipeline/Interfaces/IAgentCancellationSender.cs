using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Sends cancellation messages to connected agents. Separated into its own interface
/// so that <see cref="Services.PipelineOrchestrationService"/> can signal agents during
/// graceful shutdown without depending on the Orchestration project directly.
/// </summary>
public interface IAgentCancellationSender
{
    /// <summary>
    /// Sends a CancelJob message to the agent identified by <paramref name="agentId"/>.
    /// If the agent is not connected or the send fails, the method returns without throwing.
    /// </summary>
    Task SendCancelJobAsync(AgentId agentId, string runId, CancellationToken ct = default);
}
