using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration.Dispatch;

// ─── Exception types ──────────────────────────────────────────────────────────

public sealed class NoPvcAvailableException()
    : Exception("No agent credentials (PVC) available for a chat pod.");

public sealed class ChatPodTimeoutException(int timeoutSeconds)
    : Exception($"Chat pod did not connect within {timeoutSeconds}s.")
{
    // TODO [WARNING]: When timeoutSeconds is 0 (the unknown-timeout sentinel used by ApiChatJobDispatcher),
    // the exception message reads "Chat pod did not connect within 0s." which is misleading. AgentChat.razor's
    // ClassifyLaunchError guards on TimeoutSeconds > 0 so the UI is correct, but any caller that surfaces
    // .Message directly (e.g. a future log line or a fallback that bypasses ClassifyLaunchError) would display
    // "within 0s." Consider using a sentinel-free message (e.g. "Chat pod did not connect in time.") when
    // timeoutSeconds == 0, or accepting a nullable int here.
    public int TimeoutSeconds { get; } = timeoutSeconds;
}

// ─── Interface ────────────────────────────────────────────────────────────────

/// <summary>
/// Abstracts chat pod dispatch so <c>AgentChat.razor</c> can inject it.
/// Implemented by <c>ChatJobDispatcher</c> (API host) and <c>ApiChatJobDispatcher</c> (web host).
/// Requirements: Req 15.
/// </summary>
public interface IChatJobDispatcher
{
    Task<string> DispatchChatPodAsync(string agentSelector, string? model, string? effort, CancellationToken cancellationToken);

    /// <summary>
    /// Terminates the active chat session for the given agent.
    ///
    /// <para>
    /// <b>agentId == jobName invariant:</b> the <c>ChatJobDispatcher</c> implementation
    /// relies on the fact that <c>agentId == jobName</c> for chat pods. The pod's
    /// <c>AGENT_ID</c> environment variable is set via a Kubernetes field ref to
    /// <c>metadata.name</c>, so the value the agent reports at hub registration equals
    /// the K8s Job name. If a future pod image change breaks this invariant, a warning
    /// is logged in <c>ChatJobDispatcher.PollForAgentConnectionAsync</c> and termination
    /// may fail to locate the correct job.
    /// </para>
    /// </summary>
    Task TerminateChatSessionAsync(AgentId agentId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a client keepalive heartbeat for the chat session, resetting its idle clock.
    /// Called by <c>POST /api/chat/{agentId}/keepalive</c> while the browser chat window is open.
    /// No-op when the session is not found (already terminated or unknown agentId).
    /// </summary>
    void SendClientKeepalive(string agentId);
}
