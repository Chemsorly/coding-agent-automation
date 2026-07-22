using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Encapsulates job slot state management and concurrency control for a single-slot agent.
/// Owns the <c>_busyLock</c> and all mutable state related to active job/chat tracking,
/// ensuring mutual exclusion between concurrent slot acquisition attempts.
/// </summary>
/// <remarks>
/// <para>
/// The agent supports one active task at a time — either a pipeline job or a chat session.
/// <see cref="TryAcquireJobSlot"/> and <see cref="TryAcquireChatSlot"/> enforce mutual exclusion
/// under a single lock. <see cref="ReleaseJobSlotAndSignalReadyAsync"/> clears state and invokes
/// the <c>signalReady</c> callback to notify the orchestrator.
/// </para>
/// <para>
/// Thread safety: all state mutations happen under <see cref="_busyLock"/>. CTS disposal uses
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/> to prevent double-dispose races.
/// </para>
/// </remarks>
public sealed class AgentJobSlotManager
{
    private readonly object _busyLock = new();
    private readonly Func<Task> _signalReady;

    private volatile CancellationTokenSource? _jobCts;
    private Task? _activeJobTask;
    private volatile string? _activeJobId;
    private JobAssignmentMessage? _activeJobAssignment;
    private DateTimeOffset? _activeJobStartedAt;
    private PipelineRunType _activeJobRunType;
    private PipelineStep? _currentStep;

    private volatile CancellationTokenSource? _chatCts;
    private Task? _activeChatTask;
    private string? _activeChatSessionId;

    /// <summary>
    /// Creates a new <see cref="AgentJobSlotManager"/>.
    /// </summary>
    /// <param name="signalReady">
    /// Callback invoked after a job slot is released to notify the orchestrator that the agent
    /// is ready for new work. Typically sends an <c>AgentReady</c> hub message.
    /// </param>
    public AgentJobSlotManager(Func<Task> signalReady)
    {
        ArgumentNullException.ThrowIfNull(signalReady);
        _signalReady = signalReady;
    }

    // TODO: Inconsistent thread-safety contract — ActiveChatSessionId acquires _busyLock for its
    // read, but ActiveJobId, IsBusy, and CurrentStep do not. Consider making all property reads
    // consistently use locks (or volatile) to establish a clear thread-safety contract for callers.
    /// <summary>Whether the agent is currently executing a job.</summary>
    public bool IsBusy => _activeJobId is not null;

    /// <summary>The current pipeline step being executed, or null if idle.</summary>
    public PipelineStep? CurrentStep => _currentStep;

    /// <summary>The active job ID, or null if idle.</summary>
    public string? ActiveJobId => _activeJobId;

    /// <summary>The active chat session ID, or null if no chat is in progress.</summary>
    public string? ActiveChatSessionId
    {
        get { lock (_busyLock) { return _activeChatSessionId; } }
    }

    /// <summary>The active job task, or null if no job is running.</summary>
    public Task? ActiveJobTask => Volatile.Read(ref _activeJobTask);

    /// <summary>The active chat task, or null if no chat is running.</summary>
    public Task? ActiveChatTask => Volatile.Read(ref _activeChatTask);

    // TODO: JobCancellationToken can throw ObjectDisposedException if ForceReleaseJobSlot() disposes
    // the CTS between the null-check and the .Token access. Current call sites read the token
    // immediately after slot acquisition (before any release path is reachable), but the public API
    // contract doesn't enforce this ordering. Consider capturing the reference locally and wrapping
    // in try/catch like the Cancel methods do, or documenting the precondition.
    /// <summary>The job cancellation token, or null if no job is active.</summary>
    public CancellationToken? JobCancellationToken => _jobCts?.Token;

    // TODO: Same race condition as JobCancellationToken — ReleaseChatSlot() uses
    // Interlocked.Exchange(ref _chatCts, null)?.Dispose() which can dispose the CTS between the
    // null-conditional evaluation and the .Token property access. Consider the same fix.
    /// <summary>The chat cancellation token, or null if no chat is active.</summary>
    public CancellationToken? ChatCancellationToken => _chatCts?.Token;

    /// <summary>
    /// Cancels the currently running job, if any.
    /// </summary>
    public void CancelCurrentJob()
    {
        var cts = _jobCts;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Cancels the currently running chat, if any.
    /// </summary>
    public void CancelCurrentChat()
    {
        var cts = _chatCts;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
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
            catch (ObjectDisposedException) { }
            return true;
        }
    }

    /// <summary>
    /// Attempts to acquire the job slot for the given job ID.
    /// Returns <c>false</c> if the agent is already busy (with a job or chat).
    /// </summary>
    /// <param name="jobId">The job ID to acquire the slot for.</param>
    /// <param name="busyWith">If rejected, describes what the agent is busy with.</param>
    /// <returns><c>true</c> if the slot was acquired; <c>false</c> otherwise.</returns>
    public bool TryAcquireJobSlot(string jobId, out string? busyWith)
    {
        lock (_busyLock)
        {
            if (_activeJobId is not null || _activeChatSessionId is not null)
            {
                busyWith = _activeJobId ?? $"chat:{_activeChatSessionId}";
                return false;
            }

            _activeJobId = jobId;
            _activeJobStartedAt = DateTimeOffset.UtcNow;
            _jobCts = new CancellationTokenSource();
            busyWith = null;
            return true;
        }
    }

