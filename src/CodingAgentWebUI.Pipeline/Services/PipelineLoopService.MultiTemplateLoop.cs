using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

public sealed partial class PipelineLoopService
{
    /// <summary>
    /// Generic cache reconciliation: evicts stale entries (disposes before removing),
    /// creates missing entries via the factory delegate.
    /// </summary>
    private async Task ReconcileCacheAsync<TProvider>(
        Dictionary<string, TProvider> cache,
        HashSet<string> neededIds,
        IReadOnlyList<ProviderConfig> providerConfigs,
        Func<ProviderConfig, TProvider> factory,
        string providerKindLabel,
        CancellationToken ct) where TProvider : IAsyncDisposable
    {
        // Evict stale entries
        var staleKeys = cache.Keys.Where(k => !neededIds.Contains(k)).ToList();
        foreach (var key in staleKeys)
        {
            if (cache.TryGetValue(key, out var provider))
            {
                try { await provider.DisposeAsync(); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to dispose cached {Kind} provider {ProviderId}", providerKindLabel, key); }
                cache.Remove(key);
            }
        }

        // Create missing entries
        foreach (var neededId in neededIds)
        {
            ct.ThrowIfCancellationRequested();

            if (!cache.ContainsKey(neededId))
            {
                var config = providerConfigs.FirstOrDefault(c => c.Id == neededId);
                if (config is not null)
                {
                    try
                    {
                        cache[neededId] = factory(config);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to create {Kind} provider for {ProviderId}", providerKindLabel, neededId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reconciles the provider cache with the needed set of IssueProviderIds.
    /// </summary>
    private Task ReconcileProviderCacheAsync(
        HashSet<string> neededIssueProviderIds,
        IReadOnlyList<ProviderConfig> issueProviderConfigs,
        CancellationToken ct)
        => ReconcileCacheAsync(_providerCache, neededIssueProviderIds, issueProviderConfigs,
            _providerFactory.CreateIssueProvider, "issue", ct);

    /// <summary>
    /// Reconciles the repository provider cache with the needed set of RepoProviderIds.
    /// </summary>
    private Task ReconcileRepoProviderCacheAsync(
        HashSet<string> neededRepoProviderIds,
        IReadOnlyList<ProviderConfig> repoProviderConfigs,
        CancellationToken ct)
        => ReconcileCacheAsync(_repoProviderCache, neededRepoProviderIds, repoProviderConfigs,
            _providerFactory.CreateRepositoryProvider, "repo", ct);

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
            // Step 1–2: Snapshot config and reconcile provider caches
            var snapshot = await SnapshotAndReconcileAsync(ct);
            if (snapshot is null)
            {
                PipelineTelemetry.LoopPolls.Add(1, new KeyValuePair<string, object?>("result", "failure"));
                await DelayOrStop(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            // Step 3: Poll per-template queues (issues + PRs + decomposition)
            var (issueQueues, prQueues, decompositionQueues) = await PollTemplateQueuesAsync(
                snapshot.PollableTemplates, snapshot.MaxPagesToFetch, ct);

            if (_stopRequested || ct.IsCancellationRequested) break;

            // Step 3b: Project-level epic polling
            var projectLevelDecompositionQueues = await PollProjectLevelEpicsAsync(
                snapshot.Projects, snapshot.TemplateLookup, snapshot.MaxPagesToFetch, ct);

            if (_stopRequested || ct.IsCancellationRequested) break;

            // Emit cycle-level poll metrics
            // TODO: totalItemsFound does not include projectLevelDecompositionQueues — issues_found under-reports when project-level epics are found
            var totalItemsFound = issueQueues.Values.Sum(q => q.Count)
                + prQueues.Values.Sum(q => q.Count)
                + decompositionQueues.Values.Sum(q => q.Count);
            // TODO: LoopPolls emits "success" even when individual template polls failed (caught per-template). Consider emitting "failure" when all templates failed within the cycle.
            PipelineTelemetry.LoopPolls.Add(1, new KeyValuePair<string, object?>("result", "success"));
            if (totalItemsFound > 0)
                PipelineTelemetry.LoopIssuesFound.Add(totalItemsFound);

            // Step 4: Circuit breaker
            if (await CheckCircuitBreakerAsync(snapshot.EnabledTemplates, snapshot.MaxConsecutiveFailures, ct))
                continue;

            // Step 5: Fair dispatch
            await DispatchFairRoundRobinAsync(
                snapshot.PollableTemplates, snapshot.FlattenedTemplates, snapshot.Config,
                snapshot.MaxRunsPerCycle,
                issueQueues, prQueues, decompositionQueues, projectLevelDecompositionQueues,
                stoppingToken, ct);

            CurrentIssueIdentifier = null;
            if (_stopRequested || ct.IsCancellationRequested) break;

            // Step 6: Wait before next cycle
            StatusMessage = $"🔄 Cycle complete. Polling {snapshot.EnabledTemplates.Count} templates every {(int)snapshot.PollInterval.TotalSeconds}s.";
            NotifyChange();
            await DelayOrStop(snapshot.PollInterval, ct);
        }
    }

    /// <summary>
    /// Snapshot record bundling all cycle-immutable state from Steps 1–2.
    /// </summary>
    // TODO: Use IReadOnlyList<PipelineJobTemplate> for EnabledTemplates/PollableTemplates and IReadOnlyDictionary for TemplateLookup to enforce immutability
    private sealed record CycleSnapshot(
        PipelineConfiguration Config,
        IReadOnlyList<PipelineProject> Projects,
        IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> FlattenedTemplates,
        List<PipelineJobTemplate> EnabledTemplates,
        List<PipelineJobTemplate> PollableTemplates,
        Dictionary<string, PipelineJobTemplate> TemplateLookup,
        TimeSpan PollInterval,
        int MaxRunsPerCycle,
        int MaxConsecutiveFailures,
        int MaxPagesToFetch);

    /// <summary>
    /// Step 1–2: Reads config snapshot, loads projects, flattens templates, filters rate-limited,
    /// and reconciles provider caches. Returns null if no templates are available.
    /// </summary>
    private async Task<CycleSnapshot?> SnapshotAndReconcileAsync(CancellationToken ct)
    {
        // Step 1: Snapshot — read config at cycle start (immutable for cycle duration)
        var config = await _pipelineConfigStore.LoadPipelineConfigAsync(ct);
        var pollInterval = config.ClosedLoopPollInterval;
        var maxRunsPerCycle = config.ClosedLoopMaxRunsPerCycle;
        var maxConsecutiveFailures = config.ClosedLoopMaxConsecutivePollFailures;
        var maxPagesToFetch = config.ClosedLoopMaxPagesToFetch;

        // Load projects and flatten templates using project-based ordering
        var projects = await _projectStore.LoadProjectsAsync(ct) ?? (IReadOnlyList<PipelineProject>)[];
        var flattenedTemplates = FlattenTemplates(projects, config);
        var enabledTemplates = flattenedTemplates.Select(ft => ft.Template).ToList();

        // Pre-built lookup shared by FlattenTemplates logic and SelectDecompositionTemplate
        var templateLookup = config.PipelineJobTemplates.ToDictionary(t => t.Id);

        // Filter out rate-limited templates
        var now = DateTimeOffset.UtcNow;
        var pollableEntries = flattenedTemplates.Where(ft =>
        {
            if (_templateStatuses.TryGetValue(ft.Template.Id, out var status) && status.RateLimitResetAt.HasValue)
                return now >= status.RateLimitResetAt.Value;
            return true;
        }).ToList();
        var pollableTemplates = pollableEntries.Select(pe => pe.Template).ToList();

        CurrentCycleTemplateCount = enabledTemplates.Count;

        // Step 2: Provider cache reconciliation
        var neededIds = enabledTemplates.Select(t => t.IssueProviderId).ToHashSet();

        // Include project-level EpicIssueProviderId values so the cache contains epic providers for polling
        foreach (var project in projects.Where(p => p.Enabled && !string.IsNullOrEmpty(p.EpicIssueProviderId)))
            neededIds.Add(project.EpicIssueProviderId!);

        var issueProviderConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, ct);
        await ReconcileProviderCacheAsync(neededIds, issueProviderConfigs, ct);

        // Reconcile repo provider cache for templates with ReviewEnabled or DecompositionEnabled
        var neededRepoIds = enabledTemplates
            .Where(t => t.ReviewEnabled || t.DecompositionEnabled)
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

        return new CycleSnapshot(
            config, projects, flattenedTemplates, enabledTemplates, pollableTemplates,
            templateLookup, pollInterval, maxRunsPerCycle, maxConsecutiveFailures, maxPagesToFetch);
    }

    /// <summary>
    /// Step 3: Polls once per pollable template for issues, PRs, and decomposition candidates.
    /// </summary>
    private async Task<(Dictionary<string, List<IssueSummary>> IssueQueues, Dictionary<string, List<PullRequestSummary>> PrQueues, Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> DecompositionQueues)>
        PollTemplateQueuesAsync(List<PipelineJobTemplate> pollableTemplates, int maxPagesToFetch, CancellationToken ct)
    {
        var issueQueues = new Dictionary<string, List<IssueSummary>>();
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var decompositionQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>();

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
                // Wrapped in its own try-catch so that a PR polling failure does not
                // discard the already-fetched issue queue for this template.
                prQueues[template.Id] = new List<PullRequestSummary>();
                if (template.ReviewEnabled)
                {
                    try
                    {
                        if (!_repoProviderCache.TryGetValue(template.RepoProviderId, out var repoProvider))
                        {
                            _logger.Warning("Template '{TemplateName}': repo provider '{RepoProviderId}' not found in cache, skipping PR polling",
                                template.Name, template.RepoProviderId);
                        }
                        else
                        {
                            var prs = await FetchAgentNextPullRequestsAsync(repoProvider, maxPagesToFetch, ct);
                            prQueues[template.Id] = prs;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Template '{TemplateName}' PR polling failed, issue polling unaffected: {Error}",
                            template.Name, ex.Message);
                    }
                }

                // ── Decomposition polling (only when DecompositionEnabled) ──
                // Wrapped in its own try-catch so that a decomposition polling failure does not
                // discard the already-fetched issue/PR queues for this template.
                decompositionQueues[template.Id] = new List<(IssueSummary, PipelineRunType)>();
                if (template.DecompositionEnabled)
                {
                    try
                    {
                        if (!_providerCache.TryGetValue(template.IssueProviderId, out var decompProvider))
                        {
                            _logger.Warning("Template '{TemplateName}': issue provider '{IssueProviderId}' not found in cache, skipping decomposition polling",
                                template.Name, template.IssueProviderId);
                        }
                        else
                        {
                            // Validate that RepoProviderId references an existing provider config (Req 1.3)
                            // IssueProviderId is already validated by the provider cache lookup above.
                            if (!_repoProviderCache.ContainsKey(template.RepoProviderId))
                            {
                                _logger.Warning("Template '{TemplateName}': decomposition skipped — RepoProviderId '{RepoProviderId}' references non-existent provider config",
                                    template.Name, template.RepoProviderId);
                            }
                            else
                            {
                                // Poll for agent:epic issues (Phase 1 candidates)
                                var epicIssues = await FetchEpicIssuesAsync(decompProvider, AgentLabels.Epic, maxPagesToFetch, ct);
                                foreach (var epic in epicIssues)
                                    decompositionQueues[template.Id].Add((epic, PipelineRunType.DecompositionAnalysis));

                                // Poll for agent:epic-approved issues (Phase 2 candidates)
                                var approvedIssues = await FetchEpicIssuesAsync(decompProvider, AgentLabels.EpicApproved, maxPagesToFetch, ct);
                                foreach (var approved in approvedIssues)
                                    decompositionQueues[template.Id].Add((approved, PipelineRunType.Decomposition));
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Template '{TemplateName}' decomposition polling failed, issue/PR polling unaffected: {Error}",
                            template.Name, ex.Message);
                    }
                }

                // Success — update status
                var issueCount = issueQueues[template.Id].Count;
                var prCount = prQueues[template.Id].Count;
                var decompCount = decompositionQueues[template.Id].Count;
                _templateStatuses[template.Id] = new ConfigStatusSnapshot
                {
                    LastPollTime = DateTimeOffset.UtcNow,
                    LastPollIssueCount = issueCount + prCount + decompCount,
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
                ClearQueuesForTemplate(template.Id, issueQueues, prQueues, decompositionQueues);
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
                PipelineTelemetry.LoopBackoffEvents.Add(1);
                ClearQueuesForTemplate(template.Id, issueQueues, prQueues, decompositionQueues);
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
                PipelineTelemetry.LoopBackoffEvents.Add(1);
                ClearQueuesForTemplate(template.Id, issueQueues, prQueues, decompositionQueues);
            }
        }

        return (issueQueues, prQueues, decompositionQueues);
    }

    /// <summary>
    /// Step 3b: Project-level epic polling — polls EpicIssueProviderId for each enabled project
    /// that has the field set and at least one decomposition-enabled template.
    /// This is separate from per-template decomposition polling (which uses the template's own IssueProviderId).
    /// </summary>
    private async Task<Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>>>
        PollProjectLevelEpicsAsync(
            IReadOnlyList<PipelineProject> projects,
            Dictionary<string, PipelineJobTemplate> templateLookup,
            int maxPagesToFetch,
            CancellationToken ct)
    {
        var projectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>>();

        foreach (var project in projects.Where(p => p.Enabled && !string.IsNullOrEmpty(p.EpicIssueProviderId)))
        {
            if (_stopRequested || ct.IsCancellationRequested) break;

            var epicProviderId = project.EpicIssueProviderId!;

            // Validate that EpicIssueProviderId references an existing provider config in the cache
            if (!_providerCache.TryGetValue(epicProviderId, out var epicProvider))
            {
                _logger.Warning("Project '{ProjectName}': EpicIssueProviderId '{EpicProviderId}' not found in provider cache, skipping project-level epic polling",
                    project.Name, epicProviderId);
                continue;
            }

            // Select the first decomposition-enabled template in the project
            var decompositionTemplate = SelectDecompositionTemplate(project, templateLookup);
            if (decompositionTemplate is null)
            {
                _logger.Warning("Project '{ProjectName}': no decomposition-enabled template found, skipping project-level epic polling",
                    project.Name);
                continue;
            }

            try
            {
                var projectQueue = new List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>();

                // Poll for agent:epic issues (Phase 1 candidates)
                var epicIssues = await FetchEpicIssuesAsync(epicProvider, AgentLabels.Epic, maxPagesToFetch, ct);
                foreach (var epic in epicIssues)
                    projectQueue.Add((epic, PipelineRunType.DecompositionAnalysis, decompositionTemplate));

                // Poll for agent:epic-approved issues (Phase 2 candidates)
                var approvedIssues = await FetchEpicIssuesAsync(epicProvider, AgentLabels.EpicApproved, maxPagesToFetch, ct);
                foreach (var approved in approvedIssues)
                    projectQueue.Add((approved, PipelineRunType.Decomposition, decompositionTemplate));

                if (projectQueue.Count > 0)
                    projectLevelDecompositionQueues[project.Id] = projectQueue;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Project '{ProjectName}' project-level epic polling failed: {Error}",
                    project.Name, ex.Message);
            }
        }

        return projectLevelDecompositionQueues;
    }

    /// <summary>
    /// Step 4: Circuit breaker — trips only when ALL enabled templates are failing.
    /// Returns true if the circuit breaker tripped (caller should continue to next cycle).
    /// </summary>
    private async Task<bool> CheckCircuitBreakerAsync(
        List<PipelineJobTemplate> enabledTemplates,
        int maxConsecutiveFailures,
        CancellationToken ct)
    {
        var allFailing = enabledTemplates.Count > 0 && enabledTemplates.All(t =>
        {
            if (_templateStatuses.TryGetValue(t.Id, out var s))
                return s.ConsecutiveFailures >= maxConsecutiveFailures;
            return false;
        });

        if (!allFailing) return false;

        IsCircuitBroken = true;
        PipelineTelemetry.LoopCircuitBreakerTrips.Add(1);
        StatusMessage = $"⚠️ Loop paused — all {enabledTemplates.Count} templates failing.";
        NotifyChange();
        _logger.Warning("Circuit breaker tripped: all {Count} enabled templates have {Threshold}+ consecutive failures",
            enabledTemplates.Count, maxConsecutiveFailures);

        _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await _resumeSignal.Task.WaitAsync(ct); }
        catch (OperationCanceledException) { return true; }
        if (_stopRequested) return true;
        return true;
    }

    /// <summary>
    /// Step 5: Fair dispatch — three-way interleaved round-robin (issues → PRs → decomposition).
    /// Alternates between dispatching one round of issues (one per template), one round
    /// of PRs (one per template), and one round of decomposition (one per template) to
    /// ensure all three queue types get fair access to the budget.
    /// </summary>
    private async Task DispatchFairRoundRobinAsync(
        List<PipelineJobTemplate> pollableTemplates,
        IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> flattenedTemplates,
        PipelineConfiguration config,
        int maxRunsPerCycle,
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>> projectLevelDecompositionQueues,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        int remaining = maxRunsPerCycle > 0 ? maxRunsPerCycle : int.MaxValue;

        // Per-cycle dependency state cache shared across all issue evaluations
        var cycleStateCache = new Dictionary<int, bool>();

        // Build template → project lookup for passing project context at dispatch time
        var templateProjectLookup = flattenedTemplates.ToDictionary(ft => ft.Template.Id, ft => ft.Project);

        // Count active decomposition runs for concurrency enforcement
        var activeDecompositionCount = _orchestration.GetAllActiveRuns()
            .Count(r => r.RunType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition);

        // Three-way turn tracking: 0 = issues, 1 = PRs, 2 = decomposition
        int currentTurn = 0;

        while (remaining > 0)
        {
            if (_stopRequested || ct.IsCancellationRequested) break;

            bool issueMadeProgress = false;
            bool prMadeProgress = false;
            bool decompMadeProgress = false;

            bool hasIssues = HasEligible(pollableTemplates, issueQueues, t => t.ImplementationEnabled);
            bool hasPrs = HasEligible(pollableTemplates, prQueues, t => t.ReviewEnabled);
            bool hasDecomp = (HasEligible(pollableTemplates, decompositionQueues, t => t.DecompositionEnabled)
                || HasEligibleProjectLevelDecomposition(projectLevelDecompositionQueues))
                && activeDecompositionCount < config.MaxConcurrentDecompositions;

            // Determine which queue to dispatch from this iteration.
            // If the current turn's queue is empty, try the next non-empty queue.
            int startTurn = currentTurn;
            bool foundTurn = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int tryTurn = (startTurn + attempt) % 3;
                if ((tryTurn == 0 && hasIssues) || (tryTurn == 1 && hasPrs) || (tryTurn == 2 && hasDecomp))
                {
                    currentTurn = tryTurn;
                    foundTurn = true;
                    break;
                }
            }
            if (!foundTurn) break; // All queues exhausted

            // ── Issue dispatch (one per template per pass) ──
            if (currentTurn == 0 && hasIssues)
            {
                var (progress, count) = await DispatchRoundAsync(pollableTemplates, async (template, stopToken) =>
                {
                    if (!template.ImplementationEnabled) return DispatchAttemptResult.Skip;
                    if (!issueQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                        return DispatchAttemptResult.Skip;

                    // Dequeue next valid issue
                    IssueSummary? issue = null;
                    while (queue.Count > 0)
                    {
                        var candidate = queue[0];
                        queue.RemoveAt(0);

                        if (candidate.Labels.Contains(AgentLabels.Error) || candidate.Labels.Contains(AgentLabels.NeedsRefinement))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedFilteredByLabel));
                            continue;
                        }
                        if (_orchestration.IsIssueBeingProcessed(candidate.Identifier))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }
                        if (_jobDispatcher!.IsIssueBeingProcessedOrQueued(candidate.Identifier))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }

                        if (_dependencyChecker != null)
                        {
                            if (!_providerCache.TryGetValue(template.IssueProviderId, out var provider))
                            {
                                _logger.Warning("Provider '{ProviderId}' not in cache during dependency check for #{Identifier}, skipping dispatch",
                                    template.IssueProviderId, candidate.Identifier);
                                continue;
                            }

                            var depResult = await _dependencyChecker.CheckAsync(
                                candidate.Identifier, candidate.Description, provider, cycleStateCache, ct);
                            if (!depResult.IsReady)
                            {
                                _logger.Information("Issue #{Identifier} blocked by open issues: {BlockedBy}. Skipping dispatch.",
                                    candidate.Identifier, depResult.BlockedBy);
                                PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedDependencyBlocked));
                                continue;
                            }
                        }

                        issue = candidate;
                        break;
                    }

                    if (issue is null) return DispatchAttemptResult.Skip;

                    CurrentIssueIdentifier = issue.Identifier;
                    StatusMessage = $"🔄 Dispatching #{issue.Identifier} from '{template.Name}'";
                    NotifyChange();

                    var dispatchProject = templateProjectLookup.GetValueOrDefault(template.Id);
                    _logger.Information("Dispatching issue {Issue} with project '{ProjectName}' (id={ProjectId}, template={TemplateId})",
                        issue.Identifier, dispatchProject?.Name ?? "NULL", dispatchProject?.Id ?? "NULL", template.Id);

                    var dispatched = await _jobDispatcher!.TryDispatchAsync(
                        issue.Identifier,
                        template.IssueProviderId, template.RepoProviderId,
                        template.BrainProviderId, template.PipelineProviderId,
                        initiatedBy: "loop",
                        stopToken,
                        issueTitle: issue.Title,
                        project: dispatchProject);

                    if (dispatched)
                        _logger.Information("Dispatched issue #{Issue} from template '{Template}'",
                            issue.Identifier, template.Name);

                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                        dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

                    return new DispatchAttemptResult(dispatched);
                }, remaining, stoppingToken, ct);
                issueMadeProgress = progress;
                remaining -= count;
            }

            if (_stopRequested || ct.IsCancellationRequested || remaining <= 0) break;

            // ── PR review dispatch (one per template per pass) ──
            if (currentTurn == 1 && hasPrs)
            {
                var (progress, count) = await DispatchRoundAsync(pollableTemplates, async (template, stopToken) =>
                {
                    if (!template.ReviewEnabled) return DispatchAttemptResult.Skip;
                    if (!prQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                        return DispatchAttemptResult.Skip;

                    PullRequestSummary? pr = null;
                    while (queue.Count > 0)
                    {
                        var candidate = queue[0];
                        queue.RemoveAt(0);

                        if (candidate.Labels.Contains(AgentLabels.Error) ||
                            candidate.Labels.Contains(AgentLabels.InProgress) ||
                            candidate.Labels.Contains(AgentLabels.Done) ||
                            candidate.Labels.Contains(AgentLabels.Cancelled))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedFilteredByLabel));
                            continue;
                        }
                        if (_orchestration.IsIssueBeingProcessed(candidate.Identifier))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }
                        if (_jobDispatcher!.IsIssueBeingProcessedOrQueued(candidate.Identifier))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }

                        pr = candidate;
                        break;
                    }

                    if (pr is null) return DispatchAttemptResult.Skip;

                    CurrentIssueIdentifier = pr.Identifier;
                    StatusMessage = $"🔄 Dispatching PR #{pr.Identifier} review from '{template.Name}'";
                    NotifyChange();

                    var dispatched = await _jobDispatcher!.TryDispatchReviewAsync(
                        new ReviewDispatchRequest
                        {
                            PrIdentifier = pr.Identifier,
                            PrBranchName = pr.BranchName,
                            PrTitle = pr.Title,
                            PrDescription = pr.Description,
                            PrAuthor = pr.Author,
                            PrUrl = pr.Url,
                            PrTargetBranch = pr.TargetBranch,
                            IssueProviderId = template.IssueProviderId,
                            RepoProviderId = template.RepoProviderId,
                            BrainProviderId = template.BrainProviderId,
                            InitiatedBy = "loop"
                        },
                        stopToken,
                        project: templateProjectLookup[template.Id]);

                    if (dispatched)
                        _logger.Information("Dispatched PR #{PrIdentifier} review from template '{Template}'",
                            pr.Identifier, template.Name);

                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                        dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

                    return new DispatchAttemptResult(dispatched);
                }, remaining, stoppingToken, ct);
                prMadeProgress = progress;
                remaining -= count;
            }

            if (_stopRequested || ct.IsCancellationRequested || remaining <= 0) break;

            // ── Decomposition dispatch (one per template per pass) ──
            if (currentTurn == 2 && hasDecomp)
            {
                var (progress, count) = await DispatchRoundAsync(pollableTemplates, async (template, stopToken) =>
                {
                    if (!template.DecompositionEnabled) return DispatchAttemptResult.Skip;
                    if (!decompositionQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                        return DispatchAttemptResult.Skip;

                    // Re-check concurrency limit before each dispatch
                    if (activeDecompositionCount >= config.MaxConcurrentDecompositions)
                    {
                        _logger.Information("Decomposition concurrency limit reached ({Active}/{Max}), skipping remaining decomposition dispatch",
                            activeDecompositionCount, config.MaxConcurrentDecompositions);
                        return DispatchAttemptResult.Abort;
                    }

                    (IssueSummary Issue, PipelineRunType Phase)? epic = null;
                    while (queue.Count > 0)
                    {
                        var candidate = queue[0];
                        queue.RemoveAt(0);

                        if (_orchestration.IsIssueBeingProcessed(candidate.Issue.Identifier))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }
                        if (_jobDispatcher!.IsIssueBeingProcessedOrQueued(candidate.Issue.Identifier))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }

                        epic = candidate;
                        break;
                    }

                    if (epic is null) return DispatchAttemptResult.Skip;

                    var epicItem = epic.Value;
                    var phaseLabel = epicItem.Phase == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

                    CurrentIssueIdentifier = epicItem.Issue.Identifier;
                    StatusMessage = $"🧩 Dispatching epic #{epicItem.Issue.Identifier} {phaseLabel} from '{template.Name}'";
                    NotifyChange();

                    var dispatched = await _jobDispatcher!.TryDispatchDecompositionAsync(
                        epicItem.Issue.Identifier,
                        epicItem.Issue.Title,
                        epicItem.Phase,
                        template.IssueProviderId,
                        template.RepoProviderId,
                        template.BrainProviderId,
                        initiatedBy: "loop",
                        stopToken,
                        project: templateProjectLookup[template.Id]);

                    if (dispatched)
                    {
                        activeDecompositionCount++;
                        _logger.Information("Dispatched epic #{EpicIdentifier} ({Phase}) from template '{Template}'",
                            epicItem.Issue.Identifier, epicItem.Phase, template.Name);
                    }

                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                        dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

                    return new DispatchAttemptResult(dispatched);
                }, remaining, stoppingToken, ct);
                decompMadeProgress = progress;
                remaining -= count;
            }

            // ── Project-level decomposition dispatch ──
            // Dispatch epics from project-level EpicIssueProviderId after template-level decomposition.
            // These use the first decomposition-enabled template in the project (SelectDecompositionTemplate).
            if (currentTurn == 2 && !decompMadeProgress && projectLevelDecompositionQueues.Count > 0
                && activeDecompositionCount < config.MaxConcurrentDecompositions)
            {
                foreach (var kvp in projectLevelDecompositionQueues.ToList())
                {
                    if (remaining <= 0 || _stopRequested || ct.IsCancellationRequested) break;
                    if (activeDecompositionCount >= config.MaxConcurrentDecompositions) break;

                    var queue = kvp.Value;
                    while (queue.Count > 0 && remaining > 0)
                    {
                        if (activeDecompositionCount >= config.MaxConcurrentDecompositions) break;

                        var candidate = queue[0];
                        queue.RemoveAt(0);

                        // Deduplication: skip if already being processed or queued
                        // (may have been picked up by template-level polling of the same issue)
                        if (_orchestration.IsIssueBeingProcessed(candidate.Issue.Identifier))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }
                        if (_jobDispatcher!.IsIssueBeingProcessedOrQueued(candidate.Issue.Identifier))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }

                        var phaseLabel = candidate.Phase == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

                        CurrentIssueIdentifier = candidate.Issue.Identifier;
                        StatusMessage = $"🧩 Dispatching project-level epic #{candidate.Issue.Identifier} {phaseLabel} from '{candidate.Template.Name}'";
                        NotifyChange();

                        try
                        {
                            var dispatched = await _jobDispatcher!.TryDispatchDecompositionAsync(
                                candidate.Issue.Identifier,
                                candidate.Issue.Title,
                                candidate.Phase,
                                candidate.Template.IssueProviderId,
                                candidate.Template.RepoProviderId,
                                candidate.Template.BrainProviderId,
                                initiatedBy: "loop",
                                stoppingToken,
                                decompositionSource: "project-level",
                                project: templateProjectLookup.GetValueOrDefault(candidate.Template.Id));

                            if (dispatched)
                            {
                                activeDecompositionCount++;
                                remaining--;
                                decompMadeProgress = true;
                                _logger.Information("Dispatched project-level epic #{EpicIdentifier} ({Phase}) via template '{Template}'",
                                    candidate.Issue.Identifier, candidate.Phase, candidate.Template.Name);
                            }

                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                                dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Project-level decomposition dispatch failed for epic #{EpicIdentifier}: {Error}",
                                candidate.Issue.Identifier, ex.Message);
                            remaining--;
                            decompMadeProgress = true;
                        }

                        break; // One dispatch per project per round (fair alternation)
                    }
                }

                // Remove empty project queues
                foreach (var key in projectLevelDecompositionQueues.Where(kvp => kvp.Value.Count == 0).Select(kvp => kvp.Key).ToList())
                    projectLevelDecompositionQueues.Remove(key);
            }

            // If no queue made progress, all are exhausted
            if (!issueMadeProgress && !prMadeProgress && !decompMadeProgress) break;

            // Advance to next turn for fair alternation
            currentTurn = (currentTurn + 1) % 3;
        }

        // Emit skipped_max_runs for items remaining in queues after budget exhaustion
        // TODO: Remaining queue items may include items that would have been skipped for other reasons (label, already processing, dependency) — this may overcount skipped_max_runs
        if (remaining <= 0)
        {
            var remainingItems = issueQueues.Values.Sum(q => q.Count)
                + prQueues.Values.Sum(q => q.Count)
                + decompositionQueues.Values.Sum(q => q.Count)
                + projectLevelDecompositionQueues.Values.Sum(q => q.Count);
            if (remainingItems > 0)
                PipelineTelemetry.LoopDispatchDecisions.Add(remainingItems, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedMaxRuns));
        }
    }

    /// <summary>
    /// Clears all three queue dictionaries for a given template. Used in error catch blocks
    /// to ensure a failed template doesn't leave stale partial data in queues.
    /// </summary>
    private static void ClearQueuesForTemplate(
        string templateId,
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues)
    {
        issueQueues[templateId] = new List<IssueSummary>();
        prQueues[templateId] = new List<PullRequestSummary>();
        decompositionQueues[templateId] = new List<(IssueSummary, PipelineRunType)>();
    }

    /// <summary>
    /// Result of a single-template dispatch attempt within <see cref="DispatchRoundAsync"/>.
    /// </summary>
    private readonly record struct DispatchAttemptResult(bool Dispatched, bool Attempted = true, bool AbortRemaining = false)
    {
        /// <summary>No candidate found for this template — skip it.</summary>
        public static readonly DispatchAttemptResult Skip = new(false, Attempted: false);

        /// <summary>Abort remaining templates (e.g., concurrency limit reached).</summary>
        public static readonly DispatchAttemptResult Abort = new(false, Attempted: false, AbortRemaining: true);
    }

    /// <summary>
    /// Shared dispatch helper that iterates templates, invokes the dispatch delegate inside
    /// a try/catch, and manages counters and progress tracking. Eliminates the triplicated
    /// dispatch pattern for issues, PRs, and decomposition.
    /// The delegate is responsible for setting <see cref="CurrentIssueIdentifier"/>,
    /// <see cref="StatusMessage"/>, and calling <see cref="NotifyChange"/> before dispatching.
    /// </summary>
    /// <returns>Whether any dispatch was attempted (progress) and how many items were consumed.</returns>
    private async Task<(bool madeProgress, int consumed)> DispatchRoundAsync(
        List<PipelineJobTemplate> pollableTemplates,
        Func<PipelineJobTemplate, CancellationToken, Task<DispatchAttemptResult>> tryDispatchOne,
        int remainingBudget,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        bool madeProgress = false;
        int consumed = 0;

        foreach (var template in pollableTemplates)
        {
            if (remainingBudget - consumed <= 0) break;
            if (_stopRequested || ct.IsCancellationRequested) break;

            DispatchAttemptResult result;
            try
            {
                result = await tryDispatchOne(template, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Dispatch failed for {Identifier} from template '{Template}'",
                    CurrentIssueIdentifier, template.Name);
                FailedCount++;
                ProcessedCount++;
                consumed++;
                madeProgress = true;
                continue;
            }

            if (result.AbortRemaining) break;
            if (!result.Attempted) continue;

            if (result.Dispatched)
            {
                ProcessedCount++;
                consumed++;
                madeProgress = true;
            }
        }

        return (madeProgress, consumed);
    }

    /// <summary>
    /// Checks whether any pollable template has eligible items remaining in its queue,
    /// filtered by a template-level feature-enabled predicate.
    /// Used by the fair alternation logic to determine which queues have work available.
    /// </summary>
    private static bool HasEligible<T>(
        List<PipelineJobTemplate> pollableTemplates,
        Dictionary<string, List<T>> queues,
        Func<PipelineJobTemplate, bool> isEnabledForTemplate)
    {
        foreach (var template in pollableTemplates)
        {
            if (!isEnabledForTemplate(template)) continue;
            if (queues.TryGetValue(template.Id, out var queue) && queue.Count > 0)
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

    /// <summary>Fetches all pages from a paginated API up to maxPages.</summary>
    private static async Task<List<T>> FetchAllPagesAsync<T>(
        Func<int, int, CancellationToken, Task<PagedResult<T>>> fetchPage,
        int maxPages,
        CancellationToken ct)
    {
        var result = new List<T>();
        int page = 1;
        const int pageSize = PipelineConstants.DefaultPageSize;

        while (true)
        {
            var pagedResult = await fetchPage(page, pageSize, ct);
            result.AddRange(pagedResult.Items);
            if (!pagedResult.HasMore) break;
            if (page >= maxPages) break;
            page++;
        }

        return result;
    }

    /// <summary>Fetches agent:next issues from a specific provider (used in multi-template mode).</summary>
    private async Task<List<IssueSummary>> FetchAgentNextIssuesForProviderAsync(
        IIssueProvider provider, int maxPages, CancellationToken ct)
    {
        var result = await FetchAllPagesAsync<IssueSummary>(
            (page, pageSize, token) => provider.ListOpenIssuesAsync(page, pageSize, new[] { AgentLabels.Next }, token),
            maxPages, ct);

        // FIFO: oldest first
        result.Sort((a, b) => (a.CreatedAt ?? DateTime.MaxValue).CompareTo(b.CreatedAt ?? DateTime.MaxValue));
        return result;
    }

    /// <summary>
    /// Fetches agent:next pull requests from a repository provider, filters out ineligible PRs,
    /// and orders by CreatedAt ascending (FIFO). PRs without CreatedAt sort last.
    /// </summary>
    private async Task<List<PullRequestSummary>> FetchAgentNextPullRequestsAsync(
        IRepositoryProvider repoProvider, int maxPages, CancellationToken ct)
    {
        var result = await FetchAllPagesAsync<PullRequestSummary>(
            (page, pageSize, token) => repoProvider.ListOpenPullRequestsAsync(page, pageSize, new[] { AgentLabels.Next }, token),
            maxPages, ct);

        // Filter: skip PRs with terminal/in-progress status labels
        result.RemoveAll(pr =>
            pr.Labels.Contains(AgentLabels.Error) ||
            pr.Labels.Contains(AgentLabels.InProgress) ||
            pr.Labels.Contains(AgentLabels.Done) ||
            pr.Labels.Contains(AgentLabels.Cancelled));

        // FIFO: oldest first, PRs without CreatedAt go last
        result.Sort((a, b) => (a.CreatedAt ?? DateTime.MaxValue).CompareTo(b.CreatedAt ?? DateTime.MaxValue));
        return result;
    }

    /// <summary>
    /// Fetches epic issues with a specific label from a provider, applies eligibility filters,
    /// and orders by CreatedAt ascending (FIFO). Used for decomposition polling.
    /// </summary>
    /// <param name="provider">The issue provider to query.</param>
    /// <param name="label">The label to filter by (agent:epic or agent:epic-approved).</param>
    /// <param name="maxPages">Maximum pages to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<IssueSummary>> FetchEpicIssuesAsync(
        IIssueProvider provider, string label, int maxPages, CancellationToken ct)
    {
        var result = await FetchAllPagesAsync<IssueSummary>(
            (page, pageSize, token) => provider.ListOpenIssuesAsync(page, pageSize, new[] { label }, token),
            maxPages, ct);

        // Apply eligibility filters based on the label type:
        if (label == AgentLabels.Epic)
        {
            // Phase 1: skip if also has agent:epic-review, agent:in-progress, agent:error, or agent:done
            result.RemoveAll(issue =>
                issue.Labels.Contains(AgentLabels.EpicReview) ||
                issue.Labels.Contains(AgentLabels.InProgress) ||
                issue.Labels.Contains(AgentLabels.Error) ||
                issue.Labels.Contains(AgentLabels.Done));
        }
        else if (label == AgentLabels.EpicApproved)
        {
            // Phase 2: skip if also has agent:in-progress, agent:error, or agent:done
            result.RemoveAll(issue =>
                issue.Labels.Contains(AgentLabels.InProgress) ||
                issue.Labels.Contains(AgentLabels.Error) ||
                issue.Labels.Contains(AgentLabels.Done));
        }

        // FIFO: oldest first
        result.Sort((a, b) => (a.CreatedAt ?? DateTime.MaxValue).CompareTo(b.CreatedAt ?? DateTime.MaxValue));
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

    /// <summary>
    /// Flattens all enabled projects' templates into a single ordered list.
    /// Order: projects alphabetical by Name, templates by TemplateIds position.
    /// Templates are looked up from PipelineConfiguration.PipelineJobTemplates (Phase 1 —
    /// templates remain stored in the global config, projects only own IDs).
    /// Skips disabled projects entirely. Skips missing template IDs with a warning.
    /// Only includes templates that are individually enabled.
    /// </summary>
    internal IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> FlattenTemplates(
        IReadOnlyList<PipelineProject> projects,
        PipelineConfiguration globalConfig)
    {
        var result = new List<(PipelineJobTemplate, PipelineProject)>();
        // Build lookup for O(1) template resolution
        var templateLookup = globalConfig.PipelineJobTemplates.ToDictionary(t => t.Id);

        // If no projects exist yet (pre-migration or test scenario), create a virtual Default
        // containing all template IDs to preserve backward compatibility
        if (projects.Count == 0)
        {
            var virtualDefault = new PipelineProject
            {
                Id = WellKnownIds.DefaultProjectId,
                Name = "Default",
                TemplateIds = globalConfig.PipelineJobTemplates.Select(t => t.Id).ToList()
            };
            projects = new List<PipelineProject> { virtualDefault };
        }
        else
        {
            // Handle orphaned templates: templates in globalConfig that don't appear in any project
            var allProjectTemplateIds = projects
                .SelectMany(p => p.TemplateIds)
                .ToHashSet();

            var orphanedTemplateIds = globalConfig.PipelineJobTemplates
                .Where(t => !allProjectTemplateIds.Contains(t.Id))
                .Select(t => t.Id)
                .ToList();

            if (orphanedTemplateIds.Count > 0)
            {
                _logger.Warning("Found {Count} orphaned template(s) not assigned to any project — assigning to Default: {TemplateIds}",
                    orphanedTemplateIds.Count, string.Join(", ", orphanedTemplateIds));

                // Find Default project and add orphaned templates to it for this cycle
                var defaultProject = projects.FirstOrDefault(p => p.Id == WellKnownIds.DefaultProjectId);
                if (defaultProject is not null)
                {
                    var updatedTemplateIds = defaultProject.TemplateIds.Concat(orphanedTemplateIds).ToList();
                    var updatedDefault = defaultProject with { TemplateIds = updatedTemplateIds };

                    // Replace in a new list for this cycle's processing
                    projects = projects
                        .Select(p => p.Id == WellKnownIds.DefaultProjectId ? updatedDefault : p)
                        .ToList();
                }
                else
                {
                    // Default project is missing — create a virtual one for orphans
                    _logger.Warning("Default project not found — creating virtual Default for orphaned templates");
                    var virtualDefault = new PipelineProject
                    {
                        Id = WellKnownIds.DefaultProjectId,
                        Name = "Default",
                        TemplateIds = orphanedTemplateIds
                    };
                    projects = projects.Concat(new[] { virtualDefault }).ToList();
                }
            }
        }

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

    /// <summary>
    /// Selects the repository template for a project-level epic decomposition dispatch.
    /// Returns the first decomposition-enabled template in the project (by TemplateIds position).
    /// Returns null if no decomposition-enabled template exists (skip this project's epic polling).
    /// </summary>
    private static PipelineJobTemplate? SelectDecompositionTemplate(
        PipelineProject project,
        Dictionary<string, PipelineJobTemplate> templateLookup)
    {
        foreach (var templateId in project.TemplateIds)
        {
            if (templateLookup.TryGetValue(templateId, out var template)
                && template.Enabled
                && template.DecompositionEnabled)
            {
                return template;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether any project-level decomposition queue has eligible epics remaining.
    /// Used by the fair alternation logic to include project-level epics in the decomposition turn.
    /// </summary>
    private static bool HasEligibleProjectLevelDecomposition(
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>> projectLevelQueues)
    {
        foreach (var kvp in projectLevelQueues)
        {
            if (kvp.Value.Count > 0)
                return true;
        }
        return false;
    }
}
