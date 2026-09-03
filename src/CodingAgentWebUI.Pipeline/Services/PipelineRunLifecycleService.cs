using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog.Context;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Single source of truth for pipeline run state, lifecycle transitions, events, and cancellation.
/// Registered as a singleton. Consumers inject this for state/event access.
/// </summary>
public class PipelineRunLifecycleService : IDisposable, IAsyncDisposable, ILifecycleShutdownAction, IChangeNotifier, IChatNotifier
{
    // ── Dependencies ────────────────────────────────────────────────────
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IOrchestratorRunService? _runService;
    private readonly Serilog.ILogger _logger;
    private readonly IAgentCancellationSender? _agentCancellationSender;

    // ── State ───────────────────────────────────────────────────────────
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>The current cancellation token source for the active pipeline run.</summary>
    public CancellationTokenSource? CancellationTokenSource => _cancellationTokenSource;

    // ── Constructor ─────────────────────────────────────────────────────
    public PipelineRunLifecycleService(
        IPipelineRunHistoryService historyService,
        IOrchestratorRunService? runService,
        Serilog.ILogger logger,
        IAgentCancellationSender? agentCancellationSender = null)
    {
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(logger);

        _historyService = historyService;
        _runService = runService;
        _logger = logger;
        _agentCancellationSender = agentCancellationSender;
    }

    // ── Run State Properties ────────────────────────────────────────────

    /// <summary>The currently active pipeline run (set by test infrastructure), or null if idle.</summary>
    public PipelineRun? ActiveRun { get; set; }

    /// <summary>Whether a pipeline run is currently in progress (test infrastructure only in production).</summary>
    public bool IsRunning => ActiveRun != null
        && ActiveRun.CurrentStep != PipelineStep.Completed
        && ActiveRun.CurrentStep != PipelineStep.Failed
        && ActiveRun.CurrentStep != PipelineStep.Cancelled;

    /// <summary>Whether any pipeline run is active (in-process or agent-dispatched).</summary>
    public bool HasAnyActiveRuns => IsRunning || (_runService?.HasActiveRuns == true);

    // ── Events ──────────────────────────────────────────────────────────

    /// <summary>Fired after each state transition for UI binding.</summary>
    public event Action? OnChange;

    /// <summary>Fired for each agent output line for real-time display.</summary>
    public event Action<string>? OnOutputLine;

    /// <summary>Fired when chat response lines are received from an agent.</summary>
    public event Action<string, IReadOnlyList<string>>? OnChatResponse;

    /// <summary>Fired when a chat session completes on an agent.</summary>
    public event Action<string, int, string?>? OnChatCompleted;

    // ── State Query Methods ─────────────────────────────────────────────

    /// <summary>
    /// Returns all active runs — both the in-process run (if any) and all agent-dispatched runs.
    /// </summary>
    public IReadOnlyList<PipelineRun> GetAllActiveRuns()
    {
        var runs = new List<PipelineRun>();

        if (ActiveRun != null && IsRunning)
            runs.Add(ActiveRun);

        if (_runService != null)
            runs.AddRange(_runService.GetActiveRuns());

        return runs.AsReadOnly();
    }

    /// <summary>
    /// Checks whether the given issue is being processed by any active run (in-process or agent-dispatched).
    /// </summary>
    public bool IsIssueBeingProcessed(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        // TODO: ThrowIfNullOrEmpty is stricter than the original ThrowIfNull — it now rejects empty strings.
        // Also, [CallerArgumentExpression] emits "issueIdentifier.Value" as ParamName instead of "issueIdentifier".
        // Consider reverting to ArgumentNullException.ThrowIfNull(issueIdentifier.Value) to match original semantics,
        // or use the explicit paramName overload: ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier)).
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value);

        // Check in-process run
        if (ActiveRun != null && ActiveRun.IssueIdentifier == issueIdentifier
            && ActiveRun.IssueProviderConfigId == issueProviderConfigId.Value && IsRunning)
            return true;

