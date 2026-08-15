using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Background service that polls for agent:next issues and dispatches them to agents
/// via the <see cref="IWorkDistributor"/>. Issues are always dispatched to agents or enqueued;
/// if no distributor is available, issues are skipped.
/// Starts dormant and is activated via <see cref="StartLoop"/>. Survives page navigation.
/// </summary>
public sealed partial class PipelineLoopService : BackgroundService, IPipelineLoopService
{
    private readonly IPipelineConfigStore _pipelineConfigStore;
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IProjectStore _projectStore;
    private readonly IWorkDistributor? _workDistributor;
    private readonly IHousekeepingService? _housekeepingService;
    private readonly ILeaderGate? _leaderGate;
    private readonly Serilog.ILogger _logger;

    private TaskCompletionSource _activationSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _lock = new();
    private readonly TemplateCircuitBreaker _circuitBreaker = new();
    // TODO: _cacheManager is promoted to internal solely to enable direct field seeding in tests
    // (svc._cacheManager.RepoProviders["rp-hk"] = ...). This exposes mutable provider dictionaries
    // to all internal assembly consumers. Consider a narrower alternative such as an internal
    // test-seeding method or constructor overload to restore the field to private readonly.
    internal readonly ProviderCacheManager _cacheManager;
    private readonly TemplatePoller _poller;
    private readonly DispatchScheduler _dispatcher;

    private volatile bool _stopRequested;
    private CancellationTokenSource? _loopCts;
    private TaskCompletionSource? _resumeSignal;

    // ── Multi-template fields ───────────────────────────────────────────

    /// <summary>Per-template runtime status. Immutable records swapped atomically.</summary>
    private readonly ConcurrentDictionary<string, ConfigStatusSnapshot> _templateStatuses = new();

    /// <summary>Validation errors from the last StartLoop() call.</summary>
    private List<string> _validationErrors = [];

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

    /// <summary>Whether the circuit breaker has tripped due to consecutive poll failures.</summary>
    public bool IsCircuitBroken => _circuitBreaker.IsTripped;

    /// <summary>Last poll error message, or null if last poll succeeded.</summary>
    public string? LastPollError => _circuitBreaker.LastError;

    // ── Multi-template public API ───────────────────────────────────────

    /// <summary>Per-template status for UI binding (immutable snapshots, atomically swapped).</summary>
    public IReadOnlyDictionary<string, ConfigStatusSnapshot> TemplateStatuses => _templateStatuses;

    /// <summary>Index of the template currently being polled in this cycle (0-based).</summary>
    public int CurrentCycleTemplateIndex { get; private set; }

    /// <summary>Total number of enabled templates in the current cycle.</summary>
    public int CurrentCycleTemplateCount { get; private set; }

    /// <summary>Validation errors from the last failed StartLoop() call.</summary>
    public IReadOnlyList<string> ValidationErrors => _validationErrors;

    public PipelineLoopService(PipelineLoopServiceDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Orchestration);
        ArgumentNullException.ThrowIfNull(deps.ProviderFactory);
        ArgumentNullException.ThrowIfNull(deps.PipelineConfigStore);
        ArgumentNullException.ThrowIfNull(deps.ProviderConfigStore);
        ArgumentNullException.ThrowIfNull(deps.ProjectStore);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _pipelineConfigStore = deps.PipelineConfigStore;
        _providerConfigStore = deps.ProviderConfigStore;
        _projectStore = deps.ProjectStore;
        _logger = deps.Logger;
        _workDistributor = deps.WorkDistributor;
        _housekeepingService = deps.HousekeepingService;
        _leaderGate = deps.LeaderElection;

