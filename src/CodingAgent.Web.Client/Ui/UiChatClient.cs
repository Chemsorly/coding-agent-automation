using System.Net.Http.Json;
using CodingAgent.Contracts.Ui;

namespace CodingAgent.Web.Ui;

/// <summary>
/// The Agent Chat page (spec 051 2.5b). Launching needs operator on the chosen project, or global operator
/// without one; the other calls only work for a chat the user launched (an admin may use any). The API builds
/// every prompt, with the profile's and project's MCP servers, secrets and steering. A refusal, or a pod that
/// cannot be started, throws <see cref="InvalidOperationException"/> with the reason to show.
/// </summary>
public interface IUiChatClient
{
    Task<UiChatOptions> GetOptionsAsync(CancellationToken ct = default);

    /// <summary>Launches a chat pod and waits until it connects, up to the connect timeout.</summary>
    Task<UiChatSession> StartAsync(string profileId, string? projectId, CancellationToken ct = default);

    /// <summary>Sends a prompt; the reply streams to the hub group of <paramref name="sessionId"/>.</summary>
    Task SendPromptAsync(string agentId, string sessionId, string text, CancellationToken ct = default);

    Task TerminateAsync(string agentId, CancellationToken ct = default);

    /// <summary>Keeps the pod of an open chat window from idling out.</summary>
    Task KeepaliveAsync(string agentId, CancellationToken ct = default);
}

public sealed class UiChatClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiChatClient
{
    private const string Chat = "/api/ui/chat";

    private static string Agent(string agentId) => $"{Chat}/{Uri.EscapeDataString(agentId)}";

    public Task<UiChatOptions> GetOptionsAsync(CancellationToken ct = default) =>
        GetAsync($"{Chat}/options", UiJsonContext.Default.UiChatOptions, ct);

    public Task<UiChatSession> StartAsync(string profileId, string? projectId, CancellationToken ct = default) =>
        SendForResultAsync(
            HttpMethod.Post,
            Chat,
            JsonContent.Create(new UiChatStartRequest(profileId, projectId), UiJsonContext.Default.UiChatStartRequest),
            UiJsonContext.Default.UiChatSession,
            ct);

    public Task SendPromptAsync(string agentId, string sessionId, string text, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Post, $"{Agent(agentId)}/prompt", new UiChatPromptRequest(sessionId, text), UiJsonContext.Default.UiChatPromptRequest, ct);

    public Task TerminateAsync(string agentId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"{Agent(agentId)}/terminate", ct);

    public Task KeepaliveAsync(string agentId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"{Agent(agentId)}/keepalive", ct);
}
