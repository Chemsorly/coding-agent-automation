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
    private readonly IHubConnectionManager _hubManager;
    private readonly ResiliencePipeline _signalRPipeline;
    private readonly CriticalMessageBuffer _criticalMessageBuffer;
    private readonly Serilog.ILogger _logger;

    public SignalRCompletionReporter(
        IHubConnectionManager hubManager,
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
    public async Task ReportCompletionAsync(JobId jobId, JobCompletionPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        // TODO: [WARNING] Add ArgumentException.ThrowIfNullOrEmpty(jobId.Value) guard here.
        // ThrowIfNull(jobId) was removed because JobId is a value type, but default(JobId)
        // (where Value is null) can still be passed. If that occurs, jobId.Value is null and
        // activity?.SetTag("job_id", jobId.Value) sets a null tag, _logger.Error logs a null
        // job ID, and _criticalMessageBuffer.Enqueue stores a null JobId in BufferedJobCompleted,
        // silently corrupting the replay buffer. A guard makes the contract explicit.
        // See: review-findings.md [WARNING] SignalRCompletionReporter.cs:59

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Agent.ReportCompletion");
        activity?.SetTag("job_id", jobId.Value);
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
            _logger.Error(ex, "Failed to report job completion for {JobId}, buffering for replay", jobId.Value);
            _criticalMessageBuffer.Enqueue(new BufferedJobCompleted(jobId.Value, payload, DateTimeOffset.UtcNow));
        }
    }
}
