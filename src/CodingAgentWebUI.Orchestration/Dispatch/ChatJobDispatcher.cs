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

    /// <summary>
    /// Terminates the chat session for the given agent.
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
}

// ─── Null-object implementation (SignalR mode) ────────────────────────────────

/// <summary>
/// No-op implementation of <see cref="IChatJobDispatcher"/> for a process that cannot dispatch
/// chat pods itself. Lets <c>AgentChat.razor</c> inject <see cref="IChatJobDispatcher"/>
/// unconditionally rather than mode-guarding through <c>IServiceProvider</c>.
///
/// <para>
/// It was introduced for SignalR mode, which no longer exists; it is now what the Blazor host
/// binds, because Spec 044 moved the real dispatcher to the Pipeline API alongside the hub whose
/// registry it polls. Requirements: Req 15.
/// </para>
/// </summary>
public sealed class NullChatJobDispatcher : IChatJobDispatcher
{
    public Task<string> DispatchChatPodAsync(string agentSelector, string? model, string? effort, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "Chat pods are dispatched by the Pipeline API, which owns the agent hub. This process cannot start one.");

    public Task TerminateChatSessionAsync(AgentId agentId, CancellationToken cancellationToken)
        => Task.CompletedTask; // nothing was started here, so there is nothing to terminate
}
