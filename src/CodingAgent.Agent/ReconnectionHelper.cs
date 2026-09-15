namespace CodingAgent.Agent;

internal static class ReconnectionHelper
{
    internal static TimeSpan CalculateReconnectionDelay(int attempt)
    {
        var baseSeconds = Math.Min(Math.Pow(2, attempt), 120);
        var jitter = Random.Shared.NextDouble(); // 0–1s
        return TimeSpan.FromSeconds(baseSeconds + jitter);
    }

    /// <summary>
    /// Waits for <paramref name="gateTask"/> to complete, or times out after
    /// <see cref="Infrastructure.Resilience.ResiliencePipelineFactory.SignalRTimeout"/>.
    /// The caller-supplied <paramref name="ct"/> is intentionally not raced here
    /// (see TODO in callers); the timeout is the only unblocking path besides gate completion.
    /// </summary>
    internal static async Task WaitWithTimeoutAsync(
        Task gateTask,
        CancellationToken ct,
        string agentId,
        Serilog.ILogger logger)
    {
        // CTS is created inside the async method so its lifetime is tied to the async
        // state machine rather than the synchronous call frame — this prevents the
        // `using var` early-dispose bug that plagued the previous synchronous wrapper.
        using var timeoutCts = new CancellationTokenSource(Infrastructure.Resilience.ResiliencePipelineFactory.SignalRTimeout);
        // TODO [WARNING]: The caller-supplied `ct` is not included in Task.WhenAny below.
        // Cancelling `ct` (e.g. ApplicationStopping) will not promptly unblock this wait;
        // the gate or the 30-second timeout must fire first. Fix: add a Task.Delay(Timeout.Infinite, ct)
        // competitor to the WhenAny call, and catch OperationCanceledException from it.
        var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
        try
        {
            var completedTask = await Task.WhenAny(gateTask, timeoutTask);
            if (completedTask != gateTask)
            {
                logger.Warning(
                    "Agent {AgentId}: registration gate wait timed out after {Timeout}s — proceeding anyway",
                    agentId, Infrastructure.Resilience.ResiliencePipelineFactory.SignalRTimeout.TotalSeconds);
            }
        }
        catch (OperationCanceledException)
        {
            logger.Warning(
                "Agent {AgentId}: registration gate wait cancelled — proceeding anyway",
                agentId);
        }
        finally
        {
            // Cancel the timeout so the Task.Delay timer is released immediately
            await timeoutCts.CancelAsync();
        }
    }
}