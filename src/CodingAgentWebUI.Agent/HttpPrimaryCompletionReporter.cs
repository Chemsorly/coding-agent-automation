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
    private readonly AgentIdentity _agentIdentity;
    private readonly Serilog.ILogger _logger;

    public HttpPrimaryCompletionReporter(
        string workItemId,
        IWorkItemLifecycleClient lifecycleClient,
        IAgentConnectionManager connectionManager,
        AgentIdentity agentIdentity,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workItemId);
        ArgumentNullException.ThrowIfNull(lifecycleClient);
        ArgumentNullException.ThrowIfNull(connectionManager);
        ArgumentNullException.ThrowIfNull(agentIdentity);
        ArgumentNullException.ThrowIfNull(logger);

        _workItemId = workItemId;
        _lifecycleClient = lifecycleClient;
        _connectionManager = connectionManager;
        _agentIdentity = agentIdentity;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task ReportCompletionAsync(string jobId, JobCompletionPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(payload);

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
            AgentId = _agentIdentity.Id,
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
