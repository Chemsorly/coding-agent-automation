using Serilog;

namespace KiroCliLib.Core;

/// <summary>
/// Provides a standardized pattern for graceful shutdown: cancel a CancellationTokenSource,
/// await the associated task with a timeout, and log a warning if the timeout is exceeded.
/// </summary>
public static class GracefulShutdownHelper
{
    /// <summary>
    /// Cancels the given <paramref name="cts"/> and waits for <paramref name="task"/> to complete
    /// within the specified <paramref name="timeout"/>. Logs a warning if the task does not
    /// complete in time. Catches <see cref="OperationCanceledException"/> as expected during shutdown.
    /// </summary>
    /// <param name="cts">The cancellation token source to cancel. May be null.</param>
    /// <param name="task">The task to await. If null, returns immediately.</param>
    /// <param name="timeout">Maximum time to wait for the task to complete.</param>
    /// <param name="logger">Logger for timeout warnings.</param>
    /// <param name="operationName">Descriptive name of the operation for log messages.</param>
    public static async Task CancelAndWaitAsync(
        CancellationTokenSource? cts,
        Task? task,
        TimeSpan timeout,
        ILogger logger,
        string operationName)
    {
        if (task is null) return;

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        try
        {
            await task.WaitAsync(timeout, CancellationToken.None); // intentional: cts already cancelled above; timeout guards the wait
            // TODO: No test verifies that passing CancellationToken.None here (rather than cts.Token)
            // prevents an OperationCanceledException from surfacing when the CTS is already cancelled.
            // Consider a regression test that asserts CancelAndWaitAsync completes via timeout
            // (not via OperationCanceledException) when the CTS is cancelled before the task finishes.
        }
        catch (TimeoutException)
        {
            logger.Warning("{Operation} did not complete within {Timeout}", operationName, timeout);
        }
        catch (OperationCanceledException)
        {
            // Expected during graceful shutdown — the task responded to cancellation.
        }
    }
}
