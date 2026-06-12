using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Background service that polls for agent:next issues and dispatches them to agents
/// via the <see cref="IJobDispatcher"/>. Issues are always dispatched to agents or enqueued;
/// local execution is not supported. If no dispatcher is available, issues are skipped.
/// Starts dormant and is activated via <see cref="StartLoop"/>. Survives page navigation.
/// </summary>
public sealed partial class PipelineLoopService : BackgroundService
{
    private readonly PipelineOrchestrationService _orchestration;
    private readonly IProviderFactory _providerFactory;
    private readonly IPipelineConfigStore _pipelineConfigStore;
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IProjectStore _projectStore;
    private readonly IJobDispatcher? _jobDispatcher;
    private readonly IDependencyChecker? _dependencyChecker;
    private readonly Serilog.ILogger _logger;

    private TaskCompletionSource _activationSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();

    private volatile bool _stopRequested;
    private CancellationTokenSource? _loopCts;
    private TaskCompletionSource? _resumeSignal;

    // ── Multi-template fields ───────────────────────────────────────────

    /// <summary>Provider cache keyed by IssueProviderId. Reused across cycles.</summary>
    private readonly Dictionary<string, IIssueProvider> _providerCache = new();

    /// <summary>Repository provider cache keyed by RepoProviderId. Reused across cycles.</summary>
    private readonly Dictionary<string, IRepositoryProvider> _repoProviderCache = new();

    /// <summary>Per-template runtime status. Immutable records swapped atomically.</summary>
    private readonly ConcurrentDictionary<string, ConfigStatusSnapshot> _templateStatuses = new();

    /// <summary>Validation errors from the last StartLoop() call.</summary>
    private List<string> _validationErrors = new();

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
    // NOTE: [RES-03] ConsecutivePollFailures, IsCircuitBroken, and LastPollError are written in RunMultiTemplateLoopAsync without _lock — consider wrapping writes under lock for consistency with StartLoop/StopLoop/ResumeLoop (review finding .NET #1)
    public int ConsecutivePollFailures { get; private set; }

    /// <summary>Whether the circuit breaker has tripped due to consecutive poll failures.</summary>
    public bool IsCircuitBroken { get; private set; }

    /// <summary>Last poll error message, or null if last poll succeeded.</summary>
    public string? LastPollError { get; private set; }

    // ── Multi-template public API ───────────────────────────────────────

    /// <summary>Per-template status for UI binding (immutable snapshots, atomically swapped).</summary>
    public IReadOnlyDictionary<string, ConfigStatusSnapshot> TemplateStatuses => _templateStatuses;

    /// <summary>Index of the template currently being polled in this cycle (0-based).</summary>
    public int CurrentCycleTemplateIndex { get; private set; }

    /// <summary>Total number of enabled templates in the current cycle.</summary>
    public int CurrentCycleTemplateCount { get; private set; }

    /// <summary>Validation errors from the last failed StartLoop() call.</summary>
    public IReadOnlyList<string> ValidationErrors => _validationErrors;

    public PipelineLoopService(
        PipelineOrchestrationService orchestration,
        IProviderFactory providerFactory,
        IPipelineConfigStore pipelineConfigStore,
        IProviderConfigStore providerConfigStore,
        IProjectStore projectStore,
        Serilog.ILogger logger,
        IJobDispatcher? jobDispatcher = null,
        IDependencyChecker? dependencyChecker = null)
    {
        ArgumentNullException.ThrowIfNull(orchestration);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(pipelineConfigStore);
        ArgumentNullException.ThrowIfNull(providerConfigStore);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(logger);

        _orchestration = orchestration;
        _providerFactory = providerFactory;
        _pipelineConfigStore = pipelineConfigStore;
        _providerConfigStore = providerConfigStore;
        _projectStore = projectStore;
        _logger = logger;
        _jobDispatcher = jobDispatcher;
        _dependencyChecker = dependencyChecker;
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

    /// <summary>
    /// Activates the multi-template round-robin loop using PipelineJobTemplates from config.
    /// Returns false if no enabled templates exist or validation fails.
    /// </summary>
    public async Task<bool> StartLoopAsync()
    {
        // Load config outside the lock to avoid sync-over-async deadlocks
        // (Blazor Server's RendererSynchronizationContext would deadlock on .GetAwaiter().GetResult())
        var config = await _pipelineConfigStore.LoadPipelineConfigAsync(CancellationToken.None).ConfigureAwait(false);
        var issueProviders = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None).ConfigureAwait(false);
        var repoProviders = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None).ConfigureAwait(false);
        var templates = await _projectStore.LoadAllTemplatesAsync(CancellationToken.None).ConfigureAwait(false);

        lock (_lock)
        {
            if (IsLoopActive)
                return false;
            if (_orchestration.IsRunning)
                return false;

            var enabledTemplates = templates.Where(t => t.Enabled).ToList();

            _validationErrors = new List<string>();

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
            ConsecutivePollFailures = 0;
            IsCircuitBroken = false;
            LastPollError = null;
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
            // Wait for activation
            await _activationSignal.Task.WaitAsync(stoppingToken);

            try
            {
                await RunMultiTemplateLoopAsync(stoppingToken);
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

    private async Task CleanupAsync()
    {
        lock (_lock)
        {
            IsLoopActive = false;
            _stopRequested = false;
            CurrentIssueIdentifier = null;
            CurrentCycleTemplateIndex = 0;
            CurrentCycleTemplateCount = 0;
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

        // Dispose all cached providers
        foreach (var kvp in _providerCache)
        {
            try { await kvp.Value.DisposeAsync(); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to dispose cached provider {ProviderId}", kvp.Key); }
        }
        _providerCache.Clear();

        // Dispose all cached repo providers
        foreach (var kvp in _repoProviderCache)
        {
            try { await kvp.Value.DisposeAsync(); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to dispose cached repo provider {ProviderId}", kvp.Key); }
        }
        _repoProviderCache.Clear();

        _templateStatuses.Clear();

        NotifyChange();
        _logger.Information("Pipeline loop stopped. Processed: {Processed}, Failed: {Failed}", ProcessedCount, FailedCount);
    }
}
