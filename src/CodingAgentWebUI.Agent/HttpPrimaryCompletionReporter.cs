using System.Text.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Reports job completion via HTTP POST (primary, durable) and SignalR (secondary, real-time).
/// Used in K8s mode (<see cref="WorkItemAgentService"/>) where HTTP is the reliable channel
/// and SignalR provides real-time notification to the UI.
/// </summary>
/// <remarks>
/// <para>HTTP POST is the primary channel: if it fails, the job is considered failed.
/// SignalR is the secondary channel: if it fails, it's logged as a warning (non-fatal).</para>
/// </remarks>
public sealed class HttpPrimaryCompletionReporter : IJobCompletionReporter
{
    private readonly string _workItemId;
    private readonly IWorkItemLifecycleClient _lifecycleClient;
    private readonly IAgentConnectionManager _connectionManager;
    private readonly AgentId _agentId;
    private readonly Serilog.ILogger _logger;

    public HttpPrimaryCompletionReporter(
        string workItemId,
        IWorkItemLifecycleClient lifecycleClient,
        IAgentConnectionManager connectionManager,
        AgentId agentId,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workItemId);
        ArgumentNullException.ThrowIfNull(lifecycleClient);
        ArgumentNullException.ThrowIfNull(connectionManager);
        ArgumentNullException.ThrowIfNull(logger);

        _workItemId = workItemId;
        _lifecycleClient = lifecycleClient;
        _connectionManager = connectionManager;
        // TODO: Validate agentId.Value is not null/empty — default(AgentId) would propagate null.
        _agentId = agentId;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task ReportCompletionAsync(JobId jobId, JobCompletionPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        // TODO: [WARNING] Add ArgumentException.ThrowIfNullOrEmpty(jobId.Value) guard here.
        // ThrowIfNull(jobId) was removed because JobId is a value type, but default(JobId)
        // (where Value is null) can still be passed. If that occurs, InvokeAsync below will
        // forward a null jobId to the SignalR secondary channel, which will fail in
        // JobIdFormatter.Serialize with a MessagePackSerializationException (non-fatal here,
        // but confusing). A guard makes the contract explicit and gives a clear error message.
        // See: review-findings.md [WARNING] HttpPrimaryCompletionReporter.cs:47

        // Primary channel: HTTP POST terminal status (durable)
        var terminalStatus = payload.FinalStep switch
        {
            PipelineStep.Completed => "Succeeded",
            PipelineStep.Cancelled => "Cancelled",
            _ => "Failed"
        };

        var terminalUpdate = new WorkItemStatusUpdate
        {
            Status = terminalStatus,
            AgentId = _agentId.Value,
            Result = SerializeResult(payload),
            ErrorMessage = payload.FailureReason,
            FailureReason = terminalStatus == "Failed"
                ? (payload.FailureCategory?.ToString() ?? nameof(Pipeline.Models.FailureReason.AgentError))
                : null
        };

        await _lifecycleClient.PostStatusAsync(_workItemId, terminalUpdate, CancellationToken.None);

        // Secondary channel: SignalR notification (real-time, non-fatal failure)
        try
        {
            await _connectionManager.InvokeAsync(
                (conn, token) => conn.InvokeAsync(HubMethodNames.ReportJobCompleted, jobId, payload, token),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to report completion via SignalR (non-fatal, HTTP status already posted)");
        }
    }

    private string? SerializeResult(JobCompletionPayload? completion)
    {
        if (completion is null) return null;
        try
        {
            return JsonSerializer.Serialize(completion, PipelineJsonOptions.Default);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to serialize JobCompletionPayload — result field will be omitted from terminal status");
            return null;
        }
    }
}
