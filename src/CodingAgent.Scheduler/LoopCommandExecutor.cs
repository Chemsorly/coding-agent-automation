using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Scheduler;

/// <summary>
/// Shared executor for the three loop control commands.
/// Used by both the local (leader or no-Redis) path and by the leader-side
/// <see cref="Services.LoopCommandHandlerService"/> that runs commands on behalf of a
/// non-leader pod.
/// <para>
/// The stop command is intentionally split into two methods:
/// <see cref="ExecuteStopPersistAsync"/> persists the config flag (called locally, unconditionally)
/// and <see cref="ExecuteStopLoopOnlyAsync"/> calls <c>StopLoop()</c> (called on the leader only).
/// This guarantees <c>ClosedLoopAutoStart=false</c> is persisted even when the relay times out.
/// </para>
/// </summary>
public interface ILoopCommandExecutor
{
    /// <summary>
    /// Starts the loop and, on success, persists <c>ClosedLoopAutoStart=true</c>.
    /// </summary>
    Task<LoopStartResultDto> ExecuteStartAsync(
        IPipelineLoopService loopService,
        IPipelineApiConfigClient configClient,
        CancellationToken ct);

    /// <summary>
    /// Calls <c>StopLoop()</c> only — no config persistence.
    /// Must be preceded by <see cref="ExecuteStopPersistAsync"/> on the same request.
    /// </summary>
    Task ExecuteStopLoopOnlyAsync(IPipelineLoopService loopService);

    /// <summary>
    /// Persists <c>ClosedLoopAutoStart=false</c> to the shared config.
    /// Called locally (on the receiving pod) unconditionally before relaying the stop command,
    /// so the config flag is durable even when the leader does not confirm.
    /// </summary>
    Task ExecuteStopPersistAsync(IPipelineApiConfigClient configClient, CancellationToken ct);

    /// <summary>Calls <c>ResumeLoop()</c> on the given loop service.</summary>
    Task ExecuteResumeAsync(IPipelineLoopService loopService);
}

/// <inheritdoc cref="ILoopCommandExecutor"/>
public sealed class LoopCommandExecutor : ILoopCommandExecutor
{
    /// <inheritdoc/>
    public async Task<LoopStartResultDto> ExecuteStartAsync(
        IPipelineLoopService loopService,
        IPipelineApiConfigClient configClient,
        CancellationToken ct)
    {
        var started = await loopService.StartLoopAsync();
        if (started)
        {
            await configClient.UpdatePipelineConfigAsync(
                c => c with { ClosedLoopAutoStart = true }, ct);
        }

        string? error = started ? null : DescribeStartFailure(loopService);
        return new LoopStartResultDto(started, error);
    }

    /// <inheritdoc/>
    public Task ExecuteStopLoopOnlyAsync(IPipelineLoopService loopService)
    {
        loopService.StopLoop();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ExecuteStopPersistAsync(IPipelineApiConfigClient configClient, CancellationToken ct)
        => configClient.UpdatePipelineConfigAsync(
            c => c with { ClosedLoopAutoStart = false }, ct);

    /// <inheritdoc/>
    public Task ExecuteResumeAsync(IPipelineLoopService loopService)
    {
        loopService.ResumeLoop();
        return Task.CompletedTask;
    }

    private static string DescribeStartFailure(IPipelineLoopService loopService)
    {
        if (loopService.ValidationErrors.Count > 0)
            return "Loop failed to start due to validation errors.";
        if (loopService.IsLoopActive)
            return "Loop is already active.";
        return "A manual run is in progress. Wait for it to complete.";
    }
}
