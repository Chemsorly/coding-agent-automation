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
    private int _currentStep = NullStep;
    private int _jobReleased; // 0 = not released, 1 = released (Interlocked guard for exactly-once signaling)

    private const int NullStep = -1;

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

    // NOTE: Thread-safety contract — ActiveChatSessionId acquires _busyLock for its read because
    // it participates in TOCTOU-sensitive operations (CancelChatIfSession, GetChatSlotSnapshot).
    // ActiveJobId uses volatile; CurrentStep uses Volatile.Read; IsBusy reads the volatile _activeJobId.
    // This is intentional: single-field reads use lightweight barriers, multi-field consistency uses locks.
    /// <summary>Whether the agent is currently executing a job.</summary>
    public bool IsBusy => _activeJobId is not null;

    /// <summary>The current pipeline step being executed, or null if idle.</summary>
    public PipelineStep? CurrentStep
    {
        get
        {
            var value = Volatile.Read(ref _currentStep);
            return value == NullStep ? null : (PipelineStep)value;
        }
    }

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

    /// <summary>The job cancellation token, or null if no job is active.</summary>
    public CancellationToken? JobCancellationToken
    {
        get
        {
            var cts = _jobCts;
            if (cts is null) return null;
            try { return cts.Token; }
            catch (ObjectDisposedException) { return null; }
        }
    }

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
            Interlocked.Exchange(ref _jobReleased, 0); // Reset guard for new job
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
        Volatile.Write(ref _currentStep, step.HasValue ? (int)step.Value : NullStep);
    }

    /// <summary>
    /// Releases the job slot, disposes the CTS, and signals the orchestrator that the agent is ready.
    /// Uses an atomic guard to ensure exactly-once signaling even if called concurrently from
    /// multiple paths (e.g., job completion and DrainBufferAsync on reconnection).
    /// </summary>
    public async Task ReleaseJobSlotAndSignalReadyAsync()
    {
        // Atomic guard: only the first caller proceeds with release and signal.
        // Subsequent concurrent or sequential calls are no-ops.
        if (Interlocked.CompareExchange(ref _jobReleased, 1, 0) != 0)
            return;

        lock (_busyLock)
        {
            _activeJobId = null;
            _activeJobAssignment = null;
            _activeJobStartedAt = null;
            _activeJobRunType = default;
            _currentStep = NullStep;
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
        // Mark as released so no concurrent path attempts to signal via ReleaseJobSlotAndSignalReadyAsync.
        // In practice, ForceReleaseJobSlot is called before the job task starts (on JobAccepted failure),
        // so there's no concurrent release to race with — this is defensive/future-proofing.
        Interlocked.Exchange(ref _jobReleased, 1);

        lock (_busyLock)
        {
            _activeJobId = null;
            _activeJobAssignment = null;
            _activeJobStartedAt = null;
            _activeJobRunType = default;
            _currentStep = NullStep;
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

            // TODO: Consider using Volatile.Read(ref _currentStep) here for consistency with the
            // CurrentStep property. Although _busyLock provides acquire/release fences, SetCurrentStep()
            // writes outside the lock using Volatile.Write, so a plain read here relies on the CLR's
            // current lock implementation emitting full barriers (which is not guaranteed by the spec).
            var step = _currentStep;
            return ActiveJobStateFactory.Create(
                _activeJobId, _activeJobAssignment,
                step == NullStep ? PipelineStep.GeneratingCode : (PipelineStep)step,
                _activeJobStartedAt ?? DateTimeOffset.UtcNow);
        }
    }
}
