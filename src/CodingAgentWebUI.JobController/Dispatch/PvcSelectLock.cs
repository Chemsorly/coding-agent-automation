namespace CodingAgentWebUI.JobController.Dispatch;

/// <summary>
/// Process-wide mutex that serialises the "check available PVC → create K8s Job" critical
/// section across ALL dispatch loops running in this process.
///
/// <para>
/// Both <see cref="DispatchLoop"/> and <see cref="ConsolidationDispatchLoop"/> handle
/// kiro work items that require a PVC from the configured pool. Without a shared lock,
/// the two loops can observe the same free PVC concurrently and both issue a
/// <c>CreateJobAsync</c> call with the same PVC, causing a credential conflict at
/// runtime (TOCTOU race).
/// </para>
/// <para>
/// A single instance of this class is registered as a DI singleton and injected into
/// both loops, guaranteeing that the PVC-selection critical section is globally exclusive
/// within the process. This mirrors the <c>_pvcSelectLock</c> pattern used by
/// <c>DispatchLifecycleService</c> on the API side.
/// </para>
/// <para>
/// Note: this lock provides no cross-replica protection. The leader-election mechanism
/// (one active controller replica at a time) is the architectural guard for that scenario.
/// </para>
/// </summary>
public sealed class PvcSelectLock : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Asynchronously acquires the lock. Must be paired with a <c>finally { Release(); }</c> block.
    /// </summary>
    /// <param name="ct">Cancellation token. If cancelled before the lock is acquired, throws
    /// <see cref="OperationCanceledException"/> and does NOT acquire the semaphore — callers
    /// must NOT call <see cref="Release"/> in that case.</param>
    /// <returns>
    /// <c>true</c> if the semaphore was acquired. Always <c>true</c> on success; the method
    /// throws rather than returning <c>false</c>.
    /// </returns>
    public Task WaitAsync(CancellationToken ct) => _semaphore.WaitAsync(ct);

    /// <summary>
    /// Releases the lock. Must only be called after a successful <see cref="WaitAsync"/>
    /// (i.e. when <see cref="WaitAsync"/> returned without throwing).
    /// </summary>
    public void Release() => _semaphore.Release();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _semaphore.Dispose();
    }
}
