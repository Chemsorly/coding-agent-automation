using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Reusable stall detection for agent interactions. Wraps an <see cref="IAgentProvider.ExecuteAsync"/>
/// call with a background monitor that polls health status, logs silence warnings with phase context,
/// detects process death, and forcefully kills unresponsive agents after a hard timeout.
/// </summary>
internal static class AgentStallMonitor
{
    /// <summary>
    /// Executes an agent request with background stall monitoring.
    /// </summary>
    public static async Task<AgentResult> ExecuteWithMonitoringAsync(
        IAgentProvider agentProvider,
        AgentRequest request,
        PipelineRun run,
        PipelineConfiguration config,
        string phaseDescription,
        Action? onChange,
        Serilog.ILogger logger,
        CancellationToken ct,
        Action<string>? onOutputLine = null)
    {
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = RunMonitorLoopAsync(agentProvider, run, config, phaseDescription, onChange, logger, stallCts.Token);

        AgentResult result;
        try
        {
            result = await agentProvider.ExecuteAsync(request, ct, onOutputLine);
        }
        finally
        {
            await stallCts.CancelAsync();
            try { await monitorTask; } catch (OperationCanceledException) { }
        }

        return result;
    }

    /// <summary>
    /// Monitors an arbitrary async agent call (e.g., <see cref="IAgentProvider.EnsureSessionAsync"/>)
    /// that does not return an <see cref="AgentResult"/>.
    /// </summary>
    public static async Task MonitorAsync(
        IAgentProvider agentProvider,
        Func<Task> agentCall,
        PipelineRun run,
        PipelineConfiguration config,
        string phaseDescription,
        Action? onChange,
        Serilog.ILogger logger,
        CancellationToken ct)
    {
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = RunMonitorLoopAsync(agentProvider, run, config, phaseDescription, onChange, logger, stallCts.Token);

        try
        {
            await agentCall();
        }
        finally
        {
            await stallCts.CancelAsync();
            try { await monitorTask; } catch (OperationCanceledException) { }
        }
    }

    private static Task RunMonitorLoopAsync(
        IAgentProvider agentProvider,
        PipelineRun run,
        PipelineConfiguration config,
        string phaseDescription,
        Action? onChange,
        Serilog.ILogger logger,
        CancellationToken stallToken)
    {
        var killTimeout = config.AgentTimeout;

        return Task.Run(async () =>
        {
            try
            {
                var lastWarnTime = DateTime.UtcNow;

                while (!stallToken.IsCancellationRequested)
                {
                    await Task.Delay(config.StallPollInterval, stallToken);

                    AgentHealthStatus health;
                    try { health = agentProvider.GetHealthStatus(); }
                    catch (Exception ex)
                    {
                        logger.Warning(ex, "Pipeline {RunId} GetHealthStatus() call failed, continuing to poll", run.RunId);
                        continue;
                    }

                    if (health.IsProcessAlive == false)
                    {
                        var errorMsg = $"{phaseDescription} — agent process is no longer alive (PID {health.ProcessId}). " +
                                       $"Total elapsed: {(DateTime.UtcNow - run.StartedAt):hh\\:mm\\:ss}.";
                        logger.Error("Pipeline {RunId} {StallMessage}", run.RunId, errorMsg);
                        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = errorMsg });
                        onChange?.Invoke();
                        break;
                    }

                    // Determine silence duration: use LastOutputTime if available,
                    // otherwise fall back to run start time (no output received yet at all).
                    var referenceTime = health.LastOutputTime ?? run.StartedAt;
                    var silence = DateTime.UtcNow - referenceTime;

                    // Hard kill: silence exceeds kill timeout
                    if (silence >= killTimeout)
                    {
                        var killMsg = $"{phaseDescription} — no output for {silence.TotalMinutes:F0}m (kill timeout {killTimeout.TotalMinutes:F0}m). " +
                                      $"Forcefully terminating agent process.";
                        logger.Error("Pipeline {RunId} {StallMessage}", run.RunId, killMsg);
                        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = killMsg });
                        onChange?.Invoke();

                        try { await agentProvider.KillAsync(); }
                        catch (Exception ex) { logger.Warning(ex, "Pipeline {RunId} KillAsync() failed", run.RunId); }
                        break;
                    }

                    // Silence warning
                    var timeSinceLastWarn = DateTime.UtcNow - lastWarnTime;
                    if (silence >= config.StallWarningInterval && timeSinceLastWarn >= config.StallWarningInterval)
                    {
                        var elapsed = DateTime.UtcNow - run.StartedAt;
                        var statusDetail = health.SessionStatus is not null
                            ? $" Session status: {health.SessionStatus}."
                            : "";
                        var statusMsg = health.SessionStatusMessage is not null
                            ? $" Detail: {health.SessionStatusMessage}"
                            : "";
                        var msg = $"{phaseDescription} — no output for {silence.TotalMinutes:F0}m. " +
                                  $"Agent call still in progress. " +
                                  $"Total elapsed: {elapsed:hh\\:mm\\:ss}. Timeout: {config.AgentTimeout:hh\\:mm\\:ss}." +
                                  statusDetail + statusMsg;
                        logger.Warning("Pipeline {RunId} {StallMessage}", run.RunId, msg);
                        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = msg });
                        onChange?.Invoke();
                        lastWarnTime = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None);
    }
}
