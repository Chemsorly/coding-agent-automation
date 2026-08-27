using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Handles consolidation job assignments and their lifecycle.
/// Extracted from <see cref="AgentWorkerService"/> to make consolidation logic independently testable.
/// </summary>
/// <remarks>
/// Receives job assignments via <see cref="AgentConnectionLifecycle"/> events wired in
/// <see cref="AgentWorkerService"/>. Uses <see cref="AgentJobSlotManager"/> for single-slot
/// concurrency control shared with pipeline and chat job handlers.
/// </remarks>
public sealed class ConsolidationJobHandler
{
    private readonly AgentConnectionLifecycle _connectionLifecycle;
    private readonly AgentJobSlotManager _slotManager;
    private readonly IConsolidationExecutor _consolidationExecutor;
    private readonly Serilog.ILogger _logger;

    public ConsolidationJobHandler(
        AgentConnectionLifecycle connectionLifecycle,
        AgentJobSlotManager slotManager,
        IConsolidationExecutor consolidationExecutor,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connectionLifecycle);
        ArgumentNullException.ThrowIfNull(slotManager);
        ArgumentNullException.ThrowIfNull(consolidationExecutor);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionLifecycle = connectionLifecycle;
        _slotManager = slotManager;
        _consolidationExecutor = consolidationExecutor;
        _logger = logger;
    }

    public async Task HandleAssignConsolidationJobAsync(ConsolidationJobMessage message)
    {
        PipelineTelemetry.AgentJobsReceived.Add(1);

        using var receiveActivity = PipelineTelemetry.ActivitySource.StartActivity("Agent.ReceiveJob");
        receiveActivity?.SetTag("job_id", message.JobId);
        receiveActivity?.SetTag("run_type", "consolidation");

        if (!_slotManager.TryAcquireJobSlot(message.JobId, out var busyWith))
        {
            await RejectConsolidationJobBusyAsync(message.JobId, busyWith, receiveActivity);
            return;
        }

        _logger.Information("Accepted consolidation job {JobId} of type {Type}",
            message.JobId, message.Type);

        if (_slotManager.JobCancellationToken is not { } jobToken)
        {
            _logger.Warning("JobCancellationToken is null after TryAcquireJobSlot for consolidation job {JobId} — releasing slot", message.JobId);
            await _slotManager.ReleaseJobSlotAndSignalReadyAsync();
            return;
        }
        var activeTask = Task.Run(async () => await RunConsolidationTaskAsync(message, jobToken), CancellationToken.None);
        _slotManager.SetActiveJobTask(activeTask);
    }

    internal async Task RejectConsolidationJobBusyAsync(string jobId, string? busyWith, Activity? activity)
    {
        PipelineTelemetry.AgentJobsRejected.Add(1,
            new KeyValuePair<string, object?>("reason", PipelineTelemetry.AgentRejectionReasons.Busy));
        _logger.Warning("Rejecting consolidation job {JobId} — agent is busy with {ActiveJobId}",
            jobId, busyWith);
        try
        {
            await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.JobRejected, jobId, "Agent is busy", CancellationToken.None);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Failed to notify orchestrator of consolidation job rejection {JobId}", jobId);
        }
    }

    public async Task RunConsolidationTaskAsync(ConsolidationJobMessage message, CancellationToken jobToken)
    {
        try
        {
            await _consolidationExecutor.ExecuteAsync(
                message, _connectionLifecycle.Connection, jobToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Consolidation job {JobId} failed with unhandled error", message.JobId);
            await ReportConsolidationFailureAsync(message.JobId, ex.Message);
        }
        finally
        {
            await _slotManager.ReleaseJobSlotAndSignalReadyAsync();
        }
    }

    public async Task ReportConsolidationFailureAsync(string jobId, string errorMessage)
    {
        try
        {
            var failResult = new ConsolidationJobResult
            {
                JobId = jobId,
                Success = false,
                ErrorMessage = errorMessage
            };
            await _connectionLifecycle.Connection.InvokeAsync(HubMethodNames.ReportConsolidationComplete, failResult,
                CancellationToken.None); // intentional: failure report must reach orchestrator even when jobToken is cancelled
        }
        catch (Exception reportEx)
        {
            _logger.Error(reportEx, "Failed to report consolidation failure for job {JobId}", jobId);
        }
    }
}