    /// <summary>
    /// Attempts to acquire the chat slot for the given session ID.
    /// Returns <c>false</c> if the agent is already busy (with a job or chat).
    /// </summary>
    /// <param name="sessionId">The chat session ID to acquire the slot for.</param>
    /// <param name="busyWith">If rejected, describes what the agent is busy with.</param>
    /// <returns><c>true</c> if the slot was acquired; <c>false</c> otherwise.</returns>
    public bool TryAcquireChatSlot(string sessionId, out string? busyWith)
    {
        lock (_busyLock)
        {
            if (_activeJobId is not null || _activeChatSessionId is not null)
            {
                busyWith = _activeJobId ?? $"chat:{_activeChatSessionId}";
                return false;
            }

            _activeChatSessionId = sessionId;
            _chatCts = new CancellationTokenSource();
            busyWith = null;
            return true;
        }
    }

    /// <summary>
    /// Sets the active job assignment metadata after slot acquisition.
    /// </summary>
    public void SetActiveJobAssignment(JobAssignmentMessage message, PipelineRunType runType)
    {
        lock (_busyLock)
        {
            _activeJobAssignment = message;
            _activeJobRunType = runType;
        }
    }

    /// <summary>
    /// Sets the active job task reference (for shutdown/cancel-wait patterns).
    /// </summary>
    public void SetActiveJobTask(Task task)
    {
        Volatile.Write(ref _activeJobTask, task);
    }

    /// <summary>
    /// Sets the active chat task reference (for shutdown/cancel-wait patterns).
    /// </summary>
    public void SetActiveChatTask(Task task)
    {
        Volatile.Write(ref _activeChatTask, task);
    }

    /// <summary>
    /// Updates the current pipeline step being executed.
    /// </summary>
    public void SetCurrentStep(PipelineStep? step)
    {
        _currentStep = step;
    }

    /// <summary>
    /// Releases the job slot, disposes the CTS, and signals the orchestrator that the agent is ready.
    /// </summary>
    public async Task ReleaseJobSlotAndSignalReadyAsync()
    {
        lock (_busyLock)
        {
            _activeJobId = null;
            _activeJobAssignment = null;
            _activeJobStartedAt = null;
            _activeJobRunType = default;
            _currentStep = null;
        }

#pragma warning disable 0420 // volatile field passed by reference to Interlocked — safe by design
        var oldCts = Interlocked.Exchange(ref _jobCts, null);
#pragma warning restore 0420
        oldCts?.Dispose();

        await _signalReady();
    }

    /// <summary>
    /// Releases the chat slot and disposes the CTS.
    /// Does NOT signal ready — chat sessions signal ready on CancelChat.
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
            return (_activeChatSessionId, _activeChatTask);
        }
    }

    /// <summary>
    /// Clears the job slot without signaling ready or disposing CTS.
    /// Used when JobAccepted fails and the slot must be released immediately.
    /// </summary>
    // TODO: No test verifies that ForceReleaseJobSlot does NOT invoke the signalReady callback,
    // which is the key behavioral difference between it and ReleaseJobSlotAndSignalReadyAsync.
    // Add a negative test asserting signalReady is never called.
    // TODO: No test verifies that ForceReleaseJobSlot clears _activeJobAssignment,
    // _activeJobStartedAt, _activeJobRunType, or _currentStep. Add a test that sets all fields,
    // calls ForceReleaseJobSlot, then asserts BuildActiveJobState() returns null to validate the
    // clearing logic end-to-end and catch regressions.
    public void ForceReleaseJobSlot()
    {
        lock (_busyLock)
        {
            _activeJobId = null;
            _activeJobAssignment = null;
            _activeJobStartedAt = null;
            _activeJobRunType = default;
            _currentStep = null;
        }

#pragma warning disable 0420 // volatile field passed by reference to Interlocked — safe by design
        var oldCts = Interlocked.Exchange(ref _jobCts, null);
#pragma warning restore 0420
        oldCts?.Dispose();
    }

    /// <summary>
    /// Builds the active job state for registration messages.
    /// Returns null if no job is active.
    /// </summary>
    public ActiveJobState? BuildActiveJobState()
    {
        lock (_busyLock)
        {
            if (_activeJobId is null || _activeJobAssignment is null)
                return null;

            return ActiveJobStateFactory.Create(
                _activeJobId, _activeJobAssignment,
                _currentStep ?? PipelineStep.GeneratingCode,
                _activeJobStartedAt ?? DateTimeOffset.UtcNow);
        }
    }
}
