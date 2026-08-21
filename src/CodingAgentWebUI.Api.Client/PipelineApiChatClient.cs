using System.Net.Http.Json;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// <see cref="IPipelineApiChatClient"/> backed by <see cref="HttpClient"/> registered
/// via <see cref="IHttpClientFactory"/>.
/// </summary>
internal sealed class PipelineApiChatClient : IPipelineApiChatClient
{
    private readonly HttpClient _http;

    public PipelineApiChatClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> DispatchChatPodAsync(
        string agentSelector, string? model, string? effort, CancellationToken ct = default)
    {
        var request = new { AgentSelector = agentSelector, Model = model, Effort = effort };

        var response = await _http.PostAsJsonAsync(
            "/api/chat/dispatch",
            request,
            PipelineJsonOptions.Default,
            ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DispatchChatPodResponse>(
            PipelineJsonOptions.Default, ct)
            ?? throw new InvalidOperationException("POST /api/chat/dispatch returned a null body.");

        return result.AgentId;
    }

    public async Task TerminateChatSessionAsync(string agentId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"/api/chat/{Uri.EscapeDataString(agentId)}/terminate",
            content: null,
            ct);

        response.EnsureSuccessStatusCode();
    }

    // Local mirror of the API's response record — avoids a project reference to CodingAgentWebUI.Api.
    private sealed record DispatchChatPodResponse(string AgentId);
}
