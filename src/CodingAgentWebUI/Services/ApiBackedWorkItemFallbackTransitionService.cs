using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// <see cref="IWorkItemFallbackTransitionService"/> implementation for the orchestrator process
/// that routes WorkItem status transitions through the Pipeline API HTTP endpoint instead of
/// accessing Postgres directly. Removes the last direct-DB write path from the orchestrator (T8 item 3).
/// </summary>
internal sealed class ApiBackedWorkItemFallbackTransitionService : IWorkItemFallbackTransitionService
{
    private readonly IPipelineApiWorkItemClient _client;
    private readonly ILogger _logger;

    public ApiBackedWorkItemFallbackTransitionService(IPipelineApiWorkItemClient client, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryFallbackChainAsync(
        Guid workItemId,
        WorkItemStatus status,
        string? errorMessage,
        FailureReason? failureReason,
        CancellationToken ct)
    {
        try
        {
            var update = new WorkItemStatusUpdate
            {
                Status = status.ToString(),
                ErrorMessage = errorMessage,
                FailureReason = failureReason?.ToString()
            };

            await _client.PostStatusAsync(workItemId, update, ct);
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // 400 = transition rejected (already terminal or invalid transition) — not an error
            _logger.Debug(ex,
                "ApiBackedWorkItemFallbackTransitionService: WorkItem {WorkItemId} → {Status} rejected (already terminal or invalid)",
                workItemId, status);
            return false;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 404 = work item doesn't exist in the API — can happen for test/legacy runs
            _logger.Debug(ex,
                "ApiBackedWorkItemFallbackTransitionService: WorkItem {WorkItemId} not found (404), skipping transition to {Status}",
                workItemId, status);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex,
                "ApiBackedWorkItemFallbackTransitionService: WorkItem {WorkItemId} transition to {Status} failed — rethrowing for caller retry",
                workItemId, status);
            throw;
        }
    }
}
