using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

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
    /// Terminal issue labels that must never be re-queued via conflict rework.
    /// Distinct from <see cref="ActiveLabels"/> to avoid affecting stale-branch cleanup,
    /// which should still delete branches for terminal-state issues.
    /// <c>agent:error</c> and <c>agent:needs-refinement</c> are intentionally excluded —
    /// they are human-placed signals that the issue should be re-queued for rework.
    /// </summary>
    private static readonly HashSet<string> TerminalReworkBlockers = new(StringComparer.Ordinal)
    {
        AgentLabels.Done,
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
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repoProvider);
        ArgumentNullException.ThrowIfNull(repoProviderId);
        ArgumentNullException.ThrowIfNull(issueProvider);
        ArgumentNullException.ThrowIfNull(issueProviderId);
        ArgumentNullException.ThrowIfNull(agentDonePrs);

        var limit = Math.Max(1, effectiveConcurrencyLimit);
        var repoTag = new KeyValuePair<string, object?>("repo_provider_id", repoProviderId);

        // ── Step 1: Build mergeability map — one call per PR, reused below ──
        var mergeabilityMap = new Dictionary<int, PrMergeabilityStatus>(agentDonePrs.Count);
        foreach (var pr in agentDonePrs)
        {
            mergeabilityMap[pr.Number] = await repoProvider.IsPullRequestBehindBaseAsync(pr.Number, ct);
        }

        // ── Step 2: Get or create in-flight set ──────────────────────────────
        var inFlight = _inFlight.GetOrAdd(repoProviderId, _ => new HashSet<int>());

        // ── Step 3: Evict resolved in-flight entries ──────────────────────────
        var currentPrNumbers = new HashSet<int>(agentDonePrs.Select(p => p.Number));
        foreach (var prNumber in inFlight.ToList())
        {
            if (!currentPrNumbers.Contains(prNumber))
            {
                inFlight.Remove(prNumber);
                _lastTriggeredAt.TryRemove((repoProviderId, prNumber), out _); // PR merged/closed — clear cooldown state
                PipelineTelemetry.HousekeepingEvicted.Add(1, repoTag);
            }
            else
            {
                var status = mergeabilityMap[prNumber];
                if (status != PrMergeabilityStatus.Blocked && status != PrMergeabilityStatus.Unknown)
                {
                    inFlight.Remove(prNumber);
                    PipelineTelemetry.HousekeepingEvicted.Add(1, repoTag);
                }
            }
        }

        // ── Step 4: Get active run branches (for rework exclusion) ───────────
        HashSet<string> activeRunBranches;
        bool activeRunBranchesUnavailable = false;
        try
        {
            activeRunBranches = await _runService.GetActiveRunBranchesAsync(ct);
        }
        catch (Exception ex)
        {
            // Conservative fallback: if branch data is unavailable (e.g. Scheduler cannot reach the
            // orchestrator API), we must NOT proceed with branch updates — we cannot confirm which
            // branches are safe to update. Branch updates are skipped for this cycle (Steps 6a and 6b).
            // Requirement: "If branch name data is unavailable, housekeeping MUST default to
            // conservative behavior: skip branch updates for PRs where branch state cannot be confirmed."
            // NOTE: The Scheduler-deployment path reaches here when GET /api/pipeline-runs/active-branches
            //   fails (API pod down, auth misconfiguration, 5xx, etc.). The conservative skip covers correctness,
            //   but the root causes should also be addressed:
            //   1. PipelineApiRunHistoryClient.GetActiveBranchesAsync uses GetFromJsonAsync which silently
            //      returns null (→ []) on non-2xx responses instead of throwing — add EnsureSuccessStatusCode()
            //      before deserialization so HTTP errors propagate and trigger this conservative path.
            //   2. The /api/pipeline-runs/active-branches endpoint requires ApiAuthPolicies.Operator — the
            //      Scheduler HttpClient must authenticate with an operator-tier key, not an agent-tier key,
            //      or 403s will be silently swallowed as empty lists.
            _logger.Warning(ex,
                "HousekeepingService: failed to get active runs for branch exclusion; skipping all branch updates this cycle (conservative fallback)");
            activeRunBranches = [];
            activeRunBranchesUnavailable = true;
        }

        // ── Step 5: Order candidates — auto-merge first, then by cooldown, random within each tier
        // Tier 0: auto-merge enabled + cooldown expired  → update urgently (human approved merge)
        // Tier 1: no auto-merge + cooldown expired        → update when slot is free
        // Tier 2: cooldown active (any)                   → deprioritised, recently triggered
        // Random within each tier prevents starvation among peers.
        var now5 = UtcNow();
        var sorted = agentDonePrs
            .OrderBy(pr =>
            {
                var lastTriggered = _lastTriggeredAt.GetValueOrDefault((repoProviderId, pr.Number), DateTimeOffset.MinValue);
                var cooledDown = (now5 - lastTriggered) >= TriggerCooldown;
                if (!cooledDown)     return 2;   // recently triggered — back of queue
                if (pr.HasAutoMerge) return 0;   // auto-merge + cooled — front
                return 1;                        // no auto-merge + cooled — middle
            })
            .ThenBy(_ => Random.Shared.Next())
            .ToList();

        // ── Step 6a: Handle Conflicted PRs — swap linked issue to agent:next ─
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

        // ── Step 6b: Select and trigger eligible branch updates ───────────────
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
            var now6b = UtcNow();
            var lastTriggered = _lastTriggeredAt.GetValueOrDefault((repoProviderId, pr.Number), DateTimeOffset.MinValue);
            if ((now6b - lastTriggered) < TriggerCooldown)
            {
                _logger.Debug(
                    "HousekeepingService: PR #{PrNumber} is behind but was triggered {Elapsed:F0}m ago (cooldown {Cooldown:F0}m) — skipping to allow other PRs to proceed",
                    pr.Number, (now6b - lastTriggered).TotalMinutes, TriggerCooldown.TotalMinutes);
                PipelineTelemetry.HousekeepingSkipped.Add(1, repoTag);
                continue;
            }

            _lastTriggeredAt[(repoProviderId, pr.Number)] = now6b;
            inFlight.Add(pr.Number);
            PipelineTelemetry.HousekeepingTriggered.Add(1, repoTag);
            await FireAndForget(UpdateAsync(repoProvider, repoProviderId, pr.Number, repoTag));
        }

        // ── Step 7: Stale branch cleanup ──────────────────────────────────────
        if (branchCleanupEnabled)
        {
            var now = UtcNow();
            var lastCleanup = _lastCleanupAt.GetValueOrDefault(repoProviderId, DateTimeOffset.MinValue);
            var intervalElapsed = (now - lastCleanup).TotalMinutes >= cleanupIntervalMinutes;

            if (intervalElapsed)
            {
                _lastCleanupAt[repoProviderId] = now;
                await RunBranchCleanupAsync(repoProvider, issueProvider, agentDonePrs, repoTag, ct);
            }
        }
    }

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

        // Build a fast lookup of branches that have open PRs — these must never be deleted.
        var branchesWithOpenPr = new HashSet<string>(
            agentDonePrs.Select(p => p.BranchName),
            StringComparer.OrdinalIgnoreCase);

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
    /// (see <see cref="ActiveLabels"/>) or a terminal label (see <see cref="TerminalReworkBlockers"/>),
    /// in which case it returns early without modifying any labels.
    /// <c>agent:error</c> and <c>agent:needs-refinement</c> are intentional rework targets and
    /// will proceed to a swap; terminal labels (<c>agent:done</c>, <c>agent:wont-do</c>,
    /// <c>agent:cancelled</c>) must never be re-queued.
    /// </summary>
    // TODO: the stale comment above replaced a misleading one ("swaps its label to agent:next if it is
    // in a terminal state and not already active") that had the intended behaviour backwards — the method
    // now returns early for terminal states rather than proceeding. Keep this comment accurate if the
    // guard logic changes. (review-findings WARNING, HousekeepingService.cs:431)
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
                "HousekeepingService: issue {IssueId} linked to conflicted PR #{PrNumber} has a terminal label — skipping rework swap",
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
