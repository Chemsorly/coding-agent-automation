using System.Collections.Concurrent;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Evaluates agent:done PRs, evicts resolved in-flight entries, triggers
/// server-side branch updates for PRs that are behind base, re-queues
/// conflicted PRs for rework, and deletes stale agent branches.
/// </summary>
/// <remarks>
/// State: <c>_inFlight</c> is a <c>ConcurrentDictionary&lt;string, HashSet&lt;int&gt;&gt;</c>
/// keyed by <c>repoProviderId</c>. The inner <c>HashSet&lt;int&gt;</c> is NOT thread-safe,
/// but is safe here because <see cref="ExecuteAsync"/> is called sequentially from the
/// poll tick (one call at a time per template). <see cref="UpdateAsync"/> does NOT
/// access <c>_inFlight</c> — it only calls the provider and emits telemetry.
/// </remarks>
public sealed class HousekeepingService : IHousekeepingService
{
    private readonly IOrchestratorRunService _runService;
    private readonly ILogger _logger;

    /// <summary>
    /// Issue labels that indicate the issue is already actively queued or in-progress.
    /// Used to guard both conflict-rework label swaps and stale branch deletion.
    /// </summary>
    private static readonly HashSet<string> ActiveLabels = new(StringComparer.Ordinal)
    {
        AgentLabels.Next,
        AgentLabels.InProgress,
        AgentLabels.Epic,
        AgentLabels.EpicApproved,
        AgentLabels.EpicReview,
    };

    /// <summary>
    /// Issue labels representing an explicit human decision to abandon work — conflict rework
    /// must not re-queue these issues as <c>agent:next</c>.
    /// Distinct from <see cref="ActiveLabels"/> to avoid affecting stale-branch cleanup,
    /// which should still delete branches for these abandoned issues.
    /// <para>
    /// <c>agent:done</c> is intentionally <em>excluded</em>: it means the agent completed a run,
    /// but the resulting PR may still be open and conflicted. An open conflicted PR always needs
    /// rework regardless of the issue's current label — <c>agent:done</c> is not a human signal
    /// to abandon the work.
    /// </para>
    /// <para>
    /// <c>agent:error</c> and <c>agent:needs-refinement</c> are intentionally excluded —
    /// they are human-placed signals that the issue should be re-queued for rework.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> TerminalReworkBlockers = new(StringComparer.Ordinal)
    {
        AgentLabels.WontDo,
        AgentLabels.Cancelled,
    };

    /// <summary>
    /// Controls how fire-and-forget update tasks are dispatched.
    /// In production: discards the task (true fire-and-forget).
    /// In tests: overridden to await synchronously so assertions are deterministic.
    /// </summary>
    internal Func<Task, Task> FireAndForget { get; set; } = task => { _ = task; return Task.CompletedTask; };

    /// <summary>
    /// Overridable time source for the cleanup interval guard.
    /// In tests: replace with a lambda that returns a controlled time.
    /// </summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// In-flight PR numbers per repository, persisted across poll ticks.
    /// The slot represents the CI run lifetime, not the HTTP call lifetime.
    /// </summary>
    private readonly ConcurrentDictionary<string, HashSet<int>> _inFlight = new();

    /// <summary>
    /// Tracks when each PR's in-flight slot was acquired, keyed by (repoProviderId, prNumber).
    /// Used to implement max-age slot eviction: a PR that has held the slot for longer than
    /// <see cref="PipelineConfiguration.HousekeepingMaxSlotAgeMinutes"/> is evicted regardless
    /// of mergeability status, preventing a single stuck PR from starving all other behind PRs.
    /// Cleared on normal slot release (Steps 3 and 6b) and on PR removal from the polled set.
    /// </summary>
    /// <remarks>
    /// TODO: the invariant "every entry in _inFlight has a matching entry in _inFlightAt" is not
    /// enforced structurally — it relies on Step 6b being the only writer to both dictionaries.
    /// A PR that enters _inFlight without a corresponding _inFlightAt entry (e.g. via a future code
    /// path) will have slotAge fall back to 0.0 and max-age eviction will silently never fire for it.
    /// Consider encapsulating both writes behind a helper method (e.g. AcquireSlot / ReleaseSlot)
    /// to make the pairing structural rather than a documentation convention.
    /// </remarks>
    private readonly ConcurrentDictionary<(string repoId, int prNumber), DateTimeOffset> _inFlightAt = new();

    /// <summary>
    /// Tracks when the last stale-branch cleanup pass ran per repository,
    /// so we don't call <c>ListAgentBranchesAsync</c> on every tick.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCleanupAt = new();

    /// <summary>
    /// Tracks when each PR last had a branch update triggered, keyed by (repoProviderId, prNumber).
    /// Keyed by repo to prevent cross-repo collisions when the singleton handles multiple repos
    /// (two repos can both have a PR #N — their cooldown entries must not interfere).
    /// Used to deprioritise recently-triggered PRs so the single concurrency slot
    /// drains the queue fairly instead of re-selecting the same PR on every cycle.
    /// </summary>
    private readonly ConcurrentDictionary<(string repoId, int prNumber), DateTimeOffset> _lastTriggeredAt = new();

    /// <summary>
    /// Minimum time between consecutive branch-update triggers for the same PR.
    /// Prevents a single PR from monopolising the slot when CI takes longer than
    /// one poll cycle — the PR is deprioritised for this window after each trigger.
    /// Defaults to 25 minutes to comfortably exceed a typical CI run (~20 min).
    /// Overridable in tests.
    /// </summary>
    internal TimeSpan TriggerCooldown { get; set; } = TimeSpan.FromMinutes(25);