        _cacheManager = new ProviderCacheManager(deps.ProviderFactory, deps.Logger);
        _poller = new TemplatePoller(_cacheManager, deps.Logger);
        _dispatcher = new DispatchScheduler(deps.Orchestration, deps.DispatchOrchestration, deps.WorkDistributor, deps.DependencyChecker, _cacheManager, deps.Logger);
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
            if (!_circuitBreaker.IsTripped) return;
            _circuitBreaker.Reset();
            StatusMessage = "🔄 Loop resumed, polling at normal interval.";
            _resumeSignal?.TrySetResult();
            NotifyChange();
            _logger.Information("Loop resumed, polling at normal interval");
        }
    }

    /// <summary>
    /// Activates the multi-template round-robin loop using templates from IProjectStore.
    /// Returns false if no enabled templates exist or validation fails.
    /// </summary>
    public async Task<bool> StartLoopAsync()
    {
        // Load config outside the lock to avoid sync-over-async deadlocks
        // (Blazor Server's RendererSynchronizationContext would deadlock on .GetAwaiter().GetResult())
        IReadOnlyList<ProviderConfig> issueProviders;
        IReadOnlyList<ProviderConfig> repoProviders;
        IReadOnlyList<PipelineJobTemplate> templates;

        try
        {
            _ = await _pipelineConfigStore.LoadPipelineConfigAsync(CancellationToken.None).ConfigureAwait(false);
            issueProviders = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None).ConfigureAwait(false);
            repoProviders = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None).ConfigureAwait(false);
            templates = await _projectStore.LoadAllTemplatesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "StartLoopAsync failed to load configuration from stores");
            lock (_lock)
            {
                _validationErrors = [$"Failed to load configuration: {ex.Message}"];
            }
            NotifyChange();
            return false;
        }

        lock (_lock)
        {
            if (IsLoopActive)
                return false;

            var enabledTemplates = templates.Where(t => t.Enabled).ToList();

            _validationErrors = [];

            if (enabledTemplates.Count == 0)
            {
                _validationErrors.Add("No enabled pipeline job templates configured.");
                return false;
            }

            // Validate all enabled templates reference existing provider IDs
            var issueProviderIds = issueProviders.Select(p => p.Id).ToHashSet();
            var repoProviderIds = repoProviders.Select(p => p.Id).ToHashSet();

            foreach (var template in enabledTemplates)
            {
                if (!issueProviderIds.Contains(template.IssueProviderId))
                    _validationErrors.Add($"Template '{template.Name}' references non-existent issue provider '{template.IssueProviderId}'.");
                if (!repoProviderIds.Contains(template.RepoProviderId))
                    _validationErrors.Add($"Template '{template.Name}' references non-existent repo provider '{template.RepoProviderId}'.");
            }

            if (_validationErrors.Count > 0)
                return false;

            _stopRequested = false;
            ProcessedCount = 0;
            FailedCount = 0;
            QueueCount = 0;
            _circuitBreaker.Reset();
            CurrentIssueIdentifier = null;
            CurrentCycleTemplateIndex = 0;
            CurrentCycleTemplateCount = enabledTemplates.Count;
            IsLoopActive = true;
            StatusMessage = "🔄 Loop starting…";

            _loopCts = new CancellationTokenSource();
            _activationSignal.TrySetResult();

            NotifyChange();
            _logger.Information("Pipeline loop started in multi-template mode with {Count} enabled templates",
                enabledTemplates.Count);
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // K8s mode: wait for leadership before entering the activation-wait loop.
            // _leaderGate is null in Legacy mode → this inner loop is skipped entirely,
            // preserving the existing unconditional behaviour.
            while (!stoppingToken.IsCancellationRequested && (_leaderGate is { IsLeader: false }))
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            // Wait for loop activation (StartLoopAsync signals _activationSignal)
            await _activationSignal.Task.WaitAsync(stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            // Create a linked token: cancelled on host stop OR leadership loss.
            // _leaderGate?.LeaderToken is CancellationToken.None when null (no-op token),
            // so linking it has no effect in Legacy / no-gate mode.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, _leaderGate?.LeaderToken ?? CancellationToken.None);

            try
            {
                // linked.Token is passed as stoppingToken to RunMultiTemplateLoopAsync.
                // Inside that method it flows through ExecuteCycleAsync and into
                // DispatchFairRoundRobinAsync — meaning in-flight dispatch is interrupted
                // cleanly on leadership loss, which is the intended behaviour.
                await RunMultiTemplateLoopAsync(linked.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is stopping — break out of the outer loop entirely.
                // CleanupAsync still runs in finally, but rearmForLeaderReacquisition = false.
                break;
            }
            catch (OperationCanceledException) when (linked.Token.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // Leadership lost mid-run and the OCE escaped RunMultiTemplateLoopAsync.
                // CleanupAsync (in finally) will re-arm via the linked token check below.
                // Fall through to finally, then re-enter the outer while to wait for leadership.
            }
            catch (Exception ex)
            {
                // TODO [WARNING]: This catch block is reached when an OperationCanceledException
                // surfaces from RunMultiTemplateLoopAsync due to _loopCts cancellation (StopLoop path),
                // because _loopCts.Token is linked inside RunMultiTemplateLoopAsync but is NOT linked
                // into the outer `linked` CTS. As a result, `linked.Token.IsCancellationRequested`
                // is false, neither `when` filter above matches, and the OCE falls through here and
                // is logged as an unexpected error — a false alarm. Fix: either link _loopCts.Token
                // into the outer `linked` CTS, or add a dedicated catch filter for this path.
                _logger.Error(ex, "Pipeline loop encountered an unexpected error");
            }
            finally
            {
                // Re-arm if: the linked token (leader + host) was cancelled for leadership loss,
                // NOT for host stop. RunMultiTemplateLoopAsync may exit normally (not via OCE)
                // when linked.Token fires — checking the token state here covers both paths.
                var rearmForLeaderReacquisition = _leaderGate is not null
                    && linked.Token.IsCancellationRequested
                    && !stoppingToken.IsCancellationRequested;

                await CleanupAsync(rearmForLeaderReacquisition);
            }
        }
    }

    /// <param name="rearmForLeaderReacquisition">
    /// When <see langword="true"/> (leadership was lost mid-run), re-arms the activation signal
    /// and <c>_loopCts</c> so that <see cref="ExecuteAsync"/> automatically re-enters the loop
    /// on next leadership acquisition without a second <see cref="StartLoopAsync"/> call.
    /// When <see langword="false"/> (explicit stop or host shutdown), the loop stays dormant.
    /// </param>
    private async Task CleanupAsync(bool rearmForLeaderReacquisition = false)
    {
        lock (_lock)
        {
            IsLoopActive = false;
            _stopRequested = false;
            CurrentIssueIdentifier = null;
            CurrentCycleTemplateIndex = 0;
            CurrentCycleTemplateCount = 0;
            _circuitBreaker.Reset();
            StatusMessage = "";
            // Reset activation signal under lock to prevent race with StartLoop (review finding #1)
            _activationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Dispose _loopCts under lock to prevent race with StartLoop creating a new one (review finding #17)
            _loopCts?.Dispose();
            _loopCts = null;

            if (rearmForLeaderReacquisition)
            {
                // Leadership was lost mid-run. Re-arm the activation signal and a fresh loop CTS
                // so that ExecuteAsync automatically re-enters RunMultiTemplateLoopAsync as soon
                // as leadership is next acquired — without requiring another StartLoopAsync() call.
                //
                // IsLoopActive is restored to true: the operator's intent to run the loop is
                // preserved. The loop is not currently executing (waiting for leadership), but it
                // will resume as soon as it becomes leader again.
                //
                // TODO [WARNING]: If StopLoop() is called between leadership loss and the next
                // leadership acquisition, it will find IsLoopActive == true (re-armed here) and
                // cancel the fresh _loopCts, but will not clear the pre-signalled _activationSignal.
                // ExecuteAsync will then run one spurious short-circuit pass through
                // RunMultiTemplateLoopAsync (which exits immediately because _stopRequested == true),
                // and then CleanupAsync(false) fires. This is benign but wasteful. Fix: check
                // `!_stopRequested` before re-arming, e.g. `if (rearmForLeaderReacquisition && !_stopRequested)`.
                IsLoopActive = true;
                _loopCts = new CancellationTokenSource();
                _activationSignal.TrySetResult();
            }
        }

        // Dispose all cached providers via the cache manager
        await _cacheManager.DisposeAsync();

        _templateStatuses.Clear();

        NotifyChange();
        _logger.Information("Pipeline loop stopped. Processed: {Processed}, Failed: {Failed}", ProcessedCount, FailedCount);
    }
}