        // Check agent runs via OrchestratorRunService
        return _runService?.IsIssueBeingProcessed(issueIdentifier, issueProviderConfigId) == true;
    }

    // ── State Transition Methods ────────────────────────────────────────

    /// <summary>
    /// Transitions the run to the specified step, updating HighWaterMark if applicable.
    /// </summary>
    public void TransitionTo(PipelineRun run, PipelineStep step)
    {
        var previousStep = run.CurrentStep;
        run.CurrentStep = step;

        if (step is not (PipelineStep.Failed or PipelineStep.Cancelled)
            && StepOrder.GetOrder(step) > StepOrder.GetOrder(run.HighWaterMark))
            run.HighWaterMark = step;

        _logger.Information("Pipeline {RunId} transitioned from {PreviousStep} to {Step}",
            run.RunId, previousStep, step);
        NotifyChange();
    }

    /// <summary>
    /// Marks the run as failed with the given reason, sets CompletedAt, emits output, transitions to Failed, and adds to history.
    /// </summary>
    public async Task FailRunAsync(PipelineRun run, string reason, CancellationToken ct = default)
    {
        run.FailureReason = reason;
        run.MarkCompleted();
        // TODO: [WARNING] EmitOutputLine reads ActiveRun?.RunId internally. If ActiveRun differs from `run`
        // (e.g. ActiveRun is null or points to a different run), the Serilog PipelineRunId property will be null
        // or wrong. Consider passing run.RunId directly to ensure the Serilog entry is correlated to the correct
        // run even when ActiveRun is not set. (#2178)
        EmitOutputLine($"❌ Pipeline failed: {reason}");
        TransitionTo(run, PipelineStep.Failed);
        await AddRunToHistoryAsync(run, ct).ConfigureAwait(false);
    }

    /// <summary>Adds the run to persistent history.</summary>
    public Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default) => _historyService.AddRunToHistoryAsync(run, ct);

    // ── Event Emission Methods ──────────────────────────────────────────

    /// <summary>Notifies subscribers of a state change. Exception-isolated.</summary>
    public void NotifyChange()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { _logger.Warning(ex, "OnChange handler threw an exception"); }
    }

    /// <summary>Emits an output line to subscribers. Exception-isolated.</summary>
    public void EmitOutputLine(string message)
    {
        var runId = ActiveRun?.RunId;
        // TODO: [WARNING] When ActiveRun is null (pre-run or post-completion), runId is null and silently
        // logged as the literal null. The acceptance criterion requires the run_id context property to be
        // present and meaningful. A guard or diagnostic warning when ActiveRun is null may be appropriate. (#2178)
        using (LogContext.PushProperty("PipelineRunId", runId))
        {
            _logger.Information("[Pipeline] {Line}", message);
        }
        try { OnOutputLine?.Invoke(message); }
        catch (Exception ex) { _logger.Warning(ex, "OnOutputLine handler threw an exception"); }
    }

    /// <summary>Notifies subscribers that chat response lines were received. Exception-isolated.</summary>
    public void NotifyChatResponse(string sessionId, IReadOnlyList<string> lines)
    {
        try { OnChatResponse?.Invoke(sessionId, lines); }
        catch (Exception ex) { _logger.Warning(ex, "OnChatResponse handler threw an exception"); }
    }

    /// <summary>Notifies subscribers that a chat session has completed. Exception-isolated.</summary>
    public void NotifyChatCompleted(string sessionId, int exitCode, string? error)
    {
        try { OnChatCompleted?.Invoke(sessionId, exitCode, error); }
        catch (Exception ex) { _logger.Warning(ex, "OnChatCompleted handler threw an exception"); }
    }

    // ── Cancellation ────────────────────────────────────────────────────

    /// <summary>
    /// Creates a linked cancellation token source from the external token and stores it.
    /// Returns the linked token.
    /// </summary>
    public CancellationToken CreateLinkedCancellationToken(CancellationToken externalToken)
    {
        var newCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var old = Interlocked.Exchange(ref _cancellationTokenSource, newCts);
        old?.Dispose();
        return newCts.Token;
    }

    /// <summary>Cancels the active pipeline run if one is running.</summary>
    public async Task CancelPipelineAsync()
    {
        if (ActiveRun == null || !IsRunning) return;

        var run = ActiveRun;
        _logger.Information("Pipeline {RunId} cancellation requested", run.RunId);

        try
        {
            Interlocked.CompareExchange(ref _cancellationTokenSource, null, null)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            _logger.Warning(
                "Pipeline {RunId} cancellation encountered disposed CancellationTokenSource — " +
                "CTS race between cancel and dispose. Attempting fallback cancel signal",
                run.RunId);

            if (_agentCancellationSender is not null && !string.IsNullOrEmpty(run.AgentId))
            {
                try
                {
                    await _agentCancellationSender.SendCancelJobAsync(
                        // Fire-and-forget: called from catch block after CTS disposed; cancellation signal must still be sent
                        run.AgentId, run.RunId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex,
                        "Pipeline {RunId} fallback cancel signal to agent {AgentId} also failed",
                        run.RunId, run.AgentId);
                }
            }
        }
        run.MarkCompleted();
        // TODO: [WARNING] Double-emission risk: LocalPipelineExecutor.ExecutePipelineStepsAsync (line ~246) also
        // calls buildResult.EmitOutputLine("🚫 Pipeline cancelled") via PipelineSignalRReporter on the
        // CancelledOutcome path. In local (in-process) deployments both this call and the agent-side call can
        // fire for the same cancellation event, causing "🚫 Pipeline cancelled" to appear twice in Serilog.
        // The per-class test (EmitOutputLine_CancellationPath_SingleCallProducesExactlyOneEntry) guards only
        // the PipelineSignalRReporter side and does not detect cross-class duplication. (#2178)
        EmitOutputLine("🚫 Pipeline cancelled");
        TransitionTo(run, PipelineStep.Cancelled);
        // Fire-and-forget: called from UI-triggered cancel; no ambient token available after CTS is cancelled
        await AddRunToHistoryAsync(run, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases all agent-dispatched runs from in-memory tracking during graceful shutdown.
    /// Returns list of issue identifiers for the caller to release dedup guards.
    /// No-op if no run service is configured.
    /// </summary>
    /// <remarks>
    /// Intentionally does NOT write Cancelled history entries and does NOT mutate run state.
    /// During a rolling update the new pod has already rehydrated these runs before this pod
    /// shuts down. Writing Cancelled history here would cause those runs to appear as CANCELLED
    /// in the UI even though the agents will complete them successfully on the new pod.
    /// The new pod's <see cref="RunLifecycleManager.CompleteRunAsync"/> writes the real outcome.
    /// Includes sentinel runs (AgentId == null) so their dedup guards are always freed.
    /// </remarks>
    public IReadOnlyList<(IssueIdentifier IssueIdentifier, string IssueProviderConfigId)> ReleaseAgentRunsForHandoff()
    {
        if (_runService is null) return [];

        var activeRuns = _runService.GetActiveRuns();
        if (activeRuns.Count == 0) return [];

        var releasedIssues = new List<(IssueIdentifier IssueIdentifier, string IssueProviderConfigId)>();
        foreach (var run in activeRuns)
        {
            _runService.RemoveRun(run.RunId);
            releasedIssues.Add((run.IssueIdentifier, run.IssueProviderConfigId));
        }

        NotifyChange();
        return releasedIssues;
    }

    // ── Dispatched Run Registration ─────────────────────────────────────

    /// <summary>
    /// Registers a dispatched run with the run service. Returns false if the issue is already being processed.
    /// Throws <see cref="InvalidOperationException"/> if no run service is configured.
    /// </summary>
    public bool RegisterDispatchedRun(PipelineRun run)
    {
        if (_runService is null)
            throw new InvalidOperationException("OrchestratorRunService is not configured. Cannot register dispatched runs.");

        if (IsIssueBeingProcessed(run.IssueIdentifier, run.IssueProviderConfigId))
        {
            _logger.Warning("Issue {IssueIdentifier} is already being processed, skipping registration",
                run.IssueIdentifier);
            return false;
        }

        _runService.AddRun(run);
        _logger.Information("Registered dispatched run {RunId} for issue {IssueIdentifier}",
            run.RunId, run.IssueIdentifier);
        NotifyChange();
        return true;
    }

    /// <summary>
    /// Atomically replaces a sentinel run (created by <see cref="RegisterDispatchedRun"/>) with a
    /// fully-populated <see cref="PipelineRun"/>. The run must have the same RunId as the sentinel.
    /// Throws <see cref="InvalidOperationException"/> if no run service is configured.
    /// </summary>
    public void ReplaceDispatchedRun(PipelineRun run)
    {
        if (_runService is null)
            throw new InvalidOperationException("OrchestratorRunService is not configured. Cannot replace dispatched runs.");

        _runService.ReplaceRun(run);
        _logger.Debug("Replaced dispatched run {RunId} for issue {IssueIdentifier}",
            run.RunId, run.IssueIdentifier);
        // TODO: This NotifyChange() introduces an extra OnChange event that wasn't emitted in the
        // pre-refactoring code (which called _runService.ReplaceRun directly from the dispatcher).
        // While benign (triggers an additional UI refresh), this changes observable behavior for
        // OnChange subscribers. Evaluate whether this notification is desired or should be suppressed.
        NotifyChange();
    }

    // ── Lifecycle ───────────────────────────────────────────────────────

    /// <summary>Clears all event subscribers. Used by subclasses for state reset.</summary>
    protected void ClearEventSubscribers()
    {
        OnChange = null;
        OnOutputLine = null;
        OnChatResponse = null;
        OnChatCompleted = null;
    }

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            Interlocked.Exchange(ref _cancellationTokenSource, null)?.Dispose();
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
