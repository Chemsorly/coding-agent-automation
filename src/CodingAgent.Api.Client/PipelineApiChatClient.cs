using System.Net.Http.Json;
using System.Text.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

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

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            DispatchProblemBody? problem = null;
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    problem = JsonSerializer.Deserialize<DispatchProblemBody>(body, PipelineJsonOptions.Lenient);
                }
                catch (JsonException)
                {
                    // Non-JSON body — fall through to fallback message.
                }
            }

            var message = string.IsNullOrWhiteSpace(problem?.Detail)
                ? $"POST /api/chat/dispatch failed with HTTP {(int)response.StatusCode} ({response.StatusCode})."
                : problem.Detail;

            throw new ChatDispatchFailedException(message, response.StatusCode, problem?.TimeoutSeconds);
        }

        var result = await response.Content.ReadFromJsonAsync<DispatchChatPodResponse>(
            PipelineJsonOptions.Default, ct)
            ?? throw new InvalidOperationException("POST /api/chat/dispatch returned a null body.");

        return result.AgentId;
    }

    public async Task TerminateChatSessionAsync(AgentId agentId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"/api/chat/{Uri.EscapeDataString(agentId.Value)}/terminate",
            content: null,
            ct);

        response.EnsureSuccessStatusCode();
    }

    public async Task SendKeepaliveAsync(AgentId agentId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"/api/chat/{Uri.EscapeDataString(agentId.Value)}/keepalive",
            content: null,
            ct);

        response.EnsureSuccessStatusCode();
    }

    // Local mirror of the API's response record — avoids a project reference to CodingAgent.Api.
    private sealed record DispatchChatPodResponse(string AgentId);

    // Deserialization target for the ProblemDetails body returned by DispatchChatPod on error.
    private sealed record DispatchProblemBody(string? Detail, int? TimeoutSeconds);
}
