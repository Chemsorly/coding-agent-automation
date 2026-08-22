namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Typed HTTP client for the <c>/api/chat</c> endpoint group.
///
/// The Pipeline API owns <c>ChatJobDispatcher</c> alongside the <c>AgentHub</c> and
/// <c>AgentRegistryService</c> it polls. This client is the bridge that lets the Blazor
/// monolith (or any other operator-tier process) dispatch and terminate chat pods without
/// holding a direct dependency on the API's internal types.
/// </summary>
public interface IPipelineApiChatClient
{
    /// <summary>
    /// Dispatches a chat pod for <paramref name="agentSelector"/>, waits for the pod to
    /// connect to the API hub, and returns the <c>agentId</c> of the connected agent.
    ///
    /// <para>Blocks until the pod registers (up to the API's configured timeout).</para>
    /// </summary>
    /// <exception cref="HttpRequestException">
    /// 409 — a chat pod is already active for the given selector.<br/>
    /// 503 — no credential PVC available.<br/>
    /// 504 — pod did not connect within the timeout.
    /// </exception>
    Task<string> DispatchChatPodAsync(string agentSelector, string? model, string? effort, CancellationToken ct = default);

    /// <summary>
    /// Terminates the active chat session for <paramref name="agentId"/>.
    /// Idempotent — no-op when the agent is not known to the API.
    /// </summary>
    Task TerminateChatSessionAsync(string agentId, CancellationToken ct = default);
}
