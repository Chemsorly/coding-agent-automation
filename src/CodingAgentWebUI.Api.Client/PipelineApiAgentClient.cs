using System.Net.Http.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// <see cref="IPipelineApiAgentClient"/> backed by <see cref="HttpClient"/> registered
/// via <see cref="IHttpClientFactory"/>.
/// </summary>
internal sealed class PipelineApiAgentClient : IPipelineApiAgentClient
{
    private readonly HttpClient _http;

    public PipelineApiAgentClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<AgentEntry>> GetAgentsAsync(CancellationToken ct = default)
    {
        var agents = await _http.GetFromJsonAsync<List<AgentEntry>>(
            "/api/agents",
            PipelineJsonOptions.Default,
            ct);

        // A 200 carrying a literal `null` body is not something the endpoint produces, but
        // GetFromJsonAsync types it as nullable — collapse it to empty rather than propagating null
        // into an IReadOnlyList the callers dereference without checking.
        return agents ?? [];
    }

    public async Task AssignChatPromptAsync(string agentId, ChatPromptMessage message, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/agents/{Uri.EscapeDataString(agentId)}/chat-prompt",
            message,
            PipelineJsonOptions.Default,
            ct);

        response.EnsureSuccessStatusCode();
    }
}
