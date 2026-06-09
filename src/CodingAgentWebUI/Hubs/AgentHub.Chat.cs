using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgentWebUI.Hubs;

public sealed partial class AgentHub
{
    // ── Interactive chat ─────────────────────────────────────────────────

    /// <summary>
    /// Receives streamed chat response lines from an agent during interactive chat.
    /// Validates that the calling agent owns the session before broadcasting.
    /// </summary>
    public Task ReportChatResponse(ChatResponseMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        var agentId = agent?.AgentId ?? "unknown";
        if (agent?.ActiveChatSessionId != message.SessionId)
        {
            _logger.Warning("ReportChatResponse rejected — session {SessionId} not assigned to agent {AgentId}",
                message.SessionId, agentId);
            throw new HubException($"Session {message.SessionId} not assigned to agent {agentId}");
        }

        _orchestration.NotifyChatResponse(message.SessionId, message.Lines);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Signals that a chat prompt execution has completed on the agent.
    /// Validates session ownership and clears <see cref="AgentEntry.ActiveChatSessionId"/>.
    /// Does NOT transition the agent to Idle — the chat session remains active
    /// until the orchestrator sends CancelChat (End Chat / navigate away).
    /// </summary>
    public Task ReportChatCompleted(ChatCompletedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        var agentId = agent?.AgentId ?? "unknown";
        if (agent?.ActiveChatSessionId != message.SessionId)
        {
            _logger.Warning("ReportChatCompleted rejected — session {SessionId} not assigned to agent {AgentId}",
                message.SessionId, agentId);
            throw new HubException($"Session {message.SessionId} not assigned to agent {agentId}");
        }

        agent.ActiveChatSessionId = null;

        _logger.Information("Chat prompt completed for session {SessionId} on agent {AgentId} (exit={ExitCode})",
            message.SessionId, agent.AgentId, message.ExitCode);

        _orchestration.NotifyChatCompleted(message.SessionId, message.ExitCode, message.Error);
        return Task.CompletedTask;
    }
}
