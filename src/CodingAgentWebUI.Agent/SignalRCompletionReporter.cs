using System.Diagnostics;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR.Client;
using Polly;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Reports job completion via SignalR with Polly resilience. On failure, buffers the message
/// in <see cref="CriticalMessageBuffer"/> for replay after reconnection.
/// Used in SignalR mode (<see cref="AgentWorkerService"/>).
/// </summary>
public sealed class SignalRCompletionReporter : IJobCompletionReporter
{
    private readonly HubConnectionManager _hubManager;
    private readonly ResiliencePipeline _signalRPipeline;
    private readonly CriticalMessageBuffer _criticalMessageBuffer;
    private readonly Serilog.ILogger _logger;

    public SignalRCompletionReporter(
        HubConnectionManager hubManager,
        ResiliencePipeline signalRPipeline,
        CriticalMessageBuffer criticalMessageBuffer,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(hubManager);
        ArgumentNullException.ThrowIfNull(signalRPipeline);
        ArgumentNullException.ThrowIfNull(criticalMessageBuffer);
        ArgumentNullException.ThrowIfNull(logger);

        _hubManager = hubManager;
        _signalRPipeline = signalRPipeline;
        _criticalMessageBuffer = criticalMessageBuffer;
        _logger = logger;
    }

    /// <summary>
    /// Whether the buffer has pending messages awaiting replay (used by the service for slot management).
    /// </summary>
    public bool HasPendingMessages => _criticalMessageBuffer.HasPendingMessages;

    /// <summary>
    /// The underlying buffer for draining on reconnection.
    /// </summary>
    public CriticalMessageBuffer Buffer => _criticalMessageBuffer;

    /// <inheritdoc/>
    public async Task ReportCompletionAsync(string jobId, JobCompletionPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(payload);

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Agent.ReportCompletion");
        activity?.SetTag("job_id", jobId);
        activity?.SetTag("success", payload.FinalStep is not (PipelineStep.Failed or PipelineStep.Cancelled));

        try
        {
            await _signalRPipeline.ExecuteAsync(async token =>
                await _hubManager.Connection.InvokeAsync(
                    HubMethodNames.ReportJobCompleted, jobId, payload, token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Error(ex, "Failed to report job completion for {JobId}, buffering for replay", jobId);
            _criticalMessageBuffer.Enqueue(new BufferedJobCompleted(jobId, payload, DateTimeOffset.UtcNow));
        }
    }
}
