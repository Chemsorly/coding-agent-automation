using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

public sealed partial class PipelineLoopService
{
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
            var snapshot = await SnapshotAndReconcileAsync(ct);
            if (snapshot is null)
            {
                PipelineTelemetry.LoopPolls.Add(1, new KeyValuePair<string, object?>("result", "failure"));
                await DelayOrStop(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            if (!await ExecuteCycleAsync(snapshot, stoppingToken, ct))
                break;
        }
    }

    /// <summary>
    /// Executes a single pipeline loop cycle: poll → circuit-breaker → dispatch → wait.
    /// Returns false if the loop should stop immediately (cancellation or stop requested).
    /// </summary>
    private async Task<bool> ExecuteCycleAsync(CycleSnapshot snapshot, CancellationToken stoppingToken, CancellationToken ct)
    {
        var failuresBefore = BuildTemplateFailureBaseline(snapshot.PollableTemplates);

        var (issueQueues, prQueues, decompositionQueues) = await _poller.PollTemplateQueuesAsync(
            snapshot.PollableTemplates, snapshot.MaxPagesToFetch, _templateStatuses,
            i => CurrentCycleTemplateIndex = i,
            msg => { lock (_lock) { StatusMessage = msg; } },
            NotifyChange,
            ct);

        if (_stopRequested || ct.IsCancellationRequested) return false;

        var projectLevelDecompositionQueues = await _poller.PollProjectLevelEpicsAsync(
            snapshot.Projects, snapshot.TemplateLookup, snapshot.MaxPagesToFetch, ct);

        if (_stopRequested || ct.IsCancellationRequested) return false;

        EmitCyclePollMetrics(snapshot, failuresBefore, issueQueues, prQueues, decompositionQueues, projectLevelDecompositionQueues);

        if (await CheckCircuitBreakerAsync(snapshot.EnabledTemplates, snapshot.MaxConsecutiveFailures, snapshot.Config.ClosedLoopCircuitBreakerCooldown, ct))
            return true;

        var dispatchResult = await _dispatcher.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = snapshot.PollableTemplates,
                FlattenedTemplates = snapshot.FlattenedTemplates,
                Config = snapshot.Config,
                MaxRunsPerCycle = snapshot.MaxRunsPerCycle,
                ActiveIssueIdentifiers = snapshot.ActiveIssueIdentifiers,
                IssueQueues = issueQueues,
                PrQueues = prQueues,
                DecompositionQueues = decompositionQueues,
                ProjectLevelDecompositionQueues = projectLevelDecompositionQueues,
                ReportStatus = msg => { lock (_lock) { StatusMessage = msg; } },
                ReportIssue = id => CurrentIssueIdentifier = id,
                NotifyChange = NotifyChange
            },
            stoppingToken, ct);

        ProcessedCount += dispatchResult.ProcessedCount;
        FailedCount += dispatchResult.FailedCount;
        CurrentIssueIdentifier = null;

        if (_stopRequested || ct.IsCancellationRequested) return false;

        lock (_lock) { StatusMessage = $"🔄 Cycle complete. Polling {snapshot.EnabledTemplates.Count} templates every {(int)snapshot.PollInterval.TotalSeconds}s."; }
        NotifyChange();
        await DelayOrStop(snapshot.PollInterval, ct);
        return true;
    }

    private Dictionary<string, int> BuildTemplateFailureBaseline(IReadOnlyList<PipelineJobTemplate> templates)
    {
        return templates.DistinctBy(t => t.Id).ToDictionary(
            t => t.Id,
            t => _templateStatuses.TryGetValue(t.Id, out var s) ? s.ConsecutiveFailures : 0);
    }

    /// <summary>
    /// Emits cycle-level poll telemetry: overall poll result and total items found.
    /// </summary>
    private void EmitCyclePollMetrics(
        CycleSnapshot snapshot,
        Dictionary<string, int> failuresBefore,
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>> projectLevelDecompositionQueues)
    {
        var totalItemsFound = issueQueues.Values.Sum(q => q.Count)
            + prQueues.Values.Sum(q => q.Count)
            + decompositionQueues.Values.Sum(q => q.Count)
            + projectLevelDecompositionQueues.Values.Sum(q => q.Count);

        var templatePollFailures = snapshot.PollableTemplates.Count(t =>
        {
            var before = failuresBefore[t.Id];
            var after = _templateStatuses.TryGetValue(t.Id, out var s) ? s.ConsecutiveFailures : 0;
            return after > before;
        });

        var pollResult = templatePollFailures == 0 ? "success"
            : snapshot.PollableTemplates.Count > 0 && templatePollFailures >= snapshot.PollableTemplates.Count ? "failure"
            : "partial_failure";

        PipelineTelemetry.LoopPolls.Add(1, new KeyValuePair<string, object?>("result", pollResult));
        if (totalItemsFound > 0)
            PipelineTelemetry.LoopIssuesFound.Add(totalItemsFound);
    }

    /// <summary>
    /// Snapshot record bundling all cycle-immutable state from Steps 1–2.
    /// </summary>
    private sealed record CycleSnapshot(
        PipelineConfiguration Config,
        IReadOnlyList<PipelineProject> Projects,
        IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> FlattenedTemplates,
        IReadOnlyList<PipelineJobTemplate> EnabledTemplates,
        IReadOnlyList<PipelineJobTemplate> PollableTemplates,
        IReadOnlyDictionary<string, PipelineJobTemplate> TemplateLookup,
        TimeSpan PollInterval,
        int MaxRunsPerCycle,
        int MaxConsecutiveFailures,
        int MaxPagesToFetch,
        HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)> ActiveIssueIdentifiers);

    /// <summary>
    /// Step 1–2: Reads config snapshot, loads projects, flattens templates, filters rate-limited,
    /// and reconciles provider caches. Returns null if no templates are available.
    /// </summary>
    private async Task<CycleSnapshot?> SnapshotAndReconcileAsync(CancellationToken ct)
    {
        // Step 1: Snapshot — read config at cycle start (immutable for cycle duration)
        var config = await _pipelineConfigStore.LoadPipelineConfigAsync(ct);

        var (projects, flattenedTemplates, enabledTemplates, pollableTemplates, templateLookup) =
            await LoadAndFlattenTemplatesAsync(ct);

        CurrentCycleTemplateCount = enabledTemplates.Count;

        // Step 2: Provider cache reconciliation
        await ReconcileIssueProviderCacheAsync(enabledTemplates, projects, ct);
        await ReconcileRepoProviderCacheAsync(enabledTemplates, ct);

        // Step 2b: Batch-load active issue identifiers and reconcile stuck work items
        var activeIssueIdentifiers = await LoadActiveIssueIdentifiersAsync(ct);
        await ReconcileStuckWorkItemsAsync(ct);

        return new CycleSnapshot(
            config, projects, flattenedTemplates, enabledTemplates.AsReadOnly(), pollableTemplates.AsReadOnly(),
            templateLookup.AsReadOnly(),
            config.ClosedLoopPollInterval, config.ClosedLoopMaxRunsPerCycle,
            config.ClosedLoopMaxConsecutivePollFailures, config.ClosedLoopMaxPagesToFetch,
            activeIssueIdentifiers);
    }

    /// <summary>Loads projects and templates, deduplicates, flattens, and filters rate-limited templates.</summary>
    private async Task<(
        IReadOnlyList<PipelineProject> Projects,
        IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> FlattenedTemplates,
        List<PipelineJobTemplate> EnabledTemplates,
        List<PipelineJobTemplate> PollableTemplates,
        Dictionary<string, PipelineJobTemplate> TemplateLookup)>
        LoadAndFlattenTemplatesAsync(CancellationToken ct)
    {
        var projects = await _projectStore.LoadProjectsAsync(ct) ?? (IReadOnlyList<PipelineProject>)[];
        var allTemplates = await _projectStore.LoadAllTemplatesAsync(ct);
        var deduplicatedTemplates = allTemplates.DistinctBy(t => t.Id).ToList();
        if (deduplicatedTemplates.Count != allTemplates.Count)
            _logger.Warning("Duplicate template IDs detected in store ({Total} loaded, {Unique} unique) — using first occurrence",
                allTemplates.Count, deduplicatedTemplates.Count);

        var flattenedTemplates = FlattenTemplates(projects, deduplicatedTemplates);
        var enabledTemplates = flattenedTemplates.Select(ft => ft.Template).ToList();
        var templateLookup = deduplicatedTemplates.ToDictionary(t => t.Id);

        var now = DateTimeOffset.UtcNow;
        var pollableTemplates = flattenedTemplates.Where(ft =>
        {
            if (_templateStatuses.TryGetValue(ft.Template.Id, out var status) && status.RateLimitResetAt.HasValue)
                return now >= status.RateLimitResetAt.Value;
            return true;
        }).Select(pe => pe.Template).ToList();

        return (projects, flattenedTemplates, enabledTemplates, pollableTemplates, templateLookup);
    }

    /// <summary>Reconciles the issue provider cache, including project-level epic providers.</summary>
    private async Task ReconcileIssueProviderCacheAsync(
        List<PipelineJobTemplate> enabledTemplates,
        IReadOnlyList<PipelineProject> projects,
        CancellationToken ct)
    {
        var neededIds = enabledTemplates.Select(t => t.IssueProviderId).ToHashSet();

        // Include project-level EpicIssueProviderId values so the cache contains epic providers for polling
        foreach (var project in projects.Where(p => p.Enabled && !string.IsNullOrEmpty(p.EpicIssueProviderId)))
            neededIds.Add(project.EpicIssueProviderId!);

        var issueProviderConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, ct);
        await _cacheManager.ReconcileIssueProvidersAsync(neededIds, issueProviderConfigs, ct);
    }

    /// <summary>Reconciles the repo provider cache for templates with ReviewEnabled or DecompositionEnabled.</summary>
    private async Task ReconcileRepoProviderCacheAsync(List<PipelineJobTemplate> enabledTemplates, CancellationToken ct)
    {
        var neededRepoIds = enabledTemplates
            .Where(t => t.ReviewEnabled || t.DecompositionEnabled)
            .Select(t => t.RepoProviderId)
            .ToHashSet();
        if (neededRepoIds.Count == 0) return;

        try
        {
            var repoProviderConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
            await _cacheManager.ReconcileRepoProvidersAsync(neededRepoIds, repoProviderConfigs, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to reconcile repo provider cache, PR polling will be skipped this cycle");
        }
    }

    /// <summary>
    /// Batch-loads active issue identifiers for O(1) dedup checks per issue.
    /// Returns empty set if distributor unavailable or on error.
    /// </summary>
    private async Task<HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)>> LoadActiveIssueIdentifiersAsync(CancellationToken ct)
    {
        if (_workDistributor is null)
            return new HashSet<(IssueIdentifier, ProviderConfigId)>();

        try
        {
            return await _workDistributor.GetActiveIssueIdentifiersAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load active issue identifiers — proceeding with empty dedup set (may cause duplicate dispatch attempts)");
            return new HashSet<(IssueIdentifier, ProviderConfigId)>();
        }
    }

    /// <summary>Detects and remediates stuck work items (SignalR mode: Dispatched > 5min → Failed).</summary>
    private async Task ReconcileStuckWorkItemsAsync(CancellationToken ct)
    {
        if (_workDistributor is null) return;

        try
        {
            var stuckCount = await _workDistributor.ReconcileStuckItemsAsync(ct);
            if (stuckCount > 0)
                _logger.Information("Reconciled {StuckCount} stuck work items at cycle start", stuckCount);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to reconcile stuck work items at cycle start");
        }
    }

    /// <summary>
    /// Step 4: Circuit breaker — trips only when ALL enabled templates are failing.
    /// Returns true if the circuit breaker tripped (caller should continue to next cycle).
    /// </summary>
    private async Task<bool> CheckCircuitBreakerAsync(
        IReadOnlyList<PipelineJobTemplate> enabledTemplates,
        int maxConsecutiveFailures,
        TimeSpan cooldown,
        CancellationToken ct)
    {
        // Build failure counts from _templateStatuses for the circuit breaker to evaluate.
        // Note: This allocates a dictionary per poll cycle, but given poll intervals are typically
        // seconds (default 30s), the allocation cost is negligible vs. the I/O in each cycle.
        var failureCounts = new Dictionary<string, int>(enabledTemplates.Count);
        foreach (var t in enabledTemplates)
        {
            var failures = _templateStatuses.TryGetValue(t.Id, out var s) ? s.ConsecutiveFailures : 0;
            failureCounts[t.Id] = failures;
        }

        // Delegate decision to circuit breaker (pure query — no state mutation)
        if (!_circuitBreaker.Evaluate(failureCounts, maxConsecutiveFailures))
            return false;

        // TRIP — execution logic stays in PipelineLoopService
        Task resumeTask;
        lock (_lock)
        {
            // TODO: Pass a descriptive error message to Trip() so that LastPollError surfaces useful info to the UI
            _circuitBreaker.Trip();
            StatusMessage = $"⚠️ Loop paused — all {enabledTemplates.Count} templates failing. Auto-resume in {cooldown.TotalMinutes:0.#} min.";
            _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            resumeTask = _resumeSignal.Task;
        }
        PipelineTelemetry.LoopCircuitBreakerTrips.Add(1);
        NotifyChange();
        _logger.Warning("Circuit breaker tripped: all {Count} enabled templates have {Threshold}+ consecutive failures. Auto-resume in {Cooldown}",
            enabledTemplates.Count, maxConsecutiveFailures, cooldown);

        // WAIT for manual resume or cooldown
        try { await Task.WhenAny(resumeTask, Task.Delay(cooldown, ct)); }
        catch (OperationCanceledException) { return true; }

        if (_stopRequested) return true;

        // AUTO-RESUME (if ResumeLoop() hasn't already reset it)
        lock (_lock)
        {
            if (!_circuitBreaker.IsTripped) return true; // ResumeLoop() already handled
            _circuitBreaker.Reset();
            StatusMessage = "🔄 Circuit breaker auto-resumed, retrying poll.";
        }

        // Reset per-template failure counters
        foreach (var template in enabledTemplates)
        {
            if (_templateStatuses.TryGetValue(template.Id, out var status) && status.ConsecutiveFailures > 0)
                _templateStatuses[template.Id] = status with { ConsecutiveFailures = 0, LastError = null };
        }

        NotifyChange();
        return true;
    }

    private async Task DelayOrStop(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await Task.Delay(interval, ct);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Flattens all enabled projects' templates into a single ordered list.
    /// Order: projects alphabetical by Name, templates by TemplateIds position.
    /// Templates are loaded from IProjectStore.LoadAllTemplatesAsync.
    /// Skips disabled projects entirely. Skips missing template IDs with a warning.
    /// Only includes templates that are individually enabled.
    /// </summary>
    internal IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> FlattenTemplates(
        IReadOnlyList<PipelineProject> projects,
        IReadOnlyList<PipelineJobTemplate> templates)
    {
        var result = new List<(PipelineJobTemplate, PipelineProject)>();
        // Build lookup for O(1) template resolution
        var templateLookup = templates.ToDictionary(t => t.Id);

        foreach (var project in projects.Where(p => p.Enabled).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            foreach (var templateId in project.TemplateIds)
            {
                if (!templateLookup.TryGetValue(templateId, out var template))
                {
                    _logger.Warning("Project '{ProjectName}' references template '{TemplateId}' which does not exist, skipping",
                        project.Name, templateId);
                    continue;
                }
                if (template.Enabled)
                    result.Add((template, project));
            }
        }

        return result;
    }
}
