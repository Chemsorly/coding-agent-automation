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
        try
        {
            activeRunBranches = _runService.GetActiveRuns()
                .Where(r => r.BranchName != null)
                .Select(r => r.BranchName!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "HousekeepingService: failed to get active runs for branch exclusion; proceeding without exclusion");
            activeRunBranches = [];
        }

        // ── Step 5: Sort candidates ascending by PR number (oldest first) ────
        var sorted = agentDonePrs.OrderBy(p => p.Number).ToList();

        // ── Step 6a: Handle Conflicted PRs — swap linked issue to agent:next ─
        foreach (var pr in sorted)
        {
            if (mergeabilityMap[pr.Number] != PrMergeabilityStatus.Conflicted)
                continue;

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
    /// Fetches the issue and swaps its label to <c>agent:next</c> if it is in a terminal state
    /// and not already active.
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
