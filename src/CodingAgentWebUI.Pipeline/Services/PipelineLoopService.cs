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
public sealed class PipelineLoopService : BackgroundService
{
    private readonly PipelineOrchestrationService _orchestration;
    private readonly IProviderFactory _providerFactory;
    private readonly IPipelineConfigStore _pipelineConfigStore;
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IJobDispatcher? _jobDispatcher;
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
        Serilog.ILogger logger,
        IJobDispatcher? jobDispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(orchestration);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(pipelineConfigStore);
        ArgumentNullException.ThrowIfNull(providerConfigStore);
        ArgumentNullException.ThrowIfNull(logger);

        _orchestration = orchestration;
        _providerFactory = providerFactory;
        _pipelineConfigStore = pipelineConfigStore;
        _providerConfigStore = providerConfigStore;
        _logger = logger;
        _jobDispatcher = jobDispatcher;
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

        lock (_lock)
        {
            if (IsLoopActive)
                return false;
            if (_orchestration.IsRunning)
                return false;

            var templates = config.PipelineJobTemplates;
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



    /// <summary>
    /// Reconciles the provider cache with the needed set of IssueProviderIds.
    /// Evicts stale entries (disposes before removing), creates missing entries.
    /// </summary>
    private async Task ReconcileProviderCacheAsync(
        HashSet<string> neededIssueProviderIds,
        IReadOnlyList<ProviderConfig> issueProviderConfigs,
        CancellationToken ct)
    {
        // Evict stale entries
        var staleKeys = _providerCache.Keys.Where(k => !neededIssueProviderIds.Contains(k)).ToList();
        foreach (var key in staleKeys)
        {
            if (_providerCache.TryGetValue(key, out var provider))
            {
                try { await provider.DisposeAsync(); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to dispose cached provider {ProviderId}", key); }
                _providerCache.Remove(key);
            }
        }

        // Create missing entries
        foreach (var neededId in neededIssueProviderIds)
        {
            if (!_providerCache.ContainsKey(neededId))
            {
                var config = issueProviderConfigs.FirstOrDefault(c => c.Id == neededId);
                if (config is not null)
                {
                    _providerCache[neededId] = _providerFactory.CreateIssueProvider(config);
                }
            }
        }
    }

    /// <summary>
    /// Reconciles the repository provider cache with the needed set of RepoProviderIds.
    /// Evicts stale entries (disposes before removing), creates missing entries.
    /// </summary>
    private async Task ReconcileRepoProviderCacheAsync(
        HashSet<string> neededRepoProviderIds,
        IReadOnlyList<ProviderConfig> repoProviderConfigs,
        CancellationToken ct)
    {
        // Evict stale entries
        var staleKeys = _repoProviderCache.Keys.Where(k => !neededRepoProviderIds.Contains(k)).ToList();
        foreach (var key in staleKeys)
        {
            if (_repoProviderCache.TryGetValue(key, out var provider))
            {
                try { await provider.DisposeAsync(); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to dispose cached repo provider {ProviderId}", key); }
                _repoProviderCache.Remove(key);
            }
        }

        // Create missing entries
        foreach (var neededId in neededRepoProviderIds)
        {
            ct.ThrowIfCancellationRequested();

            if (!_repoProviderCache.ContainsKey(neededId))
            {
                var config = repoProviderConfigs.FirstOrDefault(c => c.Id == neededId);
                if (config is not null)
                {
                    try
                    {
                        var provider = _providerFactory.CreateRepositoryProvider(config);
                        if (provider is not null)
                            _repoProviderCache[neededId] = provider;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to create repo provider for {ProviderId}", neededId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Evicts a provider from the cache due to an auth error. Disposes and removes it
    /// so the next cycle recreates a fresh instance.
    /// </summary>
    private async Task EvictProviderOnAuthErrorAsync(string issueProviderId)
    {
        if (_providerCache.TryGetValue(issueProviderId, out var provider))
        {
            try { await provider.DisposeAsync(); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to dispose provider {ProviderId} after auth error", issueProviderId); }
            _providerCache.Remove(issueProviderId);
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

    /// <summary>
    /// Multi-template round-robin loop. Reads config snapshot at cycle start, reconciles
    /// provider cache, polls each enabled template, then dispatches issues fairly.
    /// </summary>
    private async Task RunMultiTemplateLoopAsync(CancellationToken stoppingToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _loopCts?.Token ?? CancellationToken.None);
        var ct = linkedCts.Token;

        while (!_stopRequested && !ct.IsCancellationRequested)
        {
            // Step 1: Snapshot — read config at cycle start (immutable for cycle duration)
            var config = await _pipelineConfigStore.LoadPipelineConfigAsync(ct);
            var pollInterval = config.ClosedLoopPollInterval;
            var maxRunsPerCycle = config.ClosedLoopMaxRunsPerCycle;
            var maxConsecutiveFailures = config.ClosedLoopMaxConsecutivePollFailures;
            var maxPagesToFetch = config.ClosedLoopMaxPagesToFetch;

            var enabledTemplates = config.PipelineJobTemplates.Where(t => t.Enabled).ToList();

            // Filter out rate-limited templates
            var now = DateTimeOffset.UtcNow;
            var pollableTemplates = enabledTemplates.Where(t =>
            {
                if (_templateStatuses.TryGetValue(t.Id, out var status) && status.RateLimitResetAt.HasValue)
                    return now >= status.RateLimitResetAt.Value;
                return true;
            }).ToList();

            CurrentCycleTemplateCount = enabledTemplates.Count;

            // Step 2: Provider cache reconciliation
            var neededIds = enabledTemplates.Select(t => t.IssueProviderId).ToHashSet();
            var issueProviderConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, ct);
            await ReconcileProviderCacheAsync(neededIds, issueProviderConfigs, ct);

            // Reconcile repo provider cache for templates with ReviewEnabled
            var neededRepoIds = enabledTemplates
                .Where(t => t.ReviewEnabled)
                .Select(t => t.RepoProviderId)
                .ToHashSet();
            if (neededRepoIds.Count > 0)
            {
                try
                {
                    var repoProviderConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
                    await ReconcileRepoProviderCacheAsync(neededRepoIds, repoProviderConfigs, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to reconcile repo provider cache, PR polling will be skipped this cycle");
                }
            }

            // Step 3: Poll once per pollable template (issues + PRs)
            var issueQueues = new Dictionary<string, List<IssueSummary>>();
            var prQueues = new Dictionary<string, List<PullRequestSummary>>();
            for (int i = 0; i < pollableTemplates.Count; i++)
            {
                if (_stopRequested || ct.IsCancellationRequested) break;

                var template = pollableTemplates[i];
                CurrentCycleTemplateIndex = i;
                StatusMessage = $"🔄 Polling template '{template.Name}' ({i + 1} of {pollableTemplates.Count})";

                // Mark as currently polling
                _templateStatuses[template.Id] = (_templateStatuses.TryGetValue(template.Id, out var prev) ? prev : ConfigStatusSnapshot.Empty)
                    with { IsCurrentlyPolling = true };
                NotifyChange();

                try
                {
                    // ── Issue polling (only when ImplementationEnabled) ──
                    if (template.ImplementationEnabled)
                    {
                        if (!_providerCache.TryGetValue(template.IssueProviderId, out var provider))
                        {
                            // Provider not in cache (config issue) — skip issues
                            _templateStatuses[template.Id] = new ConfigStatusSnapshot
                            {
                                LastPollTime = DateTimeOffset.UtcNow,
                                LastError = $"Issue provider '{template.IssueProviderId}' not found in cache.",
                                IsCurrentlyPolling = false
                            };
                            issueQueues[template.Id] = new List<IssueSummary>();
                        }
                        else
                        {
                            var issues = await FetchAgentNextIssuesForProviderAsync(provider, maxPagesToFetch, ct);
                            issueQueues[template.Id] = issues;
                        }
                    }
                    else
                    {
                        issueQueues[template.Id] = new List<IssueSummary>();
                    }

                    // ── PR polling (only when ReviewEnabled) ──
                    if (template.ReviewEnabled)
                    {
                        if (!_repoProviderCache.TryGetValue(template.RepoProviderId, out var repoProvider))
                        {
                            _logger.Warning("Template '{TemplateName}': repo provider '{RepoProviderId}' not found in cache, skipping PR polling",
                                template.Name, template.RepoProviderId);
                            prQueues[template.Id] = new List<PullRequestSummary>();
                        }
                        else
                        {
                            var prs = await FetchAgentNextPullRequestsAsync(repoProvider, maxPagesToFetch, ct);
                            prQueues[template.Id] = prs;
                        }
                    }
                    else
                    {
                        prQueues[template.Id] = new List<PullRequestSummary>();
                    }

                    // Success — update status
                    var issueCount = issueQueues[template.Id].Count;
                    var prCount = prQueues[template.Id].Count;
                    _templateStatuses[template.Id] = new ConfigStatusSnapshot
                    {
                        LastPollTime = DateTimeOffset.UtcNow,
                        LastPollIssueCount = issueCount + prCount,
                        LastError = null,
                        ConsecutiveFailures = 0,
                        RateLimitResetAt = null,
                        IsCurrentlyPolling = false
                    };
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (RateLimitExceededException ex)
                {
                    _logger.Warning("Template '{TemplateName}' rate limited until {ResetAt}", template.Name, ex.ResetAt);
                    var prevStatus = _templateStatuses.TryGetValue(template.Id, out var s) ? s : ConfigStatusSnapshot.Empty;
                    _templateStatuses[template.Id] = prevStatus with
                    {
                        LastPollTime = DateTimeOffset.UtcNow,
                        RateLimitResetAt = ex.ResetAt,
                        IsCurrentlyPolling = false
                    };
                    issueQueues[template.Id] = new List<IssueSummary>();
                    prQueues[template.Id] = new List<PullRequestSummary>();
                }
                catch (Exception ex) when (IsAuthError(ex))
                {
                    _logger.Warning(ex, "Template '{TemplateName}' auth error, evicting cached provider", template.Name);
                    await EvictProviderOnAuthErrorAsync(template.IssueProviderId);
                    var prevStatus = _templateStatuses.TryGetValue(template.Id, out var s) ? s : ConfigStatusSnapshot.Empty;
                    _templateStatuses[template.Id] = prevStatus with
                    {
                        LastPollTime = DateTimeOffset.UtcNow,
                        LastError = ex.Message,
                        ConsecutiveFailures = prevStatus.ConsecutiveFailures + 1,
                        IsCurrentlyPolling = false
                    };
                    issueQueues[template.Id] = new List<IssueSummary>();
                    prQueues[template.Id] = new List<PullRequestSummary>();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Template '{TemplateName}' poll failed: {Error}", template.Name, ex.Message);
                    var prevStatus = _templateStatuses.TryGetValue(template.Id, out var s) ? s : ConfigStatusSnapshot.Empty;
                    _templateStatuses[template.Id] = prevStatus with
                    {
                        LastPollTime = DateTimeOffset.UtcNow,
                        LastError = ex.Message,
                        ConsecutiveFailures = prevStatus.ConsecutiveFailures + 1,
                        IsCurrentlyPolling = false
                    };
                    issueQueues[template.Id] = new List<IssueSummary>();
                    prQueues[template.Id] = new List<PullRequestSummary>();
                }
            }

            if (_stopRequested || ct.IsCancellationRequested) break;

            // Step 4: Circuit breaker — trip only when ALL enabled templates are failing
            var allFailing = enabledTemplates.Count > 0 && enabledTemplates.All(t =>
            {
                if (_templateStatuses.TryGetValue(t.Id, out var s))
                    return s.ConsecutiveFailures >= maxConsecutiveFailures;
                return false;
            });

            if (allFailing)
            {
                IsCircuitBroken = true;
                StatusMessage = $"⚠️ Loop paused — all {enabledTemplates.Count} templates failing.";
                NotifyChange();
                _logger.Warning("Circuit breaker tripped: all {Count} enabled templates have {Threshold}+ consecutive failures",
                    enabledTemplates.Count, maxConsecutiveFailures);

                _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try { await _resumeSignal.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { break; }
                if (_stopRequested) break;
                continue;
            }

            // Step 5: Fair dispatch — interleaved round-robin between issues and PRs
            // Alternates between dispatching one round of issues (one per template) and one round
            // of PRs (one per template) to ensure both queue types get fair access to the budget.
            // Within each round, templates are served round-robin (one item per template per pass).
            if (_jobDispatcher != null)
            {
                int remaining = maxRunsPerCycle > 0 ? maxRunsPerCycle : int.MaxValue;

                // Track which queue type to dispatch next (true = issue turn, false = PR turn)
                bool issuesTurn = true;

                while (remaining > 0)
                {
                    if (_stopRequested || ct.IsCancellationRequested) break;

                    bool issueMadeProgress = false;
                    bool prMadeProgress = false;

                    bool hasIssues = HasEligibleIssues(pollableTemplates, issueQueues);
                    bool hasPrs = HasEligiblePrs(pollableTemplates, prQueues);

                    // ── Issue dispatch (one per template per pass) ──
                    if (hasIssues && (issuesTurn || !hasPrs))
                    {
                        foreach (var template in pollableTemplates)
                        {
                            if (remaining <= 0) break;
                            if (_stopRequested || ct.IsCancellationRequested) break;
                            if (!template.ImplementationEnabled) continue;
                            if (!issueQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                                continue;

                            // Dequeue next valid issue
                            IssueSummary? issue = null;
                            while (queue.Count > 0)
                            {
                                var candidate = queue[0];
                                queue.RemoveAt(0);

                                // Skip errored/needs-refinement
                                if (candidate.Labels.Contains(AgentLabels.Error) || candidate.Labels.Contains(AgentLabels.NeedsRefinement))
                                    continue;
                                // Skip already processing
                                if (_orchestration.IsIssueBeingProcessed(candidate.Identifier))
                                    continue;
                                if (_jobDispatcher.IsIssueBeingProcessedOrQueued(candidate.Identifier))
                                    continue;

                                issue = candidate;
                                break;
                            }

                            if (issue is null) continue;

                            CurrentIssueIdentifier = issue.Identifier;
                            StatusMessage = $"🔄 Dispatching #{issue.Identifier} from '{template.Name}'";
                            NotifyChange();

                            try
                            {
                                var dispatched = await _jobDispatcher.TryDispatchAsync(
                                    issue.Identifier,
                                    template.IssueProviderId, template.RepoProviderId,
                                    template.BrainProviderId, template.PipelineProviderId,
                                    initiatedBy: "loop",
                                    stoppingToken,
                                    issueTitle: issue.Title);

                                if (dispatched)
                                {
                                    ProcessedCount++;
                                    remaining--;
                                    issueMadeProgress = true;
                                    _logger.Information("Dispatched issue #{Issue} from template '{Template}'",
                                        issue.Identifier, template.Name);
                                }
                            }
                            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, "Dispatch failed for issue #{Issue} from template '{Template}'",
                                    issue.Identifier, template.Name);
                                FailedCount++;
                                ProcessedCount++;
                                remaining--;
                                issueMadeProgress = true;
                            }
                        }
                    }

                    if (_stopRequested || ct.IsCancellationRequested || remaining <= 0) break;

                    // ── PR review dispatch (one per template per pass) ──
                    if (hasPrs && (!issuesTurn || !hasIssues))
                    {
                        foreach (var template in pollableTemplates)
                        {
                            if (remaining <= 0) break;
                            if (_stopRequested || ct.IsCancellationRequested) break;
                            if (!template.ReviewEnabled) continue;
                            if (!prQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                                continue;

                            // Dequeue next valid PR
                            PullRequestSummary? pr = null;
                            while (queue.Count > 0)
                            {
                                var candidate = queue[0];
                                queue.RemoveAt(0);

                                // Skip drafts
                                if (candidate.IsDraft) continue;
                                // Skip PRs with terminal/in-progress status labels
                                if (candidate.Labels.Contains(AgentLabels.Error) ||
                                    candidate.Labels.Contains(AgentLabels.InProgress) ||
                                    candidate.Labels.Contains(AgentLabels.Done) ||
                                    candidate.Labels.Contains(AgentLabels.Cancelled))
                                    continue;
                                // Skip already processing
                                if (_orchestration.IsIssueBeingProcessed(candidate.Identifier))
                                    continue;
                                if (_jobDispatcher.IsIssueBeingProcessedOrQueued(candidate.Identifier))
                                    continue;

                                pr = candidate;
                                break;
                            }

                            if (pr is null) continue;

                            CurrentIssueIdentifier = pr.Identifier;
                            StatusMessage = $"🔄 Dispatching PR #{pr.Identifier} review from '{template.Name}'";
                            NotifyChange();

                            try
                            {
                                var dispatched = await _jobDispatcher.TryDispatchReviewAsync(
                                    pr.Identifier,
                                    pr.BranchName,
                                    pr.Title,
                                    pr.Description,
                                    pr.Url,
                                    pr.TargetBranch,
                                    template.IssueProviderId,
                                    template.RepoProviderId,
                                    template.BrainProviderId,
                                    initiatedBy: "loop",
                                    stoppingToken);

                                if (dispatched)
                                {
                                    ProcessedCount++;
                                    remaining--;
                                    prMadeProgress = true;
                                    _logger.Information("Dispatched PR #{PrIdentifier} review from template '{Template}'",
                                        pr.Identifier, template.Name);
                                }
                            }
                            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, "Dispatch failed for PR #{PrIdentifier} review from template '{Template}'",
                                    pr.Identifier, template.Name);
                                FailedCount++;
                                ProcessedCount++;
                                remaining--;
                                prMadeProgress = true;
                            }
                        }
                    }

                    // If neither queue made progress, both are exhausted
                    if (!issueMadeProgress && !prMadeProgress) break;

                    // Alternate turns for next iteration
                    issuesTurn = !issuesTurn;
                }
            }

            CurrentIssueIdentifier = null;
            if (_stopRequested || ct.IsCancellationRequested) break;

            // Step 6: Wait before next cycle
            StatusMessage = $"🔄 Cycle complete. Polling {enabledTemplates.Count} templates every {(int)pollInterval.TotalSeconds}s.";
            NotifyChange();
            await DelayOrStop(pollInterval, ct);
        }
    }

    /// <summary>
    /// Checks whether any pollable template has eligible issues remaining in its queue.
    /// Used by the fair alternation logic to allow the other queue to consume remaining budget.
    /// </summary>
    private static bool HasEligibleIssues(
        List<PipelineJobTemplate> pollableTemplates,
        Dictionary<string, List<IssueSummary>> issueQueues)
    {
        foreach (var template in pollableTemplates)
        {
            if (!template.ImplementationEnabled) continue;
            if (issueQueues.TryGetValue(template.Id, out var queue) && queue.Count > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks whether any pollable template has eligible PRs remaining in its queue.
    /// Used by the fair alternation logic to allow the other queue to consume remaining budget.
    /// </summary>
    private static bool HasEligiblePrs(
        List<PipelineJobTemplate> pollableTemplates,
        Dictionary<string, List<PullRequestSummary>> prQueues)
    {
        foreach (var template in pollableTemplates)
        {
            if (!template.ReviewEnabled) continue;
            if (prQueues.TryGetValue(template.Id, out var queue) && queue.Count > 0)
                return true;
        }
        return false;
    }

    /// <summary>Determines if an exception is an auth-related error (401/403/credential).</summary>
    private static bool IsAuthError(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            var statusCode = httpEx.StatusCode;
            return statusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
        }
        // Check for common auth-related exception messages
        var msg = ex.Message.ToLowerInvariant();
        return msg.Contains("unauthorized") || msg.Contains("forbidden") || msg.Contains("credential");
    }

    /// <summary>Fetches agent:next issues from a specific provider (used in multi-template mode).</summary>
    private async Task<List<IssueSummary>> FetchAgentNextIssuesForProviderAsync(
        IIssueProvider provider, int maxPages, CancellationToken ct)
    {
        var result = new List<IssueSummary>();
        int page = 1;
        const int pageSize = PipelineConstants.DefaultPageSize;

        while (true)
        {
            var pagedResult = await provider.ListOpenIssuesAsync(page, pageSize,
                new[] { AgentLabels.Next }, ct);
            result.AddRange(pagedResult.Items);
            if (!pagedResult.HasMore) break;
            if (page >= maxPages) break;
            page++;
        }

        // FIFO: oldest first
        result.Sort((a, b) =>
        {
            var aDate = a.CreatedAt ?? DateTime.MaxValue;
            var bDate = b.CreatedAt ?? DateTime.MaxValue;
            return aDate.CompareTo(bDate);
        });

        return result;
    }

    /// <summary>
    /// Fetches agent:next pull requests from a repository provider, filters out ineligible PRs,
    /// and orders by CreatedAt ascending (FIFO). PRs without CreatedAt sort last.
    /// </summary>
    private async Task<List<PullRequestSummary>> FetchAgentNextPullRequestsAsync(
        IRepositoryProvider repoProvider, int maxPages, CancellationToken ct)
    {
        var result = new List<PullRequestSummary>();
        int page = 1;
        const int pageSize = PipelineConstants.DefaultPageSize;

        while (true)
        {
            var pagedResult = await repoProvider.ListOpenPullRequestsAsync(page, pageSize,
                new[] { AgentLabels.Next }, ct);
            result.AddRange(pagedResult.Items);
            if (!pagedResult.HasMore) break;
            if (page >= maxPages) break;
            page++;
        }

        // Filter: skip drafts, skip PRs with terminal/in-progress status labels
        result.RemoveAll(pr =>
            pr.IsDraft ||
            pr.Labels.Contains(AgentLabels.Error) ||
            pr.Labels.Contains(AgentLabels.InProgress) ||
            pr.Labels.Contains(AgentLabels.Done) ||
            pr.Labels.Contains(AgentLabels.Cancelled));

        // FIFO: oldest first, PRs without CreatedAt go last
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

    private void NotifyChange()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { _logger.Warning(ex, "PipelineLoopService OnChange handler threw"); }
    }
}