    /// <summary>
    /// Delay inserted between the initial mergeability probe and the re-probe for PRs that
    /// returned <see cref="PrMergeabilityStatus.Unknown"/>. GitHub and GitLab compute mergeability
    /// lazily on-demand: the first API call triggers a background job and returns an unresolved
    /// state immediately; the second call (a few seconds later) picks up the resolved state.
    /// Applies to GitHub's <c>unknown</c> and GitLab's <c>checking</c>/<c>unchecked</c> states.
    /// Default: 5 seconds. Overridable in tests (set to
    /// <see cref="TimeSpan.Zero"/> to avoid real delays in unit tests).
    /// </summary>
    internal TimeSpan MergeabilityReprobeDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Wraps <see cref="Task.Delay(TimeSpan, CancellationToken)"/> so tests can count
    /// how many times the batch delay fires. In production this is the real <c>Task.Delay</c>.
    /// In tests, replace with a lambda that also increments a counter:
    /// <c>svc.ReprobeDelayFunc = (ts, ct) => { delayCount++; return Task.Delay(ts, ct); }</c>
    /// This is the only seam that can distinguish a single batch delay from a per-PR delay loop,
    /// since <c>MergeabilityReprobeDelay = TimeSpan.Zero</c> makes all calls take zero wall time.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> ReprobeDelayFunc { get; set; } =
        (delay, ct) => Task.Delay(delay, ct);

    public HousekeepingService(IOrchestratorRunService runService, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(logger);
        _runService = runService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(
        IRepositoryProvider repoProvider,
        string repoProviderId,
        IIssueProvider issueProvider,
        string issueProviderId,
        IReadOnlyList<PullRequestSummary> agentDonePrs,
        int effectiveConcurrencyLimit,
        bool branchCleanupEnabled,
        int cleanupIntervalMinutes,
        int triggerCooldownMinutes,
        int maxSlotAgeMinutes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repoProvider);
        ArgumentNullException.ThrowIfNull(repoProviderId);
        ArgumentNullException.ThrowIfNull(issueProvider);
        ArgumentNullException.ThrowIfNull(issueProviderId);
        ArgumentNullException.ThrowIfNull(agentDonePrs);

        // Capture locally so concurrent calls don't interfere at downstream await points.
        // TriggerCooldown is also updated so that test overrides via the property setter are
        // observable after the call (existing tests set svc.TriggerCooldown before calling
        // ExecuteAsync; the combined assignment below ensures the property reflects the
        // per-call value while triggerCooldown governs all reads within this call).
        var triggerCooldown = TriggerCooldown = TimeSpan.FromMinutes(Math.Max(1, triggerCooldownMinutes));

        var limit = Math.Max(1, effectiveConcurrencyLimit);
        var repoTag = new KeyValuePair<string, object?>("repo_provider_id", repoProviderId);

        // ── Step 1: Build mergeability map — one call per PR, with re-probe for Unknown ──
        var mergeabilityMap = await BuildMergeabilityMapAsync(repoProvider, repoProviderId, agentDonePrs, repoTag, ct);

        // ── Step 2: Get or create in-flight set ──────────────────────────────
        // TODO: Step 2 was not extracted into a named private method (unlike Steps 1, 3–7).
        // The two statements below are trivial state-initialisation whose results are shared
        // between Steps 3 and 6b, making extraction awkward (would need out-parameters or a
        // tuple return). If strict compliance with "each step is a distinct private method" is
        // required, extract as e.g. InitializeInFlightState(repoProviderId, out var evictedThisCycle).
        var inFlight = _inFlight.GetOrAdd(repoProviderId, _ => new HashSet<int>());

        // Tracks PR numbers that were max-age evicted in Step 3 during this cycle.
        // Step 6b checks this set before re-admitting Behind PRs to prevent an evicted PR
        // from immediately re-acquiring the slot it was just freed from in the same call.
        // Allocated fresh on every ExecuteAsync call — no cross-cycle state.
        var evictedThisCycle = new HashSet<int>();

        // ── Step 3: Evict resolved in-flight entries ──────────────────────────
        var currentPrNumbers = new HashSet<int>(agentDonePrs.Select(p => p.Number));
        EvictInFlightSlots(inFlight, evictedThisCycle, currentPrNumbers, mergeabilityMap,
            repoProviderId, repoTag, UtcNow(), maxSlotAgeMinutes);

        // ── Step 4: Get active run branches (for rework exclusion) ───────────
        var (activeRunBranches, activeRunBranchesUnavailable) = await FetchActiveRunBranchesAsync(ct);

        // ── Step 5: Order candidates — auto-merge first, then by cooldown, random within each tier
        var sorted = OrderCandidates(agentDonePrs, repoProviderId, triggerCooldown);

        // ── Step 6a: Handle Conflicted PRs — swap linked issue to agent:next ─
        await TriggerConflictReworkAsync(sorted, mergeabilityMap, activeRunBranches,
            activeRunBranchesUnavailable, repoProvider, issueProvider, issueProviderId, repoTag, ct);

        // ── Step 6b: Select and trigger eligible branch updates ───────────────
        await SelectAndTriggerBranchUpdatesAsync(sorted, inFlight, evictedThisCycle, mergeabilityMap,
            activeRunBranches, activeRunBranchesUnavailable, repoProvider, repoProviderId, repoTag, limit, triggerCooldown, ct);

        // ── Step 7: Stale branch cleanup ──────────────────────────────────────
        await RunStaleBranchCleanupIfDueAsync(repoProvider, issueProvider, agentDonePrs,
            repoProviderId, repoTag, branchCleanupEnabled, cleanupIntervalMinutes, ct);
    }

