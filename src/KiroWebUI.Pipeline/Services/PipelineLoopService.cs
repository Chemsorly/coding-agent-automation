using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Background service that polls for agent:next issues and processes them sequentially.
/// Starts dormant and is activated via <see cref="StartLoop"/>. Survives page navigation.
/// </summary>
public sealed class PipelineLoopService : BackgroundService
{
    private readonly PipelineOrchestrationService _orchestration;
    private readonly IProviderFactory _providerFactory;
    private readonly IConfigurationStore _configStore;
    private readonly Serilog.ILogger _logger;

    private TaskCompletionSource _activationSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();

    private volatile bool _stopRequested;
    private CancellationTokenSource? _loopCts;
    private TaskCompletionSource? _resumeSignal;

    // Captured provider IDs at start time
    private string _issueProviderId = "";
    private string _repoProviderId = "";
    private string _agentProviderId = "";
    private string? _brainProviderId;
    private string? _pipelineProviderId;

    // Polling provider (separate from per-run providers)
    private IIssueProvider? _pollingIssueProvider;

    /// <summary>Fired when loop state changes, for UI binding.</summary>
    public event Action? OnChange;

    /// <summary>Whether the loop is currently active (processing or polling).</summary>
    public bool IsLoopActive { get; private set; }

    /// <summary>Current status message for UI display.</summary>
    public string StatusMessage { get; private set; } = "";

    /// <summary>Identifier of the issue currently being processed, or null.</summary>
    public string? CurrentIssueIdentifier { get; private set; }

    /// <summary>Number of issues processed in the current loop activation.</summary>
    public int ProcessedCount { get; private set; }

    /// <summary>Number of issues that failed in the current loop activation.</summary>
    public int FailedCount { get; private set; }

    /// <summary>Number of agent:next issues remaining in the current queue snapshot.</summary>
    public int QueueCount { get; private set; }

    /// <summary>Number of consecutive poll failures since last successful poll.</summary>
    // TODO: [RES-03] ConsecutivePollFailures, IsCircuitBroken, and LastPollError are written in RunLoopAsync without _lock — consider wrapping writes under lock for consistency with StartLoop/StopLoop/ResumeLoop (review finding .NET #1)
    public int ConsecutivePollFailures { get; private set; }

    /// <summary>Whether the circuit breaker has tripped due to consecutive poll failures.</summary>
    public bool IsCircuitBroken { get; private set; }

    /// <summary>Last poll error message, or null if last poll succeeded.</summary>
    public string? LastPollError { get; private set; }

    public PipelineLoopService(
        PipelineOrchestrationService orchestration,
        IProviderFactory providerFactory,
        IConfigurationStore configStore,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(orchestration);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(logger);

        _orchestration = orchestration;
        _providerFactory = providerFactory;
        _configStore = configStore;
        _logger = logger;
    }

    /// <summary>
    /// Activates the loop with the given provider IDs. Rejects if already active or a manual run is in progress.
    /// </summary>
    public bool StartLoop(string issueProviderId, string repoProviderId, string agentProviderId,
        string? brainProviderId, string? pipelineProviderId)
    {
        // TODO: [UX-12b] Add ArgumentNullException.ThrowIfNull for issueProviderId, repoProviderId, agentProviderId (review finding #14)
        lock (_lock)
        {
            if (IsLoopActive)
                return false;
            if (_orchestration.IsRunning)
                return false;

            _issueProviderId = issueProviderId;
            _repoProviderId = repoProviderId;
            _agentProviderId = agentProviderId;
            _brainProviderId = brainProviderId;
            _pipelineProviderId = pipelineProviderId;

            _stopRequested = false;
            ProcessedCount = 0;
            FailedCount = 0;
            QueueCount = 0;
            ConsecutivePollFailures = 0;
            IsCircuitBroken = false;
            LastPollError = null;
            CurrentIssueIdentifier = null;
            IsLoopActive = true;
            StatusMessage = "🔄 Loop starting…";

            _loopCts = new CancellationTokenSource();

            // Signal the background loop to wake up
            _activationSignal.TrySetResult();

            NotifyChange();
            _logger.Information("Pipeline loop started with issue={IssueProvider}, repo={RepoProvider}, agent={AgentProvider}",
                issueProviderId, repoProviderId, agentProviderId);
            return true;
        }
    }

