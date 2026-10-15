using System.Net.Http.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

/// <summary>
/// <see cref="ISchedulerApiClient"/> backed by <see cref="HttpClient"/>. The Scheduler registers it with the
/// Pipeline API's base URL and the operator key.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "HTTP client wrapper — requires integration tests, not unit tests.")]
public sealed class HttpSchedulerApiClient : ISchedulerApiClient
{
    private readonly HttpClient _http;

    public HttpSchedulerApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<RetentionSweepResultDto> TriggerRetentionSweepAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("/api/scheduler/maintenance/retention-sweep", content: null, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RetentionSweepResultDto>(PipelineJsonOptions.Default, ct);
        return result ?? new RetentionSweepResultDto(0, 0, 0, 0);
    }

    public async Task<WorkItemCountsResponseDto> GetWorkItemCountsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<WorkItemCountsResponseDto>(
            "/api/work-items/counts-by-status", PipelineJsonOptions.Default, ct);
        return result ?? new WorkItemCountsResponseDto(Array.Empty<WorkItemCountDto>(), null);
    }

    public async Task ReportLoopStatusAsync(LoopStatusDto status, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync("/api/scheduler/loop-status", status, PipelineJsonOptions.Default, ct);
        response.EnsureSuccessStatusCode();
    }

    // NOTE (issue #3555): GetAgentsAsync and GetCredentialPoolAsync have no tests. The Scheduler tests mock
    // ISchedulerApiClient, so a wrong route string, wrong JSON options or a bad null-body fallback would
    // pass every test and only fail against a real API. Fix: add HttpSchedulerApiClient tests with a stub
    // HttpMessageHandler asserting the route ("/api/agents", "/api/agents/credential-pool"), the
    // deserialized payload and the null-body behaviour.
    public async Task<IReadOnlyList<AgentEntryDto>> GetAgentsAsync(CancellationToken ct = default)
    {
        var agents = await _http.GetFromJsonAsync<List<AgentEntryDto>>(
            "/api/agents", PipelineJsonOptions.Default, ct);
        return agents ?? [];
    }

    public async Task<CredentialPoolStatus> GetCredentialPoolAsync(CancellationToken ct = default)
    {
        var status = await _http.GetFromJsonAsync<CredentialPoolStatus>(
            "/api/agents/credential-pool", PipelineJsonOptions.Default, ct);
        return status ?? new CredentialPoolStatus(0, 0, 0);
    }
}
