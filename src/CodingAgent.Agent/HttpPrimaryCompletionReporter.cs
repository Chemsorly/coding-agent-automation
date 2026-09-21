using System.Text.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgent.Agent;

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

        // If the run created a branch, post Running+BranchName before the terminal status.
        // This populates WorkItems.BranchName in Postgres so GET /api/pipeline-runs/active-branches
        // can serve a DB-backed branch list that is immune to ghost runs caused by lost SignalR
        // completion signals. Must fire before the terminal POST: the terminal POST transitions
        // Status out of Running, after which a Running POST would be rejected (400).
        // Non-fatal: if rejected (e.g. item already terminal due to a race), log and continue.
        if (payload.BranchName is not null)
        {
            var branchUpdate = new WorkItemStatusUpdate
            {
                Status = "Running",
                AgentId = _agentId.Value,
                BranchName = payload.BranchName
            };
            // TODO: [WARNING] CancellationToken.None is passed here instead of the caller-supplied `ct`.
            // This is intentional (best-effort, non-fatal call) but means a hung intermediate POST cannot
            // be interrupted by agent shutdown, potentially delaying teardown. The resilience pipeline has
            // configured timeouts so the delay is bounded. Consider propagating `ct` here if tight shutdown
            // latency becomes a concern.
            var branchAccepted = await _lifecycleClient.PostStatusAsync(_workItemId, branchUpdate, CancellationToken.None);
            if (!branchAccepted)
                _logger.Warning(
                    "BranchName Running POST rejected for WorkItem {WorkItemId} — BranchName will not be persisted in DB",
                    _workItemId);
        }

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

        // TODO: CancellationToken.None is passed here (pre-existing pattern) instead of `ct`. If `ct` is cancelled
        // between the await and the if-branch, a cancellation-induced false return could be misinterpreted as a server
        // rejection and emit a spurious warning. Consider propagating `ct` here and to the secondary-channel call below.
        var accepted = await _lifecycleClient.PostStatusAsync(_workItemId, terminalUpdate, CancellationToken.None);
        if (!accepted)
        {
            _logger.Warning(
                "HttpPrimaryCompletionReporter: completion POST for WorkItem {WorkItemId} with status {Status} " +
                "— transition was rejected (WorkItem may already be in a terminal state or not found). Agent result not recorded.",
                _workItemId,
                terminalStatus);
        }

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
