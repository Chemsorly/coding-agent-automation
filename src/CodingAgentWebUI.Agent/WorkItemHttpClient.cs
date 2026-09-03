using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

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
/// <para><b>Traceparent propagation</b> — W3C <c>traceparent</c> is injected from <see cref="Activity.Current"/>
/// into every outgoing request header so that API handler spans appear as children of the agent's
/// <c>WorkItemAgent.Execute</c> span in Grafana Tempo.</para>
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

    /// <summary>
    /// W3C trace context propagator used to inject <c>traceparent</c> and <c>tracestate</c>
    /// headers into outgoing orchestration-channel HTTP requests.
    /// </summary>
    private static readonly TraceContextPropagator TraceContextPropagator = new();

    public WorkItemHttpClient(HttpClient httpClient, Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Injects the current W3C trace context (<c>traceparent</c> and, when present,
    /// <c>tracestate</c>) from <see cref="Activity.Current"/> into the request headers.
    /// No-op when there is no ambient activity (graceful backward-compat: API endpoints
    /// that receive no <c>traceparent</c> start a new root span rather than failing).
    /// </summary>
    // TODO [WARNING]: InjectTraceContext reads Activity.Current at call time. Activity.Current is
    // AsyncLocal-backed, so its value on a thread pool continuation may differ from the activity
    // that was current when the async method was entered. In all three callers (GetAssignmentAsync,
    // PostStatusAsync, PostLabelSwapAsync) this method is called synchronously before the first
    // await, which is correct. If this method is ever moved to after an await boundary (e.g., inside
    // a retry callback), it could silently inject null or the wrong context without a compiler error.
    private static void InjectTraceContext(HttpRequestMessage request)
    {
        if (Activity.Current is null)
            return;

        TraceContextPropagator.Inject(
            new PropagationContext(Activity.Current.Context, Baggage.Current),
            request,
            static (req, key, value) => req.Headers.TryAddWithoutValidation(key, value));
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

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
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

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = JsonContent.Create(update, options: JsonOptions);
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
    /// Posts a label-swap request to the orchestrator for the given work item.
    /// The API uses the <paramref name="label"/> field for wire compatibility but always swaps to agent:in-progress.
    /// Transient failures (5xx, network errors) are retried transparently by the resilience handler.
    /// </summary>
    /// <returns>True if accepted (200); false if the work item was not found (404).</returns>
    /// <exception cref="WorkItemLabelSwapException">Thrown when all retries are exhausted.</exception>
    public async Task<bool> PostLabelSwapAsync(string workItemId, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workItemId);
        ArgumentNullException.ThrowIfNull(label);

        HttpResponseMessage response;
        try
        {
            var url = string.IsNullOrEmpty(AgentId)
                ? $"/api/work-items/{workItemId}/label-swap"
                : $"/api/work-items/{workItemId}/label-swap?agentId={Uri.EscapeDataString(AgentId)}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = JsonContent.Create(new { label }, options: JsonOptions);
            InjectTraceContext(request);
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "All retries exhausted for POST label-swap for work item {WorkItemId}", workItemId);
            throw new WorkItemLabelSwapException(
                $"All retries exhausted for POST label-swap for work item {workItemId}: {ex.Message}", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    _logger.Information("Posted label-swap for work item {WorkItemId}", workItemId);
                    return true;

                case HttpStatusCode.NotFound:
                    _logger.Warning("Work item {WorkItemId} not found (404) for label-swap POST", workItemId);
                    return false;

                default:
                    if ((int)response.StatusCode >= 500)
                    {
                        _logger.Error("Server error {StatusCode} from POST label-swap for work item {WorkItemId} (retries exhausted)",
                            (int)response.StatusCode, workItemId);
                        throw new WorkItemLabelSwapException(
                            $"Server error {(int)response.StatusCode} from POST label-swap for work item {workItemId} (retries exhausted)");
                    }
                    _logger.Error("Unexpected status {StatusCode} from POST /api/work-items/{WorkItemId}/label-swap",
                        (int)response.StatusCode, workItemId);
                    return false;
            }
        }
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

/// <summary>
/// Thrown when the agent cannot POST a label-swap request after all retries.
/// </summary>
public sealed class WorkItemLabelSwapException : Exception
{
    public WorkItemLabelSwapException(string message) : base(message) { }
    public WorkItemLabelSwapException(string message, Exception inner) : base(message, inner) { }
}
