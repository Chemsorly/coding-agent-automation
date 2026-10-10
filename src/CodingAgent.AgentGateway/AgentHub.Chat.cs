using CodingAgent.Contracts;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgent.AgentGateway;

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
    /// Validates that the calling agent owns the session by reading <c>ActiveChatSessionId</c>
    /// from the authoritative registry store (Redis in distributed mode, in-memory otherwise),
    /// ensuring correct ownership validation across replicas regardless of which replica received
    /// the original <c>POST /api/agents/{agentId}/chat-prompt</c> request.
    /// </summary>
    public async Task ReportChatResponse(ChatResponseMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var caller = _facade.GetByConnectionId(Context.ConnectionId);
        if (caller is null)
        {
            _logger.Warning("ReportChatResponse rejected — session {SessionId} not assigned to agent {AgentId}",
                SanitizeForLog(message.SessionId), "unknown");
            throw new HubException($"Session {SanitizeForLog(message.SessionId)} not assigned to agent unknown");
        }

        // TODO: Context.ConnectionAborted is cancelled when the agent's SignalR connection is
        // aborted mid-call (e.g. transient disconnect). If GetByAgentIdAsync throws
        // OperationCanceledException it propagates unhandled — surfaced as a generic internal
        // error rather than a structured HubException, diverging from every other rejection
        // path in this file. Consider using CancellationToken.None or catching
        // OperationCanceledException and converting it to a HubException for consistency.
        var authoritativeEntry = await _facade.GetByAgentIdAsync(caller.AgentId, Context.ConnectionAborted);
        if (authoritativeEntry is null)
        {
            _logger.Warning("ReportChatResponse rejected — session {SessionId} not assigned to agent {AgentId}",
                SanitizeForLog(message.SessionId), caller.AgentId.Value);
            throw new HubException($"Session {SanitizeForLog(message.SessionId)} not assigned to agent {caller.AgentId.Value}");
        }

        var (isValid, agentId) = ValidateChatSessionOwnership(authoritativeEntry, message.SessionId);
        if (!isValid)
        {
            _logger.Warning("ReportChatResponse rejected — session {SessionId} not assigned to agent {AgentId}",
                SanitizeForLog(message.SessionId), agentId);
            throw new HubException($"Session {SanitizeForLog(message.SessionId)} not assigned to agent {agentId}");
        }

        // Broadcast to subscribed UI circuits
        await _uiContext.Clients.Group($"chat-session-{message.SessionId}")
            .SendAsync(HubMethodNames.OnChatResponse, message.SessionId, message.Lines);

        _chatNotifier.NotifyChatResponse(message.SessionId, message.Lines);
    }

    /// <summary>
    /// Signals that a chat prompt execution has completed on the agent.
    /// Validates session ownership by reading <c>ActiveChatSessionId</c> from the authoritative
    /// registry store (Redis in distributed mode, in-memory otherwise), clears it via
    /// <see cref="IAgentHubFacade.UpdateAgentFieldAsync"/> (awaited before broadcast so the
    /// session is always cleared before the UI re-enables input), then broadcasts the completion
    /// event to subscribed UI circuits.
    ///
    /// Does NOT transition the agent to Idle — the chat session remains active
    /// until the orchestrator sends CancelChat (End Chat / navigate away).
    /// </summary>
    public async Task ReportChatCompleted(ChatCompletedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var caller = _facade.GetByConnectionId(Context.ConnectionId);
        if (caller is null)
        {
            _logger.Warning("ReportChatCompleted rejected — session {SessionId} not assigned to agent {AgentId}",
                SanitizeForLog(message.SessionId), "unknown");
            throw new HubException($"Session {SanitizeForLog(message.SessionId)} not assigned to agent unknown");
        }

        // TODO: Context.ConnectionAborted is cancelled when the agent's SignalR connection is
        // aborted mid-call. If GetByAgentIdAsync throws OperationCanceledException it propagates
        // unhandled rather than as a structured HubException. See ReportChatResponse for details.
        var authoritativeEntry = await _facade.GetByAgentIdAsync(caller.AgentId, Context.ConnectionAborted);
        if (authoritativeEntry is null)
        {
            _logger.Warning("ReportChatCompleted rejected — session {SessionId} not assigned to agent {AgentId}",
                SanitizeForLog(message.SessionId), caller.AgentId.Value);
            throw new HubException($"Session {SanitizeForLog(message.SessionId)} not assigned to agent {caller.AgentId.Value}");
        }

        var (isValid, agentId) = ValidateChatSessionOwnership(authoritativeEntry, message.SessionId);
        if (!isValid)
        {
            _logger.Warning("ReportChatCompleted rejected — session {SessionId} not assigned to agent {AgentId}",
                SanitizeForLog(message.SessionId), agentId);
            throw new HubException($"Session {SanitizeForLog(message.SessionId)} not assigned to agent {agentId}");
        }

        // Clear the session stamp in the authoritative store before broadcasting completion,
        // so the UI input box is never re-enabled before the session is fully cleared.
        await _facade.UpdateAgentFieldAsync(caller.AgentId, AgentFieldNames.ActiveChatSessionId, null);

        _logger.Information("Chat prompt completed for session {SessionId} on agent {AgentId} (exit={ExitCode})",
            message.SessionId, caller.AgentId, message.ExitCode);

        // Broadcast to subscribed UI circuits
        await _uiContext.Clients.Group($"chat-session-{message.SessionId}")
            .SendAsync(HubMethodNames.OnChatCompleted, message.SessionId, message.ExitCode, message.Error);

        _chatNotifier.NotifyChatCompleted(message.SessionId, message.ExitCode, message.Error);
    }
}
