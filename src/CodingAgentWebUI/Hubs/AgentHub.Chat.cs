using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Hubs;

public sealed partial class AgentHub
{
    // ── Interactive chat ─────────────────────────────────────────────────

    /// <summary>
    /// Receives streamed chat response lines from an agent during interactive chat.
    /// Broadcasts to the orchestration service for UI consumption.
    /// </summary>
    public Task ReportChatResponse(ChatResponseMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        _logger.Debug("Chat response received for session {SessionId}: {LineCount} lines",
            message.SessionId, message.Lines.Count);

        _orchestration.NotifyChatResponse(message.SessionId, message.Lines);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Signals that a chat prompt execution has completed on the agent.
    /// Does NOT transition the agent to Idle — the chat session remains active
    /// until the orchestrator sends CancelChat (End Chat / navigate away).
    /// </summary>
    public Task ReportChatCompleted(ChatCompletedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        if (agent is not null)
        {
            _logger.Information("Chat prompt completed for session {SessionId} on agent {AgentId} (exit={ExitCode})",
                message.SessionId, agent.AgentId, message.ExitCode);
        }

        _orchestration.NotifyChatCompleted(message.SessionId, message.ExitCode, message.Error);
        return Task.CompletedTask;
    }
}
