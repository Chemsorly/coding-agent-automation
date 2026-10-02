namespace CodingAgent.Agent;

/// <summary>
/// Encapsulates chat slot state management and concurrency control for a chat-mode agent pod.
/// Owns the <c>_busyLock</c> and all mutable state related to the active chat session,
/// ensuring mutual exclusion between concurrent slot acquisition attempts.
/// </summary>
/// <remarks>
/// <para>
/// The agent serves one chat session at a time.
/// <see cref="TryAcquireChatSlot"/> enforces mutual exclusion under a single lock.
/// </para>
/// <para>
/// Thread safety: all state mutations happen under <see cref="_busyLock"/>. CTS disposal uses
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/> to prevent double-dispose races.
/// </para>
/// </remarks>
public sealed class ChatSlotManager
{
    private readonly object _busyLock = new();

    private volatile CancellationTokenSource? _chatCts;
    private Task? _activeChatTask;
    private string? _activeChatSessionId;

    /// <summary>The active chat session ID, or null if no chat is in progress.</summary>
    public string? ActiveChatSessionId
    {
        get { lock (_busyLock) { return _activeChatSessionId; } }
    }

    /// <summary>The active chat task, or null if no chat is running.</summary>
    public Task? ActiveChatTask => Volatile.Read(ref _activeChatTask);

    /// <summary>The chat cancellation token, or null if no chat is active.</summary>
    public CancellationToken? ChatCancellationToken
    {
        get
        {
            var cts = _chatCts;
            if (cts is null) return null;
            try { return cts.Token; }
            catch (ObjectDisposedException) { return null; }
        }
    }

    /// <summary>
    /// Cancels the currently running chat, if any.
    /// </summary>
    public void CancelCurrentChat()
    {
        var cts = _chatCts;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* Intentional: CTS already disposed (chat finished); cancellation is a no-op. */ }
    }

    /// <summary>
    /// Atomically verifies that the active chat session matches <paramref name="sessionId"/>
    /// and cancels it. Returns <c>true</c> if cancellation was performed, <c>false</c> if the
    /// session did not match (or no chat is active).
    /// </summary>
    /// <remarks>
    /// This eliminates the TOCTOU window where a caller could verify the session ID via
    /// <see cref="GetChatSlotSnapshot"/> and then call <see cref="CancelCurrentChat"/>, during
    /// which time the session could have been released and a new one acquired.
    /// </remarks>
    public bool CancelChatIfSession(string sessionId)
    {
        lock (_busyLock)
        {
            if (_activeChatSessionId != sessionId)
                return false;

            var cts = _chatCts;
            try { cts?.Cancel(); }
            catch (ObjectDisposedException) { /* Intentional: CTS already disposed (chat finished); cancellation is a no-op. */ }
            return true;
        }
    }

    /// <summary>
    /// Attempts to acquire the chat slot for the given session ID.
    /// Returns <c>false</c> if the agent is already busy with a chat session.
    /// </summary>
    /// <param name="sessionId">The chat session ID to acquire the slot for.</param>
    /// <param name="busyWith">If rejected, describes what the agent is busy with.</param>
    /// <returns><c>true</c> if the slot was acquired; <c>false</c> otherwise.</returns>
    public bool TryAcquireChatSlot(string sessionId, out string? busyWith)
    {
        // TODO [WARNING]: Add ArgumentNullException.ThrowIfNull(sessionId) guard here.
        // A null sessionId is stored in _activeChatSessionId and silently poisons the slot:
        // any subsequent call with a real session ID passes the `is not null` guard and
        // overwrites the null entry without detecting the prior state. Also, CancelChatIfSession
        // with a real ID never matches a null _activeChatSessionId, making the chat
        // un-cancellable by session. Null can arrive from a deserialized hub message with a
        // missing field.
        lock (_busyLock)
        {
            if (_activeChatSessionId is not null)
            {
                busyWith = $"chat:{_activeChatSessionId}";
                return false;
            }

            _activeChatSessionId = sessionId;
            _chatCts = new CancellationTokenSource();
            busyWith = null;
            return true;
        }
    }

    /// <summary>
    /// Sets the active chat task reference (for shutdown/cancel-wait patterns).
    /// </summary>
    public void SetActiveChatTask(Task task)
    {
        Volatile.Write(ref _activeChatTask, task);
    }

    /// <summary>
    /// Releases the chat slot and disposes the CTS.
    /// </summary>
    public void ReleaseChatSlot()
    {
        lock (_busyLock)
        {
            _activeChatSessionId = null;
        }

#pragma warning disable 0420 // volatile field passed by reference to Interlocked — safe by design
        var oldCts = Interlocked.Exchange(ref _chatCts, null);
#pragma warning restore 0420
        oldCts?.Dispose();
    }

    /// <summary>
    /// Returns an atomic snapshot of the chat slot state for cancel coordination.
    /// All fields are read under <see cref="_busyLock"/> to prevent TOCTOU races
    /// where ReleaseChatSlot() could clear state between individual property reads.
    /// </summary>
    public (string? SessionId, Task? Task) GetChatSlotSnapshot()
    {
        lock (_busyLock)
        {
            // TODO [WARNING]: _activeChatTask is not declared volatile. On weakly-ordered
            // architectures (ARM) a stale cached value could be returned here even while
            // holding _busyLock, because the lock only provides a fence at acquire/release —
            // not between the Interlocked.Exchange of _chatCts (done outside the lock in
            // ReleaseChatSlot) and this lock-protected read. Fix: use Volatile.Read(ref
            // _activeChatTask) here, consistent with how ActiveChatTask does it.
            return (_activeChatSessionId, _activeChatTask);
        }
    }
}
