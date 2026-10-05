using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration.Registry;

/// <summary>
/// Encapsulates the allowed and rejected edges of the agent status state machine.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="DistributedAgentRegistryService"/> to remove the
/// csharpsquid:S3776 cognitive-complexity violation on <c>TransitionStatusAsync</c> and give
/// the transition rules a single, independently-testable home.
/// </para>
/// <para>
/// State machine: Idle ↔ Busy ↔ Disconnected, plus self-transitions. The only explicitly
/// rejected edge is <c>Disconnected → Busy</c>: an agent that has disconnected must re-register
/// (and thereby obtain a fresh connection ID) before it can be dispatched to again.
/// </para>
/// </remarks>
internal static class AgentStatusTransition
{
    /// <summary>
    /// Returns <see langword="true"/> if transitioning from <paramref name="current"/> to
    /// <paramref name="next"/> is a valid status-machine edge; <see langword="false"/> if the
    /// transition is rejected.
    /// </summary>
    public static bool IsAllowed(AgentStatus current, AgentStatus next)
    {
        // Disconnected → Busy is the only explicitly rejected edge: the agent must
        // re-register first so the registry has a fresh connection ID.
        if (current == AgentStatus.Disconnected && next == AgentStatus.Busy)
            return false;

        return true;
    }
}
