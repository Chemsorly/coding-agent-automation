using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

// WorkItemStatusUpdate moved to CodingAgentWebUI.Pipeline.Models

namespace CodingAgentWebUI.Agent;

/// <summary>
/// HTTP client for the orchestrator's Work Item API endpoints.
/// Used in K8s mode to fetch assignments and report status transitions.
/// </summary>
/// <remarks>
/// <para>Resilience (retries, circuit breaker, timeouts) is handled by the
/// <c>AddStandardResilienceHandler()</c> configured at the DI registration level.</para>
/// <para><b>GET /api/work-items/{id}/assignment</b> — single call; transient failures retried by handler.</para>
/// <para><b>POST /api/work-items/{id}/status</b> — single call; transient failures retried by handler.</para>
/// </remarks>
public sealed class WorkItemHttpClient : IWorkItemLifecycleClient
{
    private readonly HttpClient _httpClient;
    private readonly Serilog.ILogger _logger;

    /// <summary>
    /// When set, appended as ?agentId= query param to GetAssignment and PostStatus calls.
    /// Null = no query param (agent authenticates with master key, no per-agent derivation).
    /// </summary>
    internal string? AgentId { get; set; }

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
    /// Transient failures (5xx, network errors) are retried transparently by the resilience handler.
    /// </summary>
    /// <returns>
    /// The deserialized <see cref="JobAssignmentMessage"/>, or null if the work item
    /// is in a terminal status (410 Gone).
    /// </returns>
    /// <exception cref="WorkItemFetchException">Thrown when a non-retryable error occurs or all retries are exhausted.</exception>
    public async Task<JobAssignmentMessage?> GetAssignmentAsync(string workItemId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItemId);

        HttpResponseMessage response;
        try
        {
            var url = string.IsNullOrEmpty(AgentId)
                ? $"/api/work-items/{workItemId}/assignment"
                : $"/api/work-items/{workItemId}/assignment?agentId={Uri.EscapeDataString(AgentId)}";
            // TODO: HttpRequestMessage is IDisposable; use `using var request` to ensure deterministic
            // disposal under exception paths (e.g. TimeoutRejectedException before SendAsync completes).
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            InjectTraceContext(request);
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Resilience handler exhausted retries (TimeoutRejectedException, HttpRequestException, etc.)
            _logger.Error(ex, "All retries exhausted for GET /api/work-items/{WorkItemId}/assignment", workItemId);
            throw new WorkItemFetchException(
                $"All retries exhausted for GET /api/work-items/{workItemId}/assignment: {ex.Message}", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var message = await response.Content.ReadFromJsonAsync<JobAssignmentMessage>(JsonOptions, ct);
                    if (message is null)
                    {
                        _logger.Error("GET /api/work-items/{WorkItemId}/assignment returned 200 but deserialized to null", workItemId);
                        throw new WorkItemFetchException("Response deserialized to null");
                    }
                    return message;

                case HttpStatusCode.Gone:
                    _logger.Information("Work item {WorkItemId} is in terminal status (410 Gone), exiting gracefully", workItemId);
                    return null;

                case HttpStatusCode.NotFound:
                    _logger.Error("Work item {WorkItemId} not found (404) for assignment fetch", workItemId);
                    throw new WorkItemFetchException($"Work item {workItemId} not found (404)");

                default:
                    // TODO: Add explicit >= 500 check with "retries exhausted" message for consistency with PostStatusAsync
                    _logger.Error("Unexpected status {StatusCode} from GET /api/work-items/{WorkItemId}/assignment", (int)response.StatusCode, workItemId);
                    throw new WorkItemFetchException(
                        $"Unexpected status {(int)response.StatusCode} from GET /api/work-items/{workItemId}/assignment");
            }
        }
    }

    /// <summary>
    /// Posts a status transition to the orchestrator.
    /// Transient failures (5xx, network errors) are retried transparently by the resilience handler.
    /// </summary>
    /// <returns>True if the transition was accepted (200); false if rejected (400) or not found (404).</returns>
    /// <exception cref="WorkItemStatusPostException">Thrown when all retries are exhausted.</exception>
    public async Task<bool> PostStatusAsync(string workItemId, WorkItemStatusUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItemId);
        ArgumentNullException.ThrowIfNull(update);

        HttpResponseMessage response;
        try
        {
            var url = string.IsNullOrEmpty(AgentId)
                ? $"/api/work-items/{workItemId}/status"
                : $"/api/work-items/{workItemId}/status?agentId={Uri.EscapeDataString(AgentId)}";
            // TODO: HttpRequestMessage (and its JsonContent) is IDisposable; use `using var request` to
            // ensure deterministic disposal of the internal MemoryStream under exception paths.
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(update, options: JsonOptions)
            };
            InjectTraceContext(request);
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Resilience handler exhausted retries (TimeoutRejectedException, HttpRequestException, etc.)
            _logger.Error(ex, "All retries exhausted for POST status={Status} for work item {WorkItemId}", update.Status, workItemId);
            throw new WorkItemStatusPostException(
                $"All retries exhausted for POST status={update.Status} for work item {workItemId}: {ex.Message}", ex);
        }

        using (response)
        {
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
                        // 5xx leaked through after resilience handler exhaustion
                        _logger.Error("Server error {StatusCode} from POST status={Status} for work item {WorkItemId} (retries exhausted)",
                            (int)response.StatusCode, update.Status, workItemId);
                        throw new WorkItemStatusPostException(
                            $"Server error {(int)response.StatusCode} from POST status={update.Status} for work item {workItemId} (retries exhausted)");
                    }
                    _logger.Error("Unexpected status {StatusCode} from POST /api/work-items/{WorkItemId}/status",
                        (int)response.StatusCode, workItemId);
                    return false;
            }
        }
    }

    /// <summary>
    /// Injects the W3C <c>traceparent</c> header from <see cref="Activity.Current"/> into the request.
    /// No-op when there is no ambient activity (old agent pods, OTel disabled) — backward-compatible by design.
    /// The API's ASP.NET Core OTel HTTP server instrumentation extracts the header automatically,
    /// making the resulting API span a child of the agent's <c>WorkItemAgent.Execute</c> span.
    /// </summary>
    // TODO: The issue AC also names PostLabelSwapAsync as requiring traceparent injection. That method does
    // not exist on this client — agent-side label swaps route via OrchestratorProxy over SignalR, not HTTP.
    // If a PostLabelSwapAsync HTTP method is ever added here, call InjectTraceContext on its request too.
    private static void InjectTraceContext(HttpRequestMessage request)
    {
        var traceparent = PipelineTelemetry.FormatTraceParent(Activity.Current);
        if (traceparent is not null)
            request.Headers.TryAddWithoutValidation("traceparent", traceparent);
    }
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