    /// <summary>
    /// Requests the loop to stop. If a run is in progress, it finishes first.
    /// </summary>
    public void StopLoop()
    {
        lock (_lock)
        {
            if (!IsLoopActive) return;
            _stopRequested = true;
            // Cancel the loop CTS so DelayOrStop returns immediately (review finding #2)
            try { _loopCts?.Cancel(); } catch (ObjectDisposedException) { }
            // Unblock circuit breaker wait if paused
            _resumeSignal?.TrySetResult();
            StatusMessage = "⏹ Loop stopping… (finishing current run)";
            NotifyChange();
            _logger.Information("Pipeline loop stop requested");
        }
    }

    /// <summary>
    /// Resumes the loop after the circuit breaker has tripped. Resets failure counters
    /// and unblocks the polling loop.
    /// </summary>
    public void ResumeLoop()
    {
        lock (_lock)
        {
            if (!IsCircuitBroken) return;
            ConsecutivePollFailures = 0;
            IsCircuitBroken = false;
            LastPollError = null;
            StatusMessage = "🔄 Loop resumed, polling at normal interval.";
            _resumeSignal?.TrySetResult();
            NotifyChange();
            _logger.Information("Loop resumed, polling at normal interval");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for activation
            await _activationSignal.Task.WaitAsync(stoppingToken);

            try
            {
                await RunLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Pipeline loop encountered an unexpected error");
            }
            finally
            {
                await CleanupAsync();
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        // Create polling issue provider
        var issueProviderConfig = (await _configStore.LoadProviderConfigsAsync(ProviderKind.Issue, stoppingToken))
            .FirstOrDefault(c => c.Id == _issueProviderId)
            ?? throw new InvalidOperationException($"Issue provider '{_issueProviderId}' not found.");

        _pollingIssueProvider = _providerFactory.CreateIssueProvider(issueProviderConfig);

        // TODO: [UX-12b] Config is read once at loop start; changes via Settings page won't take effect until loop restart (review finding #5)
        var config = await _configStore.LoadPipelineConfigAsync(stoppingToken);
        var pollInterval = config.ClosedLoopPollInterval;
        var maxRunsPerCycle = config.ClosedLoopMaxRunsPerCycle;
        var maxConsecutiveFailures = config.ClosedLoopMaxConsecutivePollFailures;
        var maxBackoff = config.ClosedLoopMaxBackoffInterval;
        var maxPagesToFetch = config.ClosedLoopMaxPagesToFetch;

        // ct is linked to both stoppingToken and _loopCts — used for delays and polling.
        // StopLoop cancels _loopCts to break out of delays promptly.
        // Pipeline runs receive stoppingToken only — StopLoop does not cancel active runs (review finding #7).
        // TODO: [RES-03] _loopCts is read here without _lock — could race with CleanupAsync disposing it; capture token under lock (review finding .NET #2)
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _loopCts?.Token ?? CancellationToken.None);
        var ct = linkedCts.Token;

        while (!_stopRequested && !ct.IsCancellationRequested)
        {
            int runsThisCycle = 0;

            // Poll for agent:next issues
            List<IssueSummary> candidates;
            try
            {
                candidates = await FetchAgentNextIssuesAsync(maxPagesToFetch, ct);
                // Success — reset backoff state
                ConsecutivePollFailures = 0;
                LastPollError = null;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (RateLimitExceededException ex)
            {
                // Rate limits are expected — wait until reset, don't count as failure
                var waitUntil = ex.ResetAt - DateTimeOffset.UtcNow;
                if (waitUntil < TimeSpan.Zero) waitUntil = pollInterval;
                _logger.Warning("Rate limit exceeded, waiting until {ResetAt} ({WaitDuration})",
                    ex.ResetAt, waitUntil);
                StatusMessage = $"🔄 Loop idle — rate limited. Resuming at {ex.ResetAt:HH:mm:ss} UTC.";
                NotifyChange();
                await DelayOrStop(waitUntil, ct);
                continue;
            }
            catch (Exception ex)
            {
                ConsecutivePollFailures++;
                LastPollError = ex.Message;

                // Circuit breaker — pause after N consecutive failures
                if (ConsecutivePollFailures >= maxConsecutiveFailures)
                {
                    IsCircuitBroken = true;
                    StatusMessage = $"⚠️ Loop paused — polling failed {ConsecutivePollFailures} times consecutively. Last error: {ex.Message}";
                    NotifyChange();
                    _logger.Warning("Loop paused after {FailureCount} consecutive poll failures. Last error: {ErrorMessage}",
                        ConsecutivePollFailures, ex.Message);

                    // Wait for ResumeLoop or StopLoop
                    _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    try
                    {
                        await _resumeSignal.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (_stopRequested) break;
                    continue;
                }

                // Exponential backoff: pollInterval × 2^(failures-1), capped at maxBackoff
                var shift = Math.Min(ConsecutivePollFailures - 1, 30);
                var backoffTicks = pollInterval.Ticks * (1L << shift);
                var backoffInterval = backoffTicks > maxBackoff.Ticks || backoffTicks <= 0
                    ? maxBackoff : TimeSpan.FromTicks(backoffTicks);

                _logger.Warning(ex, "Poll failure #{FailureCount}, backing off to {NextRetryIn}. ErrorType={ErrorType}, ErrorMessage={ErrorMessage}",
                    ConsecutivePollFailures, backoffInterval, ex.GetType().Name, ex.Message);
                StatusMessage = $"🔄 Loop idle — poll failure #{ConsecutivePollFailures}, retrying in {(int)backoffInterval.TotalSeconds}s.";
                NotifyChange();
                await DelayOrStop(backoffInterval, ct);
                continue;
            }

            if (candidates.Count == 0)
            {
                StatusMessage = "🔄 Loop idle — no `agent:next` issues. Polling every " + (int)pollInterval.TotalSeconds + "s.";
                QueueCount = 0;
                NotifyChange();
                await DelayOrStop(pollInterval, ct);
                continue;
            }

            // Check if all candidates have agent:error or agent:needs-refinement
            if (candidates.All(c => c.Labels.Contains(AgentLabels.Error) || c.Labels.Contains(AgentLabels.NeedsRefinement)))
            {
                StatusMessage = "🔄 Loop idle — all `agent:next` issues have errors or need refinement. Polling every " + (int)pollInterval.TotalSeconds + "s.";
                QueueCount = candidates.Count;
                NotifyChange();
                await DelayOrStop(pollInterval, ct);
                continue;
            }

            // Process issues FIFO (oldest first)
            foreach (var issue in candidates)
            {
                if (_stopRequested || stoppingToken.IsCancellationRequested) break;
                if (maxRunsPerCycle > 0 && runsThisCycle >= maxRunsPerCycle) break;

                // Skip issues with agent:error or agent:needs-refinement
                if (issue.Labels.Contains(AgentLabels.Error) || issue.Labels.Contains(AgentLabels.NeedsRefinement))
                {
                    _logger.Information("Pipeline loop skipping issue #{Issue} (has error/needs-refinement label)", issue.Identifier);
                    continue;
                }

                // Wait for any in-progress run to finish
                if (_orchestration.IsRunning)
                {
                    _logger.Warning("Pipeline loop waiting for in-progress run to complete before starting next issue");
                    while (_orchestration.IsRunning && !_stopRequested && !stoppingToken.IsCancellationRequested)
                        await Task.Delay(1000, stoppingToken);
                    if (_stopRequested || stoppingToken.IsCancellationRequested) break;
                }

                CurrentIssueIdentifier = issue.Identifier;
                // TODO: [UX-12b] QueueCount does not subtract skipped issues — shows total candidates not processable count (review finding #4)
                QueueCount = candidates.Count - runsThisCycle;
                StatusMessage = $"🔄 Loop active — processing issue #{issue.Identifier} ({runsThisCycle + 1} of {candidates.Count} in queue, {FailedCount} failed)";
                NotifyChange();

                try
                {
                    _logger.Information("Pipeline loop starting run for issue #{Issue}: {Title}", issue.Identifier, issue.Title);
                    // Pass stoppingToken (not ct) so StopLoop doesn't cancel the active run — only app shutdown does
                    await _orchestration.StartPipelineAsync(
                        _issueProviderId, _repoProviderId, issue.Identifier, _agentProviderId,
                        stoppingToken, _brainProviderId, _pipelineProviderId, initiatedBy: "loop");

                    var run = _orchestration.ActiveRun;
                    if (run?.CurrentStep == PipelineStep.Failed)
                        FailedCount++;

                    ProcessedCount++;
                    runsThisCycle++;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Pipeline loop run failed for issue #{Issue}", issue.Identifier);
                    FailedCount++;
                    ProcessedCount++;
                    runsThisCycle++;
                }
            }

            CurrentIssueIdentifier = null;

            if (_stopRequested || stoppingToken.IsCancellationRequested) break;

            // Cycle complete — wait before next poll
            StatusMessage = "🔄 Loop idle — cycle complete. Polling every " + (int)pollInterval.TotalSeconds + "s.";
            NotifyChange();
            await DelayOrStop(pollInterval, ct);
        }
    }

    private async Task<List<IssueSummary>> FetchAgentNextIssuesAsync(int maxPages, CancellationToken ct)
    {
        var result = new List<IssueSummary>();
        int page = 1;
        const int pageSize = 100;

        while (true)
        {
            var pagedResult = await _pollingIssueProvider!.ListOpenIssuesAsync(page, pageSize,
                new[] { AgentLabels.Next }, ct);
            result.AddRange(pagedResult.Items);
            if (!pagedResult.HasMore) break;
            if (page >= maxPages)
            {
                _logger.Warning("Reached max page limit ({MaxPages}) while fetching agent:next issues; {Count} issues fetched, more available",
                    maxPages, result.Count);
                break;
            }
            page++;
        }

        // FIFO: oldest first by CreatedAt
        result.Sort((a, b) =>
        {
            var aDate = a.CreatedAt ?? DateTime.MaxValue;
            var bDate = b.CreatedAt ?? DateTime.MaxValue;
            return aDate.CompareTo(bDate);
        });

        return result;
    }

    private async Task DelayOrStop(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await Task.Delay(interval, ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task CleanupAsync()
    {
        lock (_lock)
        {
            IsLoopActive = false;
            _stopRequested = false;
            CurrentIssueIdentifier = null;
            ConsecutivePollFailures = 0;
            IsCircuitBroken = false;
            LastPollError = null;
            StatusMessage = "";
            // Reset activation signal under lock to prevent race with StartLoop (review finding #1)
            _activationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Dispose _loopCts under lock to prevent race with StartLoop creating a new one (review finding #17)
            _loopCts?.Dispose();
            _loopCts = null;
        }

        if (_pollingIssueProvider is not null)
        {
            try { await _pollingIssueProvider.DisposeAsync(); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to dispose polling issue provider"); }
            _pollingIssueProvider = null;
        }

        NotifyChange();
        _logger.Information("Pipeline loop stopped. Processed: {Processed}, Failed: {Failed}", ProcessedCount, FailedCount);
    }

    private void NotifyChange()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { _logger.Warning(ex, "PipelineLoopService OnChange handler threw"); }
    }
}
