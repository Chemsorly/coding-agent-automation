using System.Net.Http.Json;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// <see cref="ISchedulerApiClient"/> backed by <see cref="HttpClient"/>.
/// Base URL configured by consumer via IHttpClientFactory named client or typed registration.
///
/// Loop control endpoints (/loop/*) are served by the Scheduler on port 8091 (SchedulerApi__BaseUrl).
/// Maintenance and metrics endpoints (/api/scheduler/* and /api/work-items/*) are served by the
/// API on port 8090 (PipelineApi__BaseUrl). Callers should register two separate typed clients
/// or use the appropriate base URL for each group. For now, the base URL is set by the DI
/// registration — the Scheduler uses the API's base URL for maintenance/metrics, and the WebUI
/// uses the Scheduler's base URL for loop controls.
/// </summary>
public sealed class HttpSchedulerApiClient : ISchedulerApiClient
{
    private readonly HttpClient _http;

    public HttpSchedulerApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<LoopStatusDto> GetLoopStatusAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<LoopStatusDto>("/loop/status", PipelineJsonOptions.Default, ct);
        return result ?? new LoopStatusDto();
    }

    public async Task<LoopStartResultDto> StartLoopAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("/loop/start", content: null, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<LoopStartResultDto>(PipelineJsonOptions.Default, ct);
        return result ?? new LoopStartResultDto(false, "Empty response from Scheduler");
    }

    public async Task StopLoopAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("/loop/stop", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ResumeLoopAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("/loop/resume", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<RetentionSweepResultDto> TriggerRetentionSweepAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("/api/scheduler/maintenance/retention-sweep", content: null, ct);

        if ((int)response.StatusCode == 503)
            throw new RetentionSweepUnavailableException();

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RetentionSweepResultDto>(PipelineJsonOptions.Default, ct);
        return result ?? new RetentionSweepResultDto(0, 0, 0, 0, 0);
    }

    public async Task<WorkItemCountDto[]> GetWorkItemCountsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<WorkItemCountDto[]>(
            "/api/work-items/counts-by-status", PipelineJsonOptions.Default, ct);
        return result ?? Array.Empty<WorkItemCountDto>();
    }
}
