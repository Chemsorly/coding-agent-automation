using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// HTTP client for the orchestrator's Work Item API endpoints.
/// Used in K8s mode to fetch assignments and report status transitions.
/// </summary>
/// <remarks>
/// <para><b>GET /api/work-items/{id}/assignment</b> — 6 retries, exponential backoff 2s→64s (~126s window).</para>
/// <para><b>POST /api/work-items/{id}/status</b> — 3 retries, exponential backoff 5s, 10s, 20s.</para>
/// </remarks>
public sealed class WorkItemHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly Serilog.ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = PipelineJsonOptions.Default;

    public WorkItemHttpClient(HttpClient httpClient, Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the work item assignment from the orchestrator.
    /// Retries up to 6 times with exponential backoff (2s→64s, ~126s total window)
    /// to accommodate orchestrator rolling updates.
    /// </summary>
    /// <returns>
    /// The deserialized <see cref="JobAssignmentMessage"/>, or null if the work item
    /// is in a terminal status (410 Gone).
    /// </returns>
    /// <exception cref="WorkItemFetchException">Thrown when all retries are exhausted or a non-retryable error occurs.</exception>
    public async Task<JobAssignmentMessage?> GetAssignmentAsync(string workItemId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItemId);

        var retryDelays = new[] { 2, 4, 8, 16, 32, 64 }; // seconds

        for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync($"/api/work-items/{workItemId}/assignment", ct);

                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        // Server returns WorkItemAssignmentDto which has identical JSON property names to JobAssignmentMessage.
                        // System.Text.Json matches on property names, ignoring MessagePack [Key] attributes.
                        var message = await response.Content.ReadFromJsonAsync<JobAssignmentMessage>(JsonOptions, ct);
                        return message ?? throw new WorkItemFetchException("Response deserialized to null");

                    case HttpStatusCode.Gone:
                        // Work item already in terminal status — agent should exit 0
                        _logger.Information("Work item {WorkItemId} is in terminal status (410 Gone), exiting gracefully", workItemId);
                        return null;

                    case HttpStatusCode.NotFound:
                        throw new WorkItemFetchException($"Work item {workItemId} not found (404)");

                    default:
                        if ((int)response.StatusCode >= 500)
                        {
                            _logger.Warning("GET assignment returned {StatusCode} on attempt {Attempt}, will retry",
                                (int)response.StatusCode, attempt + 1);
                            break;
                        }
                        throw new WorkItemFetchException(
                            $"Unexpected status {(int)response.StatusCode} from GET /api/work-items/{workItemId}/assignment");
                }
            }
            catch (HttpRequestException ex) when (attempt < retryDelays.Length)
            {
                _logger.Warning(ex, "GET assignment network error on attempt {Attempt}, will retry", attempt + 1);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex) when (attempt < retryDelays.Length)
            {
                _logger.Warning(ex, "GET assignment timeout on attempt {Attempt}, will retry", attempt + 1);
            }

            if (attempt < retryDelays.Length)
            {
                await Task.Delay(TimeSpan.FromSeconds(retryDelays[attempt]), ct);
            }
        }

        throw new WorkItemFetchException(
            $"All {retryDelays.Length + 1} attempts to GET /api/work-items/{workItemId}/assignment failed");
    }

    /// <summary>
    /// Posts a status transition to the orchestrator.
    /// Retries up to 3 times with exponential backoff (5s, 10s, 20s).
    /// </summary>
    /// <returns>True if the transition was accepted (200); false if rejected (400) or not found (404).</returns>
    /// <exception cref="WorkItemStatusPostException">Thrown when all retries are exhausted for a terminal status POST.</exception>
    public async Task<bool> PostStatusAsync(string workItemId, WorkItemStatusUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItemId);
        ArgumentNullException.ThrowIfNull(update);

        var retryDelays = new[] { 5, 10, 20 }; // seconds

        for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    $"/api/work-items/{workItemId}/status", update, JsonOptions, ct);

                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        _logger.Information("Posted status {Status} for work item {WorkItemId}",
                            update.Status, workItemId);
                        return true;

                    case HttpStatusCode.BadRequest:
                        _logger.Warning("Status transition to {Status} rejected (400) for work item {WorkItemId}",
                            update.Status, workItemId);
                        return false;

                    case HttpStatusCode.NotFound:
                        _logger.Warning("Work item {WorkItemId} not found (404) for status POST", workItemId);
                        return false;

                    default:
                        if ((int)response.StatusCode >= 500)
                        {
                            _logger.Warning("POST status returned {StatusCode} on attempt {Attempt}, will retry",
                                (int)response.StatusCode, attempt + 1);
                            break;
                        }
                        _logger.Error("Unexpected status {StatusCode} from POST /api/work-items/{WorkItemId}/status",
                            (int)response.StatusCode, workItemId);
                        return false;
                }
            }
            catch (HttpRequestException ex) when (attempt < retryDelays.Length)
            {
                _logger.Warning(ex, "POST status network error on attempt {Attempt}, will retry", attempt + 1);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex) when (attempt < retryDelays.Length)
            {
                _logger.Warning(ex, "POST status timeout on attempt {Attempt}, will retry", attempt + 1);
            }

            if (attempt < retryDelays.Length)
            {
                await Task.Delay(TimeSpan.FromSeconds(retryDelays[attempt]), ct);
            }
        }

        // All retries exhausted
        var errorMsg = $"All {retryDelays.Length + 1} attempts to POST status={update.Status} for work item {workItemId} failed";
        _logger.Error(errorMsg);
        throw new WorkItemStatusPostException(errorMsg);
    }
}

/// <summary>
/// DTO for POST /api/work-items/{id}/status request body.
/// </summary>
public sealed class WorkItemStatusUpdate
{
    public required string Status { get; init; }
    public string? AgentId { get; init; }
    public string? Result { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>
/// Thrown when the agent cannot fetch the work item assignment after all retries.
/// </summary>
public sealed class WorkItemFetchException : Exception
{
    public WorkItemFetchException(string message) : base(message) { }
    public WorkItemFetchException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when the agent cannot POST a terminal status after all retries.
/// ReconciliationService will detect the completed Job and reconcile the WorkItem status.
/// </summary>
public sealed class WorkItemStatusPostException : Exception
{
    public WorkItemStatusPostException(string message) : base(message) { }
    public WorkItemStatusPostException(string message, Exception inner) : base(message, inner) { }
}
