using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

// ─── Exception types ──────────────────────────────────────────────────────────

public sealed class ChatAlreadyActiveException(string jobName)
    : Exception($"A chat pod is already active for this selector (job: {jobName}).");

public sealed class NoPvcAvailableException()
    : Exception("No agent credentials (PVC) available for a chat pod.");

public sealed class ChatPodTimeoutException(int timeoutSeconds)
    : Exception($"Chat pod did not connect within {timeoutSeconds}s.")
{
    public int TimeoutSeconds { get; } = timeoutSeconds;
}

// ─── Interface ────────────────────────────────────────────────────────────────

/// <summary>
/// Abstracts chat pod dispatch so <c>AgentChat.razor</c> can inject it without mode-guarding.
/// <c>ChatJobDispatcher</c> (K8s mode) and <c>NullChatJobDispatcher</c> (SignalR mode)
/// both implement this interface.
/// Requirements: Req 15.
/// </summary>
public interface IChatJobDispatcher
{
    Task<string> DispatchChatPodAsync(string agentSelector, string? model, string? effort, CancellationToken cancellationToken);
    Task TerminateChatSessionAsync(AgentId agentId, CancellationToken cancellationToken);
}

// ─── Null-object implementation (SignalR mode) ────────────────────────────────

/// <summary>
/// No-op implementation of <see cref="IChatJobDispatcher"/> registered in SignalR mode.
/// Allows <c>AgentChat.razor</c> to inject <see cref="IChatJobDispatcher"/> unconditionally
/// without IServiceProvider mode-guarding.
/// Requirements: Req 15.
/// </summary>
public sealed class NullChatJobDispatcher : IChatJobDispatcher
{
    public Task<string> DispatchChatPodAsync(string agentSelector, string? model, string? effort, CancellationToken cancellationToken)
        => throw new NotSupportedException("Chat pod dispatch is not available in SignalR mode.");

    public Task TerminateChatSessionAsync(AgentId agentId, CancellationToken cancellationToken)
        => Task.CompletedTask; // safe no-op in SignalR mode
}
