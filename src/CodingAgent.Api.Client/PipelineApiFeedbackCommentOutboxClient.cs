using System.Net.Http.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

/// <summary>
/// <see cref="IPipelineApiFeedbackCommentOutboxClient"/> backed by <see cref="HttpClient"/>.
/// </summary>
internal sealed class PipelineApiFeedbackCommentOutboxClient : IPipelineApiFeedbackCommentOutboxClient
{
    private readonly HttpClient _http;

    public PipelineApiFeedbackCommentOutboxClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<FeedbackCommentOutboxEntry>> GetPendingAsync(
        int maxAttempts,
        int pageSize,
        CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<FeedbackCommentOutboxEntry>>(
            $"/api/feedback-comment-outbox/pending?maxAttempts={maxAttempts}&pageSize={pageSize}",
            PipelineJsonOptions.Default,
            ct);
        return result ?? [];
    }

    public async Task MarkCompletedAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"/api/feedback-comment-outbox/{id}/complete",
            content: null,
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task MarkFailedAsync(Guid id, string errorMessage, int maxAttempts, CancellationToken ct = default)
    {
        var body = new { errorMessage, maxAttempts };
        var response = await _http.PostAsJsonAsync(
            $"/api/feedback-comment-outbox/{id}/fail",
            body,
            PipelineJsonOptions.Default,
            ct);
        response.EnsureSuccessStatusCode();
    }
}
