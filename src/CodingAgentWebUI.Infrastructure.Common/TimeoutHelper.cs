namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Encapsulates the timeout-with-linked-CancellationTokenSource pattern:
/// create a linked CTS with timeout, execute work, and discriminate timeout
/// from caller cancellation via the <c>when</c> clause.
/// </summary>
public static class TimeoutHelper
{
    /// <summary>
    /// Executes <paramref name="work"/> with a timeout. If the timeout fires before work completes
    /// (and the caller's <paramref name="ct"/> has not been cancelled), invokes <paramref name="onTimeout"/>
    /// instead of propagating <see cref="OperationCanceledException"/>.
    /// Caller cancellation is always re-thrown.
    /// </summary>
    public static async Task<T> ExecuteWithTimeoutAsync<T>(
        TimeSpan timeout,
        CancellationToken ct,
        Func<CancellationToken, Task<T>> work,
        Func<Task<T>> onTimeout)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await work(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return await onTimeout();
        }
    }
}
