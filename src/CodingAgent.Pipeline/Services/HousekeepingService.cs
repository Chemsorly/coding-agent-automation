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
    private readonly IStaleBranchCleaner _staleBranchCleaner;
    private readonly IIssueReworkService _issueReworkService;
    private readonly ILogger _logger;

    /// <summary>
    /// Controls how fire-and-forget update tasks are dispatched.
    /// In production: discards the task (true fire-and-forget).
    /// In tests: overridden to await synchronously so assertions are deterministic.
    /// </summary>
    internal Func<Task, Task> FireAndForget { get; set; } = task => { _ = task; return Task.CompletedTask; };

    /// <summary>
    /// Overridable time source for the cooldown and ordering guards.
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

    public HousekeepingService(
        IOrchestratorRunService runService,
        IStaleBranchCleaner staleBranchCleaner,
        IIssueReworkService issueReworkService,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(staleBranchCleaner);
        ArgumentNullException.ThrowIfNull(issueReworkService);
        ArgumentNullException.ThrowIfNull(logger);
        _runService = runService;
        _staleBranchCleaner = staleBranchCleaner;
        _issueReworkService = issueReworkService;
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
        bool wasInputTruncated,
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

        // Capture UtcNow once for the entire tick so all steps in this call share a consistent
        // timestamp. Previously UtcNow() was called separately in OrderCandidates and inside the
        // foreach in SelectAndTriggerBranchUpdatesAsync, producing inconsistent values within
        // the same logical cycle.
        var now = UtcNow();

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
            repoProviderId, repoTag, now, maxSlotAgeMinutes);

        // ── Step 4: Get active run branches (for rework exclusion) ───────────
        var (activeRunBranches, activeRunBranchesUnavailable) = await FetchActiveRunBranchesAsync(ct);

        // ── Step 5: Order candidates — auto-merge first, then by cooldown, random within each tier
        var sorted = OrderCandidates(agentDonePrs, repoProviderId, triggerCooldown, now);

        // ── Step 6a: Handle Conflicted PRs — swap linked issue to agent:next ─
        await _issueReworkService.TriggerConflictReworkAsync(sorted, mergeabilityMap, activeRunBranches,
            activeRunBranchesUnavailable, repoProvider, issueProvider, issueProviderId, repoTag, ct);

        // ── Step 6b: Select and trigger eligible branch updates ───────────────
        await SelectAndTriggerBranchUpdatesAsync(sorted, inFlight, evictedThisCycle, mergeabilityMap,
            activeRunBranches, activeRunBranchesUnavailable, repoProvider, repoProviderId, repoTag, limit, triggerCooldown, now, ct);

        // ── Step 7: Stale branch cleanup ──────────────────────────────────────
        await _staleBranchCleaner.RunIfDueAsync(repoProvider, issueProvider, agentDonePrs,
            repoProviderId, repoTag, branchCleanupEnabled, cleanupIntervalMinutes,
            wasInputTruncated, ct);
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
    private async Task<IReadOnlyDictionary<int, PrMergeabilityStatus>> BuildMergeabilityMapAsync(
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
                            PrMergeabilityStatus.Behind => "behind",
                            PrMergeabilityStatus.UpToDate => "up_to_date",
                            PrMergeabilityStatus.Conflicted => "conflicted",
                            PrMergeabilityStatus.Blocked => "blocked",
                            _ => "unknown",
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
        IReadOnlyDictionary<int, PrMergeabilityStatus> mergeabilityMap,
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
    private async Task<(IReadOnlySet<string> Branches, bool Unavailable)> FetchActiveRunBranchesAsync(CancellationToken ct)
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
            return (new HashSet<string>(), true);
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
        TimeSpan triggerCooldown,
        DateTimeOffset now)
    {
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
        IReadOnlyDictionary<int, PrMergeabilityStatus> mergeabilityMap,
        IReadOnlySet<string> activeRunBranches,
        bool activeRunBranchesUnavailable,
        IRepositoryProvider repoProvider,
        string repoProviderId,
        KeyValuePair<string, object?> repoTag,
        int limit,
        TimeSpan triggerCooldown,
        DateTimeOffset now,
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
            var lastTriggered = _lastTriggeredAt.GetValueOrDefault((repoProviderId, pr.Number), DateTimeOffset.MinValue);
            if ((now - lastTriggered) < triggerCooldown)
            {
                _logger.Debug(
                    "HousekeepingService: PR #{PrNumber} is behind but was triggered {Elapsed:F0}m ago (cooldown {Cooldown:F0}m) — skipping to allow other PRs to proceed",
                    pr.Number, (now - lastTriggered).TotalMinutes, triggerCooldown.TotalMinutes);
                PipelineTelemetry.HousekeepingSkipped.Add(1, repoTag);
                continue;
            }

            _lastTriggeredAt[(repoProviderId, pr.Number)] = now;
            inFlight.Add(pr.Number);
            _inFlightAt[(repoProviderId, pr.Number)] = now; // record slot acquisition time for max-age eviction
            PipelineTelemetry.HousekeepingTriggered.Add(1, repoTag);
            await FireAndForget(UpdateAsync(repoProvider, repoProviderId, pr.Number, repoTag));
        }
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────────

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
