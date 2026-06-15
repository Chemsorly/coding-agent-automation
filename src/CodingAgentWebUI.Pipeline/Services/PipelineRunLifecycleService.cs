using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Single source of truth for pipeline run state, lifecycle transitions, events, and cancellation.
/// Registered as a singleton. Consumers inject this for state/event access.
/// </summary>
public class PipelineRunLifecycleService : IDisposable, IAsyncDisposable, ILifecycleShutdownAction
{
    // ── Dependencies ────────────────────────────────────────────────────
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IOrchestratorRunService? _runService;
    private readonly Serilog.ILogger _logger;

    // ── State ───────────────────────────────────────────────────────────
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>The current cancellation token source for the active pipeline run.</summary>
    public CancellationTokenSource? CancellationTokenSource => _cancellationTokenSource;

    // ── Constructor ─────────────────────────────────────────────────────
    public PipelineRunLifecycleService(
        IPipelineRunHistoryService historyService,
        IOrchestratorRunService? runService,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(logger);

        _historyService = historyService;
        _runService = runService;
        _logger = logger;
    }

    // ── Run State Properties ────────────────────────────────────────────

    /// <summary>The currently active local pipeline run, or null if idle.</summary>
    public PipelineRun? ActiveRun { get; set; }

    /// <summary>Whether a local pipeline run is currently in progress.</summary>
    public bool IsRunning => ActiveRun != null
        && ActiveRun.CurrentStep != PipelineStep.Completed
        && ActiveRun.CurrentStep != PipelineStep.Failed
        && ActiveRun.CurrentStep != PipelineStep.Cancelled;

    /// <summary>Whether any pipeline run is active (local or agent).</summary>
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
    /// Returns all active runs — both the local run (if any) and all agent-dispatched runs.
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
    /// Checks whether the given issue is being processed by any active run (local or agent).
    /// </summary>
    public bool IsIssueBeingProcessed(string issueIdentifier, string issueProviderConfigId)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(issueProviderConfigId);

        // Check local run
        if (ActiveRun != null && ActiveRun.IssueIdentifier == issueIdentifier
            && ActiveRun.IssueProviderConfigId == issueProviderConfigId && IsRunning)
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
    public Task FailRunAsync(PipelineRun run, string reason, CancellationToken ct = default)
    {
        run.FailureReason = reason;
        run.CompletedAt = DateTime.UtcNow;
        run.CompletedAtOffset = DateTimeOffset.UtcNow;
        EmitOutputLine($"❌ Pipeline failed: {reason}");
        TransitionTo(run, PipelineStep.Failed);
        AddRunToHistory(run);
        return Task.CompletedTask;
    }

    /// <summary>Adds the run to persistent history.</summary>
    public void AddRunToHistory(PipelineRun run) => _historyService.AddRunToHistory(run);

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
    public Task CancelPipelineAsync()
    {
        if (ActiveRun == null || !IsRunning) return Task.CompletedTask;

        var run = ActiveRun;
        _logger.Information("Pipeline {RunId} cancellation requested", run.RunId);

        // TODO: _cancellationTokenSource is read non-atomically here. A concurrent CreateLinkedCancellationToken can Exchange+Dispose the CTS between this read and .Cancel(), causing ObjectDisposedException. Use Interlocked read or capture a local snapshot.
        _cancellationTokenSource?.Cancel();
        run.CompletedAt = DateTime.UtcNow;
        run.CompletedAtOffset = DateTimeOffset.UtcNow;
        EmitOutputLine("🚫 Pipeline cancelled");
        TransitionTo(run, PipelineStep.Cancelled);
        AddRunToHistory(run);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Marks all agent-dispatched runs as cancelled. Sets CompletedAt, transitions to Cancelled, adds to history,
    /// and removes runs from active tracking. Returns list of cancelled issue identifiers for caller to release dedup.
    /// No-op if no run service is configured.
    /// </summary>
    public Task<IReadOnlyList<(string IssueIdentifier, string IssueProviderConfigId)>> MarkAgentRunsCancelled()
    {
        if (_runService is null) return Task.FromResult<IReadOnlyList<(string, string)>>([]);

        var activeRuns = _runService.GetActiveRuns();
        if (activeRuns.Count == 0) return Task.FromResult<IReadOnlyList<(string, string)>>([]);

        var cancelledIssues = new List<(string IssueIdentifier, string IssueProviderConfigId)>();
        foreach (var run in activeRuns)
        {
            run.CompletedAt = DateTime.UtcNow;
            run.CompletedAtOffset = DateTimeOffset.UtcNow;
            run.CurrentStep = PipelineStep.Cancelled;
            AddRunToHistory(run);
            _runService.RemoveRun(run.RunId);
            cancelledIssues.Add((run.IssueIdentifier, run.IssueProviderConfigId));
        }

        NotifyChange();
        return Task.FromResult<IReadOnlyList<(string, string)>>(cancelledIssues);
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

    // ── Lifecycle ───────────────────────────────────────────────────────

    /// <summary>Clears all event subscribers. Used by subclasses for state reset.</summary>
    protected void ClearEventSubscribers()
    {
        OnChange = null;
        OnOutputLine = null;
        OnChatResponse = null;
        OnChatCompleted = null;
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Dispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        _cancellationTokenSource?.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
