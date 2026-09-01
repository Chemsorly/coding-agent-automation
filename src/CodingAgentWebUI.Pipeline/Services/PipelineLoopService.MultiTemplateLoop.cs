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
        // TODO [WARNING]: _loopCts is read here without holding _lock. This is safe only because
        // CleanupAsync is the sole disposer of _loopCts and it always runs *after* this method returns.
        // If that ordering ever changes (e.g. a concurrent StopLoop path that disposes _loopCts while
        // this method is still running), CreateLinkedTokenSource will throw ObjectDisposedException.
        // Consider reading _loopCts?.Token under a short lock or snapshotting it in the caller.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _loopCts?.Token ?? CancellationToken.None);
        var ct = linkedCts.Token;

        while (!_stopRequested && !ct.IsCancellationRequested)
        {
            var snapshot = await SnapshotCycleConfigAsync(ct);
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

        var (issueQueues, prQueues, decompositionQueues, agentDonePrQueues) = await _poller.PollTemplateQueuesAsync(
            snapshot.PollableTemplates, snapshot.Config.ClosedLoopMaxPagesToFetch, _templateStatuses,
            i => CurrentCycleTemplateIndex = i,
            msg => { lock (_lock) { StatusMessage = msg; } },
            NotifyChange,
            ct);

        if (_stopRequested || ct.IsCancellationRequested) return false;

        var projectLevelDecompositionQueues = await _poller.PollProjectLevelEpicsAsync(
            snapshot.Projects, snapshot.TemplateLookup, snapshot.Config.ClosedLoopMaxPagesToFetch, ct);

        if (_stopRequested || ct.IsCancellationRequested) return false;

        EmitCyclePollMetrics(snapshot, failuresBefore, issueQueues, prQueues, decompositionQueues, projectLevelDecompositionQueues);

        // Build eligibility map from already-polled data for use by the queue sweep later.
        // failuresBefore is passed so BuildEligibilityMap can detect templates that failed (or were
        // rate-limited) *during* this cycle and skip them (fail-open), preventing incorrect
        // cancellation of WorkItems when poll data is stale due to a same-cycle failure.
        var eligibleByProvider = BuildEligibilityMap(snapshot.PollableTemplates, issueQueues, failuresBefore, _templateStatuses);

        if (await CheckCircuitBreakerAsync(snapshot.EnabledTemplates, snapshot.Config.ClosedLoopMaxConsecutivePollFailures, snapshot.Config.ClosedLoopCircuitBreakerCooldown, ct))
            return true;

        // _dispatcher is null when IDispatchOrchestrationService was not registered (e.g. test environments
        // that exercise the loop lifecycle but not dispatch). Return false so the loop cycle completes cleanly.
        if (_dispatcher is null)
            return false;

        var dispatchResult = await _dispatcher.DispatchFairRoundRobinAsync(
            new DispatchScheduler.DispatchRoundRobinRequest
            {
                PollableTemplates = snapshot.PollableTemplates,
                FlattenedTemplates = snapshot.FlattenedTemplates,
                Config = snapshot.Config,
                MaxRunsPerCycle = snapshot.Config.ClosedLoopMaxRunsPerCycle,
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

        await RunHousekeepingAsync(snapshot, agentDonePrQueues, ct);

        if (snapshot.Config.QueueSweepEnabled)
            await SweepPendingWorkItemsAsync(eligibleByProvider, sweepEnabled: true, ct);

        if (_stopRequested || ct.IsCancellationRequested) return false;

        lock (_lock) { StatusMessage = $"🔄 Cycle complete. Polling {snapshot.EnabledTemplates.Count} templates every {(int)snapshot.Config.ClosedLoopPollInterval.TotalSeconds}s."; }
        NotifyChange();
        await DelayOrStop(snapshot.Config.ClosedLoopPollInterval, ct);
        return true;
    }

    private Dictionary<string, int> BuildTemplateFailureBaseline(IReadOnlyList<PipelineJobTemplate> templates)
    {
        return templates.DistinctBy(t => t.Id).ToDictionary(
            t => t.Id,
            t => _templateStatuses.TryGetValue(t.Id, out var s) ? s.ConsecutiveFailures : 0);
    }

    /// <summary>
    /// Builds a provider-keyed eligibility map from the already-polled issue queues.
    /// The map is used by <see cref="SweepPendingWorkItemsAsync"/> to decide which Pending
    /// WorkItems to cancel.
    /// <para>
    /// Fail-open cases — a template is omitted from the map (its provider will be absent,
    /// causing the sweep to skip WorkItems for that provider) when:
    /// <list type="bullet">
    ///   <item>Its ID is not in <paramref name="issueQueues"/> (template was not polled).</item>
    ///   <item>Its <c>ConsecutiveFailures</c> increased versus <paramref name="failuresBefore"/>
    ///         (it failed during this cycle — poll data is unreliable).</item>
    ///   <item>Its current status has <c>RateLimitResetAt</c> set (rate-limited during this cycle
    ///         OR excluded from <paramref name="pollableTemplates"/> upstream because it was already
    ///         rate-limited at cycle start).</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static IReadOnlyDictionary<string, HashSet<string>> BuildEligibilityMap(
        IReadOnlyList<PipelineJobTemplate> pollableTemplates,
        Dictionary<string, List<IssueSummary>> issueQueues,
        IReadOnlyDictionary<string, int>? failuresBefore = null,
        IReadOnlyDictionary<string, ConfigStatusSnapshot>? templateStatuses = null)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var template in pollableTemplates)
        {
            if (!issueQueues.TryGetValue(template.Id, out var issues))
                continue; // template was not polled this cycle — fail open, omit from map

            // Skip templates that failed (or got rate-limited) during this cycle: their
            // issueQueues entry was cleared by HandleRateLimitException/HandleGenericPollException
            // and represents missing data, not a genuine empty queue.
            // TODO [WARNING]: When templateStatuses contains an entry for a template but
            // failuresBefore does not (or vice versa), the ConsecutiveFailures check is skipped
            // and the template is included in the eligibility map with whatever issueQueues holds.
            // If a template fails during the cycle (ConsecutiveFailures incremented) but its ID
            // was never recorded in failuresBefore, the stale/empty issue list is treated as a
            // genuine "zero eligible issues" eligibility set, potentially cancelling WorkItems for
            // a provider whose poll data is unreliable. In practice failuresBefore is always built
            // from the same _templateStatuses snapshot before polling, so the two should always be
            // in sync — but this is not enforced by the type system.
            if (templateStatuses is not null && templateStatuses.TryGetValue(template.Id, out var status))
            {
                if (status.RateLimitResetAt.HasValue)
                    continue; // rate-limited during (or before) this cycle — fail open

                if (failuresBefore is not null &&
                    failuresBefore.TryGetValue(template.Id, out var before) &&
                    status.ConsecutiveFailures > before)
                    continue; // poll failed during this cycle — fail open
            }

            if (!result.TryGetValue(template.IssueProviderId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                result[template.IssueProviderId] = set;
            }
            foreach (var issue in issues)
                set.Add(issue.Identifier);
        }
        return result;
    }

    /// <summary>
    /// Fetches all Pending WorkItems and cancels any whose issue is no longer in the
    /// current cycle's eligibility set. Skips WorkItems with <c>TaskType != Implementation</c>.
    /// Aborts the entire sweep (without cancelling anything) if <c>GetPendingAsync</c> throws.
    /// Per-item <c>PostStatusAsync</c> failures are handled individually: expected HTTP races
    /// (400/404/409) are logged at Debug level; unexpected failures are logged at Warning and
    /// counted in <see cref="PipelineTelemetry.QueueSweepFailed"/>.
    /// </summary>
    /// <param name="eligibleByProvider">Provider-keyed eligibility map from <see cref="BuildEligibilityMap"/>.</param>
    /// <param name="sweepEnabled">
    /// Must be <c>true</c> for the sweep to run. Pass <see cref="PipelineConfiguration.QueueSweepEnabled"/>
    /// here; the parameter is explicit (rather than reading config inside the method) so the
    /// <c>QueueSweepEnabled = false</c> guard can be unit-tested without wiring a full cycle.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task SweepPendingWorkItemsAsync(
        IReadOnlyDictionary<string, HashSet<string>> eligibleByProvider,
        bool sweepEnabled,
        CancellationToken ct)
    {
        if (!sweepEnabled) return;
        if (_workItemClient is null) return;

        IReadOnlyList<PendingWorkItemDto> pending;
        // TODO [WARNING]: GetPendingAsync(maxResults: 500) is a hard, unpaginated cap. If more
        // than 500 Pending Implementation WorkItems exist, the excess is silently skipped this
        // cycle with no log, no counter. The assumption is that the sweep drains across multiple
        // cycles. If the full 500 are returned, consider logging a Warning to signal the cap was hit.
        try
        {
            pending = await _workItemClient.GetPendingAsync(maxResults: 500, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "QueueSweep: failed to fetch pending items, skipping this cycle");
            return;
        }

        foreach (var item in pending)
        {
            if (item.TaskType != WorkItemTaskType.Implementation)
            {
                PipelineTelemetry.QueueSweepSkipped.Add(1);
                continue;
            }

            if (!eligibleByProvider.TryGetValue(item.IssueProviderConfigId, out var eligible))
            {
                // Provider was not polled this cycle (e.g. rate-limited template excluded from
                // pollableTemplates) — fail open, do not cancel
                PipelineTelemetry.QueueSweepSkipped.Add(1);
                continue;
            }

            if (eligible.Contains(item.IssueIdentifier))
            {
                // Issue is still eligible for dispatch — do not cancel
                continue;
            }

            _logger.Information(
                "QueueSweep: cancelling WorkItem {WorkItemId} for issue {IssueIdentifier} " +
                "(provider {IssueProviderConfigId}) — issue no longer eligible",
                item.Id, item.IssueIdentifier, item.IssueProviderConfigId);
            // TODO [WARNING]: QueueSweepCancelled is incremented here, before PostStatusAsync.
            // If PostStatusAsync throws (expected 400/404/409 race or unexpected failure), the
            // counter still records a "cancelled" item that was not actually cancelled by this sweep.
            // This means QueueSweepCancelled counts "cancel attempts" rather than "confirmed
            // cancellations". To fix, move this Add(1) call inside the try block, after the
            // PostStatusAsync await succeeds.
            PipelineTelemetry.QueueSweepCancelled.Add(1);

            try
            {
                await _workItemClient.PostStatusAsync(item.Id,
                    new WorkItemStatusUpdate
                    {
                        Status = "Cancelled",
                        ErrorMessage = "Issue no longer eligible for dispatch (queue sweep)"
                    }, ct);
            }
            catch (HttpRequestException httpEx) when (
                httpEx.StatusCode is System.Net.HttpStatusCode.BadRequest
                    or System.Net.HttpStatusCode.NotFound
                    or System.Net.HttpStatusCode.Conflict)
            {
                // Expected race: item was claimed/transitioned by the DispatchLoop between our
                // GetPendingAsync scan and this PostStatusAsync call. Treat as non-error.
                _logger.Debug(
                    "QueueSweep: WorkItem {WorkItemId} already transitioned (HTTP {StatusCode}) — skipping",
                    item.Id, (int?)httpEx.StatusCode);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Unexpected failure (network error, timeout, 5xx). Count and log at Warning.
                PipelineTelemetry.QueueSweepFailed.Add(1);
                _logger.Warning(ex,
                    "QueueSweep: PostStatusAsync failed unexpectedly for WorkItem {WorkItemId} — will retry next cycle",
                    item.Id);
            }
        }
    }

    /// <summary>
    /// Housekeeping step: trigger server-side branch updates on eligible agent:done PRs.
    /// Called even when <paramref name="agentDonePrQueues"/> is empty — eviction pass must run to free slots.
    /// <paramref name="agentDonePrQueues"/> is not counted in <see cref="EmitCyclePollMetrics"/> — not dispatched work items.
    /// Deduplicated by <c>RepoProviderId</c>: if multiple templates share the same repo, only the
    /// first (lowest index in <c>PollableTemplates</c>) processes that repo this cycle.
    /// </summary>
    // TODO: agentDonePrQueues should be IReadOnlyDictionary<string, IReadOnlyList<PullRequestSummary>>
    // since this method only reads from it (.TryGetValue). The mutable concrete type widens the
    // apparent contract unnecessarily. Carried forward from the pre-refactor inline block.
    internal async Task RunHousekeepingAsync(
        CycleSnapshot snapshot,
        Dictionary<string, List<PullRequestSummary>> agentDonePrQueues,
        CancellationToken ct)
    {
        if (_housekeepingService is not { } housekeepingService) return;

        var processedRepos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var template in snapshot.PollableTemplates)
        {
            if (!template.HousekeepingEnabled) continue;
            if (!processedRepos.Add(template.RepoProviderId)) continue; // already processed this repo this cycle
            if (!_cacheManager.RepoProviders.TryGetValue(template.RepoProviderId, out var repoProvider)) continue;
            if (!repoProvider.SupportsServerSideBranchUpdate) continue;
            if (!_cacheManager.IssueProviders.TryGetValue(template.IssueProviderId, out var issueProvider)) continue;

            var donePrs = agentDonePrQueues.TryGetValue(template.Id, out var d) ? d : [];
            var limit = Math.Max(1,
                template.HousekeepingConcurrencyLimit ?? snapshot.Config.HousekeepingConcurrencyLimit);

            await housekeepingService.ExecuteAsync(
                repoProvider, template.RepoProviderId,
                issueProvider, template.IssueProviderId,
                donePrs, limit,
                template.HousekeepingBranchCleanupEnabled,
                snapshot.Config.HousekeepingBranchCleanupIntervalMinutes,
                ct);
        }
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
    /// Snapshot record bundling all cycle-immutable state.
    /// Config-derived values (PollInterval, MaxRunsPerCycle, MaxConsecutiveFailures, MaxPagesToFetch)
    /// are accessed via <see cref="Config"/> rather than copied as separate fields.
    /// </summary>
    internal sealed record CycleSnapshot(
        PipelineConfiguration Config,
        IReadOnlyList<PipelineProject> Projects,
        IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> FlattenedTemplates,
        IReadOnlyList<PipelineJobTemplate> EnabledTemplates,
        IReadOnlyList<PipelineJobTemplate> PollableTemplates,
        IReadOnlyDictionary<string, PipelineJobTemplate> TemplateLookup,
        HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)> ActiveIssueIdentifiers);

    /// <summary>
    /// Loads config and templates, reconciles provider caches, then loads active issue identifiers.
    /// Operations execute in the same order as the original SnapshotAndReconcileAsync:
    /// LoadConfig → LoadTemplates → ReconcileIssueCache → ReconcileRepoCache
    ///   → LoadActiveIssueIdentifiers → ReconcileStuckWorkItems.
    /// Always returns a non-null CycleSnapshot (even when template lists are empty).
    /// </summary>
    private async Task<CycleSnapshot?> SnapshotCycleConfigAsync(CancellationToken ct)
    {
        // Step 1: Load config and templates (pure query)
        var config = await _pipelineConfigStore.LoadPipelineConfigAsync(ct);

        var (projects, flattenedTemplates, enabledTemplates, pollableTemplates, templateLookup) =
            await LoadAndFlattenTemplatesAsync(ct);

        CurrentCycleTemplateCount = enabledTemplates.Count;

        // Step 2: Reconcile provider caches (side effects — order preserved from original)
        await ReconcileCachesAsync(enabledTemplates, projects, ct);

        // Step 2b: Load active issue identifiers (after cache reconciliation, per original order)
        var activeIssueIdentifiers = await LoadActiveIssueIdentifiersAsync(ct);

        // Step 2c: Reconcile stuck work items (after active issue identifier load, per original order)
        await ReconcileStuckWorkItemsAsync(ct);

        return new CycleSnapshot(
            config, projects, flattenedTemplates, enabledTemplates.AsReadOnly(), pollableTemplates.AsReadOnly(),
            templateLookup.AsReadOnly(),
            activeIssueIdentifiers);
    }

    /// <summary>
    /// Reconciles provider caches for the current cycle (issue + repo).
    /// Note: <see cref="ReconcileIssueProviderCacheAsync"/> exceptions propagate (no try/catch);
    /// <see cref="ReconcileRepoProviderCacheAsync"/> swallows non-cancellation exceptions and logs a warning.
    /// <see cref="ReconcileStuckWorkItemsAsync"/> is called separately after
    /// <see cref="LoadActiveIssueIdentifiersAsync"/> to preserve the original execution order.
    /// </summary>
    private async Task ReconcileCachesAsync(
        IReadOnlyList<PipelineJobTemplate> enabledTemplates,
        IReadOnlyList<PipelineProject> projects,
        CancellationToken ct)
    {
        await ReconcileIssueProviderCacheAsync(enabledTemplates, projects, ct);
        await ReconcileRepoProviderCacheAsync(enabledTemplates, ct);
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
        IReadOnlyList<PipelineJobTemplate> enabledTemplates,
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
    private async Task ReconcileRepoProviderCacheAsync(IReadOnlyList<PipelineJobTemplate> enabledTemplates, CancellationToken ct)
    {
        var neededRepoIds = enabledTemplates
            .Where(t => t.ReviewEnabled || t.DecompositionEnabled || t.HousekeepingEnabled)
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

    /// <summary>No-op: stuck item detection is owned by the Job Controller's ReconciliationService.</summary>
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
