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

                    if (!TryGetHealth(agentProvider, run, logger, out var health))
                        continue;

                    if (HandleProcessDeath(health!, run, phaseDescription, onChange, logger))
                        break;

                    var silence = ComputeSilence(health!, run);

                    if (await HandleKillTimeoutAsync(health!, silence, killTimeout, run, agentProvider, phaseDescription, onChange, logger))
                        break;

                    HandleSilenceWarning(health!, silence, config, run, phaseDescription, onChange, logger, ref lastWarnTime);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Calls <see cref="IAgentProvider.GetHealthStatus"/> safely.
    /// Returns false (and logs) when the call throws, allowing the monitor loop to continue.
    /// </summary>
    private static bool TryGetHealth(
        IAgentProvider agentProvider, PipelineRun run,
        Serilog.ILogger logger, out AgentHealthStatus? health)
    {
        try
        {
            health = agentProvider.GetHealthStatus();
            return true;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Pipeline {RunId} GetHealthStatus() call failed, continuing to poll", run.RunId);
            health = null;
            return false;
        }
    }

    /// <summary>
    /// Checks whether the agent process has died.
    /// Logs an error and notifies when true; returns true to break the monitor loop.
    /// </summary>
    private static bool HandleProcessDeath(
        AgentHealthStatus health, PipelineRun run,
        string phaseDescription, Action? onChange, Serilog.ILogger logger)
    {
        if (health.IsProcessAlive == false)
        {
            var errorMsg = $"{phaseDescription} — agent process is no longer alive (PID {health.ProcessId}). " +
                           $"Total elapsed: {(DateTimeOffset.UtcNow - run.StartedAtOffset):hh\\:mm\\:ss}.";
            logger.Error("Pipeline {RunId} {StallMessage}", run.RunId, errorMsg);
            run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = errorMsg });
            onChange?.Invoke();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Computes the silence duration using <see cref="AgentHealthStatus.LastOutputTime"/>
    /// or the run start time as a fallback.
    /// </summary>
    private static TimeSpan ComputeSilence(AgentHealthStatus health, PipelineRun run)
    {
        var referenceTime = health.LastOutputTime ?? run.StartedAtOffset.UtcDateTime;
        return DateTime.UtcNow - referenceTime;
    }

    /// <summary>
    /// Handles the hard-kill case when silence exceeds the kill timeout.
    /// Logs, notifies, kills the agent, and returns true to break the monitor loop.
    /// </summary>
    private static async Task<bool> HandleKillTimeoutAsync(
        AgentHealthStatus health, TimeSpan silence, TimeSpan killTimeout,
        PipelineRun run, IAgentProvider agentProvider,
        string phaseDescription, Action? onChange, Serilog.ILogger logger)
    {
        if (silence < killTimeout)
            return false;

        var killMsg = $"{phaseDescription} — no output for {silence.TotalMinutes:F0}m (kill timeout {killTimeout.TotalMinutes:F0}m). " +
                      $"Forcefully terminating agent process.";
        logger.Error("Pipeline {RunId} {StallMessage}", run.RunId, killMsg);
        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = killMsg });
        onChange?.Invoke();

        try { await agentProvider.KillAsync(); }
        catch (Exception ex) { logger.Warning(ex, "Pipeline {RunId} KillAsync() failed", run.RunId); }
        return true;
    }

    /// <summary>
    /// Emits a silence warning when the silence threshold is met and sufficient time has
    /// passed since the last warning. Updates <paramref name="lastWarnTime"/> on emit.
    /// </summary>
    private static void HandleSilenceWarning(
        AgentHealthStatus health, TimeSpan silence,
        PipelineConfiguration config, PipelineRun run,
        string phaseDescription, Action? onChange,
        Serilog.ILogger logger, ref DateTime lastWarnTime)
    {
        var timeSinceLastWarn = DateTime.UtcNow - lastWarnTime;
        if (silence < config.StallWarningInterval || timeSinceLastWarn < config.StallWarningInterval)
            return;

        var elapsed = DateTimeOffset.UtcNow - run.StartedAtOffset;
        var statusDetail = health.SessionStatus is not null ? $" Session status: {health.SessionStatus}." : "";
        var statusMsg = health.SessionStatusMessage is not null ? $" Detail: {health.SessionStatusMessage}" : "";
        var sessionsSummary = health.AllSessionsSummary is not null ? $" Sessions: [{health.AllSessionsSummary}]" : "";
        var msg = $"{phaseDescription} — no output for {silence.TotalMinutes:F0}m. " +
                  $"Agent call still in progress. " +
                  $"Total elapsed: {elapsed:hh\\:mm\\:ss}. Timeout: {config.AgentTimeout:hh\\:mm\\:ss}." +
                  statusDetail + statusMsg + sessionsSummary;
        logger.Warning("Pipeline {RunId} {StallMessage}", run.RunId, msg);
        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = msg });
        onChange?.Invoke();
        lastWarnTime = DateTime.UtcNow;
    }
}