    // ── Step 1 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Probes each PR's mergeability status and re-probes any that returned
    /// <see cref="PrMergeabilityStatus.Unknown"/> after a short batch delay.
    /// </summary>
    /// <remarks>
    /// GitHub's mergeable_state is computed lazily, on-demand. The first
    /// GET /repos/{owner}/{repo}/pulls/{number} call schedules a background job and
    /// returns "unknown" immediately. A subsequent call (a few seconds later) returns
    /// the resolved state: "behind", "clean", "dirty", "blocked", etc.
    ///
    /// Crucially, every push to the base branch invalidates the cached mergeability for
    /// ALL open PRs simultaneously. In a high-velocity repo with multiple merges per day,
    /// this means most PRs are perpetually "unknown" — the next merge arrives before the
    /// background job resolves the previous batch.
    ///
    /// Workaround (matches pattern used by automerge-action and other automerge tooling):
    ///   1. First probe: call GET /pulls/{n} for each PR → triggers background recompute.
    ///   2. Collect Unknown results.
    ///   3. Wait <see cref="MergeabilityReprobeDelay"/> (default 5 s) once for the entire batch.
    ///   4. Re-probe only the Unknown PRs → pick up resolved state.
    ///
    /// Conservative fallback is preserved: a PR that is still Unknown after re-probe is
    /// treated the same as before (skipped this cycle, kept in-flight if already there).
    ///
    /// References:
    ///   - https://docs.github.com/en/rest/pulls/pulls#get-a-pull-request (mergeable_state)
    ///   - https://stackoverflow.com/a/30620973 (GitHub staff confirming lazy computation)
    ///   - https://github.com/mergerine/github-mergerine/issues/9 (probe-trigger pattern)
    /// </remarks>
    private async Task<Dictionary<int, PrMergeabilityStatus>> BuildMergeabilityMapAsync(
        IRepositoryProvider repoProvider,
        string repoProviderId,
        IReadOnlyList<PullRequestSummary> agentDonePrs,
        KeyValuePair<string, object?> repoTag,
        CancellationToken ct)
    {
        var mergeabilityMap = new Dictionary<int, PrMergeabilityStatus>(agentDonePrs.Count);
        var unknownAfterFirstProbe = new List<PullRequestSummary>();

        foreach (var pr in agentDonePrs)
        {
            try
            {
                var status = await repoProvider.IsPullRequestBehindBaseAsync(pr.Number, ct);
                mergeabilityMap[pr.Number] = status;
                if (status == PrMergeabilityStatus.Unknown)
                    unknownAfterFirstProbe.Add(pr);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Conservative fallback: treat a failed probe as Unknown so one flaky PR or
                // transient API error does not abort the entire mergeability pass. Unknown status
                // means Step 3 will keep the slot occupied (no eviction) and Step 6b will skip
                // the PR — safe no-op behaviour until the probe succeeds in a future cycle.
                _logger.Warning(ex,
                    "HousekeepingService: mergeability probe failed for PR #{PrNumber} in repo {RepoId} — treating as Unknown (conservative fallback)",
                    pr.Number, repoProviderId);
                mergeabilityMap[pr.Number] = PrMergeabilityStatus.Unknown;
            }
        }

        // Re-probe PRs whose first probe returned Unknown. A single delay fires once for the
        // entire batch (not once per PR) to keep the added latency proportional regardless of
        // how many PRs are Unknown. With a high-velocity repo, every base-branch merge
        // invalidates all open-PR mergeability caches simultaneously, so batching is important.
        if (unknownAfterFirstProbe.Count > 0)
        {
            _logger.Debug(
                "HousekeepingService: {Count} PR(s) returned Unknown on first probe in repo {RepoId} — " +
                "waiting {DelayMs}ms then re-probing (GitHub lazy mergeability workaround)",
                unknownAfterFirstProbe.Count, repoProviderId, (int)MergeabilityReprobeDelay.TotalMilliseconds);

            PipelineTelemetry.HousekeepingReprobeTriggered.Add(1, repoTag);

            await ReprobeDelayFunc(MergeabilityReprobeDelay, ct);

            foreach (var prNumber in unknownAfterFirstProbe.Select(pr => pr.Number))
            {
                try
                {
                    var resolved = await repoProvider.IsPullRequestBehindBaseAsync(prNumber, ct);
                    mergeabilityMap[prNumber] = resolved;

                    if (resolved != PrMergeabilityStatus.Unknown)
                    {
                        _logger.Debug(
                            "HousekeepingService: PR #{PrNumber} re-probe resolved to {Status} in repo {RepoId}",
                            prNumber, resolved, repoProviderId);
                        var resolvedLabel = resolved switch
                        {
                            PrMergeabilityStatus.Behind     => "behind",
                            PrMergeabilityStatus.UpToDate   => "up_to_date",
                            PrMergeabilityStatus.Conflicted => "conflicted",
                            PrMergeabilityStatus.Blocked    => "blocked",
                            _                               => "unknown",
                        };
                        var resolvedTag = new KeyValuePair<string, object?>("resolved_state", resolvedLabel);
                        PipelineTelemetry.HousekeepingReprobeResolved.Add(1, repoTag, resolvedTag);
                    }
                    else
                    {
                        _logger.Debug(
                            "HousekeepingService: PR #{PrNumber} re-probe still Unknown in repo {RepoId} — skipping this cycle (conservative fallback)",
                            prNumber, repoProviderId);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.Warning(ex,
                        "HousekeepingService: re-probe failed for PR #{PrNumber} in repo {RepoId} — keeping Unknown (conservative fallback)",
                        prNumber, repoProviderId);
                    // keep Unknown — already set from first pass
                }
            }
        }

        return mergeabilityMap;
    }

    // ── Step 3 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes in-flight entries that are no longer valid: PRs absent from the current list
    /// (merged/closed), PRs whose mergeability resolved to a non-blocking state, and PRs that
    /// have held the slot longer than <paramref name="maxSlotAgeMinutes"/>.
    /// </summary>
    /// <remarks>
    /// Mutates <paramref name="inFlight"/> and <paramref name="evictedThisCycle"/> in-place.
    /// Also removes entries from the <c>_inFlightAt</c> instance field (all eviction paths)
    /// and from <c>_lastTriggeredAt</c> (PR-absent path only).
    /// </remarks>
    private void EvictInFlightSlots(
        HashSet<int> inFlight,
        HashSet<int> evictedThisCycle,
        HashSet<int> currentPrNumbers,
        // TODO: mergeabilityMap should be IReadOnlyDictionary<int, PrMergeabilityStatus> —
        // this method only reads the map. See matching TODO in TriggerConflictReworkAsync.
        Dictionary<int, PrMergeabilityStatus> mergeabilityMap,
        string repoProviderId,
        KeyValuePair<string, object?> repoTag,
        DateTimeOffset now,
        int maxSlotAgeMinutes)
    {
        foreach (var prNumber in inFlight.ToList())
        {
            if (!currentPrNumbers.Contains(prNumber))
            {
                inFlight.Remove(prNumber);
                _lastTriggeredAt.TryRemove((repoProviderId, prNumber), out _); // PR merged/closed — clear cooldown state
                _inFlightAt.TryRemove((repoProviderId, prNumber), out _);       // clear slot entry time
                PipelineTelemetry.HousekeepingEvicted.Add(1, repoTag);
            }
            else
            {
                var status = mergeabilityMap[prNumber];

                // Max-age eviction: if this slot has been held longer than the configured threshold,
                // release it regardless of mergeability status. This prevents a single PR stuck at
                // Blocked/Unknown from monopolising the slot and starving all other behind PRs.
                // maxSlotAgeMinutes=0 disables time-based eviction (preserves previous behaviour).
                var slotAge = maxSlotAgeMinutes > 0
                    && _inFlightAt.TryGetValue((repoProviderId, prNumber), out var acquiredAt)
                    ? (now - acquiredAt).TotalMinutes
                    : 0.0;

                if (maxSlotAgeMinutes > 0 && slotAge >= maxSlotAgeMinutes)
                {
                    _logger.Information(
                        "HousekeepingService: PR #{PrNumber} in repo {RepoId} has held the slot for {SlotAge:F0}m (max {MaxAge}m, status={Status}) — evicting to unblock other PRs",
                        prNumber, repoProviderId, slotAge, maxSlotAgeMinutes, status);
                    inFlight.Remove(prNumber);
                    _inFlightAt.TryRemove((repoProviderId, prNumber), out _);
                    evictedThisCycle.Add(prNumber);
                    PipelineTelemetry.HousekeepingEvicted.Add(1, repoTag);
                }
                else if (status != PrMergeabilityStatus.Blocked && status != PrMergeabilityStatus.Unknown)
                {
                    inFlight.Remove(prNumber);
                    _inFlightAt.TryRemove((repoProviderId, prNumber), out _);
                    PipelineTelemetry.HousekeepingEvicted.Add(1, repoTag);
                }
            }
        }
    }

    // ── Step 4 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the set of branch names currently occupied by active pipeline runs.
    /// Returns <c>(branches, unavailable: false)</c> on success, or
    /// <c>(empty, unavailable: true)</c> on failure so callers can apply the conservative
    /// skip-all fallback.
    /// </summary>
    private async Task<(HashSet<string> Branches, bool Unavailable)> FetchActiveRunBranchesAsync(CancellationToken ct)
    {
        try
        {
            var branches = await _runService.GetActiveRunBranchesAsync(ct);
            return (branches, false);
        }
        catch (Exception ex)
        {
            // Conservative fallback: if branch data is unavailable (e.g. Scheduler cannot reach the
            // orchestrator API), we must NOT proceed with branch updates — we cannot confirm which
            // branches are safe to update. Branch updates are skipped for this cycle (Steps 6a and 6b).
            // Requirement: "If branch name data is unavailable, housekeeping MUST default to
            // conservative behavior: skip branch updates for PRs where branch state cannot be confirmed."
            // The Scheduler-deployment path reaches here when GET /api/pipeline-runs/active-branches
            // fails (API pod down, 5xx, or 403 from a misconfigured auth key). PipelineApiRunHistoryClient
            // uses GetAsync + EnsureSuccessStatusCode so non-2xx responses propagate as HttpRequestException
            // and reach this catch block rather than being silently swallowed as empty lists.
            _logger.Warning(ex,
                "HousekeepingService: failed to get active runs for branch exclusion; skipping all branch updates AND conflict rework this cycle (conservative fallback)");
            return ([], true);
        }
    }

    // ── Step 5 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sorts <paramref name="agentDonePrs"/> into three priority tiers, with random ordering
    /// within each tier to prevent starvation.
    /// </summary>
    /// <remarks>
    /// Tier 0: auto-merge enabled + cooldown expired  → update urgently (human approved merge)<br/>
    /// Tier 1: no auto-merge + cooldown expired        → update when slot is free<br/>
    /// Tier 2: cooldown active (any)                   → deprioritised, recently triggered
    /// </remarks>
    private List<PullRequestSummary> OrderCandidates(
        IReadOnlyList<PullRequestSummary> agentDonePrs,
        string repoProviderId,
        TimeSpan triggerCooldown)
    {
        var now = UtcNow();
        return agentDonePrs
            .OrderBy(pr =>
            {
                var lastTriggered = _lastTriggeredAt.GetValueOrDefault((repoProviderId, pr.Number), DateTimeOffset.MinValue);
                var cooledDown = (now - lastTriggered) >= triggerCooldown;
                if (!cooledDown) return 2;   // recently triggered — back of queue
                if (pr.HasAutoMerge) return 0;   // auto-merge + cooled — front
                return 1;                        // no auto-merge + cooled — middle
            })
            .ThenBy(_ => Random.Shared.Next())
            .ToList();
    }

    // ── Step 6a ───────────────────────────────────────────────────────────────

    /// <summary>
    /// For each conflicted PR in <paramref name="sorted"/>, swaps the linked issue's label
    /// to <c>agent:next</c> to trigger a rework dispatch run — unless the PR's branch has an
    /// active run or active-run data was unavailable.
    /// </summary>
    private async Task TriggerConflictReworkAsync(
        IReadOnlyList<PullRequestSummary> sorted,
        // TODO: mergeabilityMap should be IReadOnlyDictionary<int, PrMergeabilityStatus> — this
        // method only reads the map (via indexer). The concrete Dictionary<> type unnecessarily
        // exposes a mutable interface. Same applies to SelectAndTriggerBranchUpdatesAsync and
        // EvictInFlightSlots. Change all three when the call-site return type of
        // BuildMergeabilityMapAsync is widened or an explicit cast is added.
        Dictionary<int, PrMergeabilityStatus> mergeabilityMap,
        // TODO: activeRunBranches should be IReadOnlySet<string> — this method only calls
        // Contains() and never mutates the set. As declared, a future edit could accidentally
        // call .Add()/.Remove() on the shared set and silently corrupt it across Steps 6a and 6b
        // in the same cycle. Same applies to SelectAndTriggerBranchUpdatesAsync.
        HashSet<string> activeRunBranches,
        bool activeRunBranchesUnavailable,
        IRepositoryProvider repoProvider,
        IIssueProvider issueProvider,
        string issueProviderId,
        KeyValuePair<string, object?> repoTag,
        CancellationToken ct)
    {
        foreach (var pr in sorted)
        {
            if (mergeabilityMap[pr.Number] != PrMergeabilityStatus.Conflicted)
                continue;

            // Skip if the branch still has an active run — the pod is live and the issue
            // will be re-queued naturally when the run completes. Swapping the label now
            // would leave the issue stuck at agent:next with no new dispatch possible.
            // Also skip conservatively when active-run data was unavailable (Step 4 threw) —
            // we cannot confirm whether the branch is safe to rework.
            if (activeRunBranchesUnavailable || activeRunBranches.Contains(pr.BranchName))
            {
                _logger.Debug(
                    "HousekeepingService: PR #{PrNumber} is conflicted but branch '{Branch}' has an active run — skipping rework swap",
                    pr.Number, pr.BranchName);
                continue;
            }

            await TriggerReworkAsync(repoProvider, issueProvider, issueProviderId, pr, repoTag, ct);
        }
    }

    // ── Step 6b ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Iterates the sorted candidate list and triggers server-side branch updates for eligible
    /// Behind PRs, up to <paramref name="limit"/> concurrent in-flight slots.
    /// </summary>
    /// <remarks>
    /// Skip guards (in evaluation order):
    /// <list type="number">
    ///   <item>Concurrency limit reached — break</item>
    ///   <item>Draft PR — skip</item>
    ///   <item>Active-run data unavailable — skip (conservative fallback)</item>
    ///   <item>Branch has an active run — skip</item>
    ///   <item>PR was max-age evicted this cycle — skip (prevent same-cycle re-admission)</item>
    ///   <item>Already in-flight — skip</item>
    ///   <item>Not Behind — skip</item>
    ///   <item>Within trigger cooldown — skip</item>
    /// </list>
    /// Mutates <paramref name="inFlight"/>, <c>_inFlightAt</c>, and <c>_lastTriggeredAt</c>
    /// in-place for each PR that passes all guards.
    /// </remarks>
    private async Task SelectAndTriggerBranchUpdatesAsync(
        IReadOnlyList<PullRequestSummary> sorted,
        HashSet<int> inFlight,
        HashSet<int> evictedThisCycle,
        // TODO: mergeabilityMap should be IReadOnlyDictionary<int, PrMergeabilityStatus> —
        // this method only reads the map. See matching TODO in TriggerConflictReworkAsync.
        Dictionary<int, PrMergeabilityStatus> mergeabilityMap,
        // TODO: activeRunBranches should be IReadOnlySet<string> — this method only calls
        // Contains() and never mutates the set. See matching TODO in TriggerConflictReworkAsync.
        HashSet<string> activeRunBranches,
        bool activeRunBranchesUnavailable,
        IRepositoryProvider repoProvider,
        string repoProviderId,
        KeyValuePair<string, object?> repoTag,
        int limit,
        TimeSpan triggerCooldown,
        CancellationToken ct)
    {
        foreach (var pr in sorted)
        {
            if (inFlight.Count >= limit)
                break;

            if (pr.IsDraft)
            {
                PipelineTelemetry.HousekeepingSkipped.Add(1, repoTag);
                continue;
            }

            // Conservative fallback: if active-run branch data was unavailable (Step 4 threw),
            // skip ALL branch updates this cycle — we cannot confirm which branches are safe.
            // NOTE: The telemetry counter is incremented per-PR but no per-PR log is emitted
            //   for the conservative-skip path. If the API is down for an extended period (e.g. 30 min),
            //   operators have no per-PR visibility into which PRs were skipped — only the aggregate
            //   counter and the single Warning-level log from Step 4. Consider logging PR number and
            //   branch name here (Debug or Information level) so housekeeping cycles with many
            //   conservative skips can be diagnosed without ambiguity.
            if (activeRunBranchesUnavailable)
            {
                PipelineTelemetry.HousekeepingSkipped.Add(1, repoTag);
                continue;
            }

            if (activeRunBranches.Contains(pr.BranchName))
            {
                PipelineTelemetry.HousekeepingSkipped.Add(1, repoTag);
                continue;
            }

            // Max-age eviction guard: if this PR was evicted from the slot in Step 3 during
            // this same cycle, do not re-admit it. This prevents a chronically-Behind PR from
            // immediately re-acquiring the slot it was just released from, which would make
            // max-age eviction a no-op and starve other eligible Behind PRs.
            // On the next poll tick, evictedThisCycle is re-allocated empty — the PR is freely
            // eligible for re-selection on subsequent cycles (subject to normal cooldown rules).
            if (evictedThisCycle.Contains(pr.Number))
            {
                PipelineTelemetry.HousekeepingSkipped.Add(1, repoTag);
                continue;
            }

            if (inFlight.Contains(pr.Number))
            {
                PipelineTelemetry.HousekeepingSkipped.Add(1, repoTag);
                continue;
            }

            var mergeability = mergeabilityMap[pr.Number];
            if (mergeability != PrMergeabilityStatus.Behind)
            {
                PipelineTelemetry.HousekeepingSkipped.Add(1, repoTag);
                continue;
            }

            // Cooldown guard: skip if this PR was triggered too recently.
            // This prevents a PR whose CI hasn't finished yet (Blocked→clean→behind
            // fast-cycle) from immediately re-occupying the slot and starving others.
            // TODO: UtcNow() is called inside the foreach loop on every iteration rather than
            // once at entry. If FireAndForget introduces any delay (e.g. in integration tests
            // where it awaits the real task), the clock can advance between iterations and
            // produce inconsistent now6b values within the same logical cycle. Capture
            // UtcNow() once at method entry (consistent with EvictInFlightSlots and
            // OrderCandidates) and pass the captured value down to the cooldown check.
            var now6b = UtcNow();
            var lastTriggered = _lastTriggeredAt.GetValueOrDefault((repoProviderId, pr.Number), DateTimeOffset.MinValue);
            if ((now6b - lastTriggered) < triggerCooldown)
            {
                _logger.Debug(
                    "HousekeepingService: PR #{PrNumber} is behind but was triggered {Elapsed:F0}m ago (cooldown {Cooldown:F0}m) — skipping to allow other PRs to proceed",
                    pr.Number, (now6b - lastTriggered).TotalMinutes, triggerCooldown.TotalMinutes);
                PipelineTelemetry.HousekeepingSkipped.Add(1, repoTag);
                continue;
            }

            _lastTriggeredAt[(repoProviderId, pr.Number)] = now6b;
            inFlight.Add(pr.Number);
            _inFlightAt[(repoProviderId, pr.Number)] = now6b; // record slot acquisition time for max-age eviction
            PipelineTelemetry.HousekeepingTriggered.Add(1, repoTag);
            await FireAndForget(UpdateAsync(repoProvider, repoProviderId, pr.Number, repoTag));
        }
    }

    // ── Step 7 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs stale-branch cleanup if <paramref name="branchCleanupEnabled"/> is true and
    /// the cleanup interval has elapsed since the last pass for this repository.
    /// </summary>
    private async Task RunStaleBranchCleanupIfDueAsync(
        IRepositoryProvider repoProvider,
        IIssueProvider issueProvider,
        IReadOnlyList<PullRequestSummary> agentDonePrs,
        string repoProviderId,
        KeyValuePair<string, object?> repoTag,
        bool branchCleanupEnabled,
        int cleanupIntervalMinutes,
        CancellationToken ct)
    {
        if (!branchCleanupEnabled)
            return;

        var now = UtcNow();
        var lastCleanup = _lastCleanupAt.GetValueOrDefault(repoProviderId, DateTimeOffset.MinValue);
        var intervalElapsed = (now - lastCleanup).TotalMinutes >= cleanupIntervalMinutes;

        if (intervalElapsed)
        {
            _lastCleanupAt[repoProviderId] = now;
            await RunBranchCleanupAsync(repoProvider, issueProvider, agentDonePrs, repoTag, ct);
        }
    }

    // ── Existing private helpers (unchanged) ──────────────────────────────────

    /// <summary>
    /// Lists all agent branches, skips those with an open PR or an active issue label,
    /// and deletes the rest.
    /// </summary>
    private async Task RunBranchCleanupAsync(
        IRepositoryProvider repoProvider,
        IIssueProvider issueProvider,
        IReadOnlyList<PullRequestSummary> agentDonePrs,
        KeyValuePair<string, object?> repoTag,
        CancellationToken ct)
    {
        IReadOnlyList<string> allAgentBranches;
        try
        {
            allAgentBranches = await repoProvider.ListAgentBranchesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "HousekeepingService: failed to list agent branches for cleanup: {Error}", ex.Message);
            return;
        }

        if (allAgentBranches.Count == 0)
            return;

        // Build a complete set of branches that have open PRs — these must never be deleted.
        // NOTE: We do NOT rely solely on agentDonePrs here. That list is capped by
        // ClosedLoopMaxPagesToFetch (default 10 pages). In repos with many open agent PRs, PRs
        // beyond the cap are absent, and their branches would be incorrectly deleted. Instead,
        // fetch all open agent PRs independently with an unlimited page scan so that every open
        // PR's branch is protected regardless of the housekeeping input cap.
        HashSet<string> branchesWithOpenPr;
        try
        {
            branchesWithOpenPr = await FetchAllOpenAgentPrBranchesAsync(repoProvider, ct);
        }
        catch (Exception ex)
        {
            // Skip cleanup this cycle — falling back to the truncated agentDonePrs list would
            // reproduce the original bug: branches whose PRs were beyond the pagination cap
            // could still be deleted. It is safer to skip than to delete live branches.
            _logger.Warning(ex,
                "HousekeepingService: failed to fetch complete open-PR list for branch cleanup; skipping branch cleanup this cycle: {Error}",
                ex.Message);
            return;
        }

        foreach (var branchName in allAgentBranches)
        {
            // Skip if an open PR exists for this branch
            if (branchesWithOpenPr.Contains(branchName))
                continue;

            // Extract issue identifier from branch name: "feature/auto-{issueId}-{slug}"
            var issueId = ExtractIssueId(branchName);
            if (issueId is null)
            {
                _logger.Debug(
                    "HousekeepingService: cannot extract issue ID from branch {BranchName} — skipping",
                    branchName);
                continue;
            }

            // Check issue label state — skip if issue is actively being worked on
            IssueDetail issue;
            try
            {
                issue = await issueProvider.GetIssueAsync(new IssueIdentifier(issueId), ct);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex,
                    "HousekeepingService: failed to fetch issue {IssueId} for branch {BranchName} cleanup — skipping: {Error}",
                    issueId, branchName, ex.Message);
                continue;
            }

            if (issue.Labels.Any(l => ActiveLabels.Contains(l)))
            {
                _logger.Debug(
                    "HousekeepingService: issue {IssueId} for branch {BranchName} has active label — skipping cleanup",
                    issueId, branchName);
                continue;
            }

            // Safe to delete
            try
            {
                await repoProvider.DeleteBranchAsync(branchName, ct);
                PipelineTelemetry.HousekeepingBranchDeleted.Add(1, repoTag);
                _logger.Information(
                    "HousekeepingService: deleted stale branch {BranchName} (issue {IssueId})",
                    branchName, issueId);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex,
                    "HousekeepingService: failed to delete branch {BranchName}: {Error}",
                    branchName, ex.Message);
            }
        }
    }

    /// <summary>
    /// Fetches all open agent-created PR branch names from the repository, paginating until
    /// exhausted. Used by <see cref="RunBranchCleanupAsync"/> to build a complete branch-protection
    /// set independently of the (possibly page-capped) <c>agentDonePrs</c> input.
    /// </summary>
    private static async Task<HashSet<string>> FetchAllOpenAgentPrBranchesAsync(
        IRepositoryProvider repoProvider, CancellationToken ct)
    {
        var branches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var page = 1;
        const int PageSize = 100;
        const int MaxPages = 50; // 5 000 open agent PRs — unreachable ceiling, guards against malformed HasMore

        while (true)
        {
            var result = await repoProvider.ListOpenPullRequestsAsync(page, PageSize, null, ct);
            foreach (var pr in result.Items)
            {
                if (pr.BranchName.StartsWith(PipelineConstants.BranchPrefix, StringComparison.Ordinal))
                    branches.Add(pr.BranchName);
            }

            if (!result.HasMore)
                break;

            // Safety cap: 50 pages × 100 PRs/page = 5 000 open agent PRs. Unreachable in
            // practice, but prevents an unbounded loop if HasMore is malformed.
            if (page >= MaxPages)
                break;

            page++;
        }

        return branches;
    }

    /// <summary>
    /// Extracts the issue identifier from an agent branch name.
    /// Branch format: <c>feature/auto-{issueId}-{slug}</c>.
    /// Returns null if the format does not match.
    /// </summary>
    internal static string? ExtractIssueId(string branchName)
    {
        if (!branchName.StartsWith(PipelineConstants.BranchPrefix, StringComparison.Ordinal))
            return null;

        var rest = branchName[PipelineConstants.BranchPrefix.Length..]; // "123-fix-login"
        if (rest.Length == 0)
            return null;

        var dashIdx = rest.IndexOf('-');
        return dashIdx > 0 ? rest[..dashIdx] : rest;
    }

    /// <summary>
    /// Handles a conflicted PR: extracts linked issues and swaps eligible issue labels
    /// to <c>agent:next</c> so the pipeline dispatches a rework run.
    /// </summary>
    private async Task TriggerReworkAsync(
        IRepositoryProvider repoProvider,
        IIssueProvider issueProvider,
        string issueProviderId,
        PullRequestSummary pr,
        KeyValuePair<string, object?> repoTag,
        CancellationToken ct)
    {
        IReadOnlyList<string> linkedIssues;
        try
        {
            linkedIssues = await repoProvider.ExtractLinkedIssuesAsync(pr.Number, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "HousekeepingService: failed to extract linked issues for PR #{PrNumber}: {Error}",
                pr.Number, ex.Message);
            return;
        }

        if (linkedIssues.Count == 0)
        {
            _logger.Information(
                "HousekeepingService: PR #{PrNumber} is conflicted but has no linked issues — skipping rework",
                pr.Number);
            return;
        }

        foreach (var issueIdString in linkedIssues)
        {
            await TrySwapIssueToNextAsync(issueProvider, issueProviderId, pr.Number, issueIdString, repoTag, ct);
        }
    }

    /// <summary>
    /// Fetches the issue linked to a conflicted PR and swaps its label to <c>agent:next</c>
    /// so it is re-queued for rework — unless the issue already carries an active label
    /// (see <see cref="ActiveLabels"/>) or an abandonment label (see <see cref="TerminalReworkBlockers"/>),
    /// in which case it returns early without modifying any labels.
    /// <c>agent:error</c>, <c>agent:needs-refinement</c>, and <c>agent:done</c> are valid rework
    /// targets — an open conflicted PR always needs another agent run regardless of the issue's
    /// current label. Only <c>agent:wont-do</c> and <c>agent:cancelled</c> block re-queue, as
    /// these represent explicit human decisions to abandon the work.
    /// </summary>
    private async Task TrySwapIssueToNextAsync(
        IIssueProvider issueProvider,
        string issueProviderId,
        int prNumber,
        string issueIdString,
        KeyValuePair<string, object?> repoTag,
        CancellationToken ct)
    {
        IssueIdentifier issueId = issueIdString;
        IssueDetail issue;
        try
        {
            issue = await issueProvider.GetIssueAsync(issueId, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "HousekeepingService: failed to fetch issue {IssueId} linked to PR #{PrNumber}: {Error}",
                issueIdString, prNumber, ex.Message);
            return;
        }

        if (issue.Labels.Any(l => ActiveLabels.Contains(l)))
        {
            _logger.Debug(
                "HousekeepingService: issue {IssueId} linked to conflicted PR #{PrNumber} already has an active label — skipping rework swap",
                issueIdString, prNumber);
            return;
        }

        if (issue.Labels.Any(l => TerminalReworkBlockers.Contains(l)))
        {
            _logger.Debug(
                "HousekeepingService: issue {IssueId} linked to conflicted PR #{PrNumber} has an abandonment label (agent:wont-do or agent:cancelled) — skipping rework swap",
                issueIdString, prNumber);
            return;
        }

        try
        {
            await AgentLabelOperations.SwapAsync(
                removeLabel: (label, c) => issueProvider.RemoveLabelAsync(issueId, label, c),
                addLabel: (label, c) => issueProvider.AddLabelAsync(issueId, label, c),
                newLabel: AgentLabels.Next,
                ct: ct,
                expectedCurrentLabel: issue.Labels.FirstOrDefault(l => l.StartsWith("agent:", StringComparison.Ordinal)),
                identifier: issueIdString);

            PipelineTelemetry.HousekeepingConflictReworkTriggered.Add(1, repoTag);
            _logger.Information(
                "HousekeepingService: re-queued issue {IssueId} for rework due to merge conflict on PR #{PrNumber} (issueProvider: {IssueProviderId})",
                issueIdString, prNumber, issueProviderId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "HousekeepingService: failed to swap label on issue {IssueId} linked to PR #{PrNumber}: {Error}",
                issueIdString, prNumber, ex.Message);
        }
    }

    /// <summary>
    /// Fire-and-forget wrapper for <see cref="IRepositoryProvider.UpdatePullRequestBranchAsync"/>.
    /// Uses <see cref="CancellationToken.None"/> so the HTTP call completes independently.
    /// </summary>
    private async Task UpdateAsync(
        IRepositoryProvider repoProvider,
        string repoProviderId,
        int prNumber,
        KeyValuePair<string, object?> repoTag)
    {
        try
        {
            await repoProvider.UpdatePullRequestBranchAsync(prNumber, CancellationToken.None);
            PipelineTelemetry.HousekeepingSucceeded.Add(1, repoTag);
            _logger.Information(
                "Housekeeping: updated branch for PR #{PrNumber} on repo {RepoProviderId}",
                prNumber, repoProviderId);
        }
        catch (Exception ex)
        {
            PipelineTelemetry.HousekeepingFailed.Add(1, repoTag);
            _logger.Warning(ex,
                "Housekeeping: failed to update branch for PR #{PrNumber} on {RepoProviderId}: {Error}",
                prNumber, repoProviderId, ex.Message);
        }
    }
}
