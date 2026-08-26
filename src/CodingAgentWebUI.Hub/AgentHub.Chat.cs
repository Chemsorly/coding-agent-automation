using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgentWebUI.Hub;

public sealed partial class AgentHub
{
    // ── Interactive chat ─────────────────────────────────────────────────

    // ── UI group subscriptions for chat sessions ─────────────────────────

    /// <summary>
    /// Adds the caller's connection to the <c>chat-session-{sessionId}</c> SignalR group
    /// so that <see cref="IAgentHubUiClient.OnChatResponse"/> and
    /// <see cref="IAgentHubUiClient.OnChatCompleted"/> events are delivered to it.
    ///
    /// Called by <c>AgentChat.razor</c> immediately after sending a chat prompt.
    /// Only operator (non-agent) connections may subscribe — agents have no business
    /// receiving their own streamed output via a UI group.
    /// </summary>
    public Task SubscribeToChatSession(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return Groups.AddToGroupAsync(Context.ConnectionId, $"chat-session-{sessionId}");
    }

    /// <summary>
    /// Removes the caller's connection from the <c>chat-session-{sessionId}</c> group.
    /// Called when the session ends or the UI navigates away.
    /// </summary>
    public Task UnsubscribeFromChatSession(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, $"chat-session-{sessionId}");
    }

    /// <summary>
    /// Returns the agent's display ID and whether it owns the given chat session.
    /// Pure logic — no I/O, no side effects.
    /// </summary>
    internal static (bool IsValid, string AgentId) ValidateChatSessionOwnership(
        AgentEntry? agent, string sessionId)
    {
        var agentId = agent?.AgentId.Value ?? "unknown";
        var isValid = agent?.ActiveChatSessionId == sessionId;
        return (isValid, agentId);
    }

    /// <summary>
    /// Receives streamed chat response lines from an agent during interactive chat.
    /// Validates that the calling agent owns the session before broadcasting to UI circuits.
    /// </summary>
    public async Task ReportChatResponse(ChatResponseMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        var (isValid, agentId) = ValidateChatSessionOwnership(agent, message.SessionId);
        if (!isValid)
        {
            _logger.Warning("ReportChatResponse rejected — session {SessionId} not assigned to agent {AgentId}",
                message.SessionId, agentId);
            throw new HubException($"Session {message.SessionId} not assigned to agent {agentId}");
        }

        // Broadcast to subscribed UI circuits
        await _uiContext.Clients.Group($"chat-session-{message.SessionId}")
            .SendAsync(HubMethodNames.OnChatResponse, message.SessionId, message.Lines);

        _chatNotifier.NotifyChatResponse(message.SessionId, message.Lines);
    }

    /// <summary>
    /// Signals that a chat prompt execution has completed on the agent.
    /// Validates session ownership, clears <see cref="AgentEntry.ActiveChatSessionId"/>,
    /// and broadcasts the completion event to subscribed UI circuits.
    ///
    /// Does NOT transition the agent to Idle — the chat session remains active
    /// until the orchestrator sends CancelChat (End Chat / navigate away).
    /// </summary>
    public async Task ReportChatCompleted(ChatCompletedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        var (isValid, agentId) = ValidateChatSessionOwnership(agent, message.SessionId);
        if (!isValid)
        {
            _logger.Warning("ReportChatCompleted rejected — session {SessionId} not assigned to agent {AgentId}",
                message.SessionId, agentId);
            throw new HubException($"Session {message.SessionId} not assigned to agent {agentId}");
        }

        agent!.ActiveChatSessionId = null; // Also write to registry for cross-replica visibility
        _ = _facade.UpdateAgentFieldAsync(agent.AgentId, "activeChatSessionId", null);

        _logger.Information("Chat prompt completed for session {SessionId} on agent {AgentId} (exit={ExitCode})",
            message.SessionId, agent.AgentId, message.ExitCode);

        // Broadcast to subscribed UI circuits
        await _uiContext.Clients.Group($"chat-session-{message.SessionId}")
            .SendAsync(HubMethodNames.OnChatCompleted, message.SessionId, message.ExitCode, message.Error);

        _chatNotifier.NotifyChatCompleted(message.SessionId, message.ExitCode, message.Error);
    }
}
