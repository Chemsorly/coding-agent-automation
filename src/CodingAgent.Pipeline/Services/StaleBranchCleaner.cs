using System.Collections.Concurrent;
using System.Diagnostics;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Implements <see cref="IStaleBranchCleaner"/>: lists all agent branches, skips those with
/// an open PR or an active issue label, and deletes the rest.
/// Maintains per-repository cadence state so that the cleanup pass only runs when the
/// configured interval has elapsed.
/// </summary>
public sealed class StaleBranchCleaner : IStaleBranchCleaner
{
    private readonly ILogger _logger;

    /// <summary>
    /// Overridable time source for the cleanup interval guard.
    /// In tests: replace with a lambda that returns a controlled time.
    /// </summary>
    // TODO: Two independent UtcNow seams now exist: one here and one on HousekeepingService.
    // If both are replaced in a test but diverge, the 'now' captured in ExecuteAsync (used for
    // step-3 eviction and step-5 ordering) will differ from the 'now' used inside StaleBranchCleaner,
    // making timestamp-sensitive tests harder to reason about. The two-seam design is intentional
    // (StaleBranchCleaner runs on an independent cadence), but tests must set both seams consistently.
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Tracks when the last stale-branch cleanup pass ran per repository,
    /// so we don't call <c>ListAgentBranchesAsync</c> on every tick.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCleanupAt = new();

    public StaleBranchCleaner(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RunIfDueAsync(
        IRepositoryProvider repoProvider,
        IIssueProvider issueProvider,
        IReadOnlyList<PullRequestSummary> agentDonePrs,
        bool wasInputTruncated,
        string repoProviderId,
        KeyValuePair<string, object?> repoTag,
        bool enabled,
        int cleanupIntervalMinutes,
        CancellationToken ct)
    {
        if (!enabled)
            return;

        var now = UtcNow();
        var lastCleanup = _lastCleanupAt.GetValueOrDefault(repoProviderId, DateTimeOffset.MinValue);
        var intervalElapsed = (now - lastCleanup).TotalMinutes >= cleanupIntervalMinutes;

        if (intervalElapsed)
        {
            // TODO: The timestamp is committed before RunBranchCleanupAsync completes. If the
            // cleanup is cancelled mid-way (via ct), the next call will treat the pass as
            // completed even though it did not finish. This matches the pre-extraction behaviour
            // in HousekeepingService.RunStaleBranchCleanupIfDueAsync and is therefore a
            // pre-existing trade-off, not a regression. To fix, commit the timestamp only after
            // RunBranchCleanupAsync returns successfully.
            // TODO [WARNING]: When wasInputTruncated=true, RunBranchCleanupAsync returns
            // immediately after the truncation guard (no work is done), but _lastCleanupAt is
            // already stamped here — advancing the cadence clock even though cleanup was skipped.
            // An operator who raises ClosedLoopMaxPagesToFetch to fix the truncation will still
            // have to wait a full HousekeepingBranchCleanupIntervalMinutes before cleanup runs.
            // Fix: stamp _lastCleanupAt only after a non-skipped cleanup completes, or at minimum
            // after the truncation guard inside RunBranchCleanupAsync passes.
            _lastCleanupAt[repoProviderId] = now;
            await RunBranchCleanupAsync(repoProvider, issueProvider, agentDonePrs, wasInputTruncated, repoTag, ct);
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
        bool wasInputTruncated,
        KeyValuePair<string, object?> repoTag,
        CancellationToken ct)
    {
        // Safety guard: if the PR input was truncated (capped by ClosedLoopMaxPagesToFetch),
        // skip this cleanup cycle entirely. A truncated input cannot reliably protect all
        // branches — PRs beyond the pagination cap would be absent, and their branches could
        // be incorrectly deleted. Operators who need branch cleanup to run must raise
        // ClosedLoopMaxPagesToFetch so that the full set of open PRs is fetched each cycle.
        if (wasInputTruncated)
        {
            _logger.Warning(
                "StaleBranchCleaner: agentDonePrs input was truncated (ClosedLoopMaxPagesToFetch cap reached); " +
                "skipping branch cleanup this cycle to avoid deleting branches whose PRs are beyond the pagination limit. " +
                "Raise ClosedLoopMaxPagesToFetch to enable cleanup in repos with many open agent PRs.");
            return;
        }

        IReadOnlyList<string> allAgentBranches;
        try
        {
            allAgentBranches = await repoProvider.ListAgentBranchesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "StaleBranchCleaner: failed to list agent branches for cleanup: {Error}", ex.Message);
            return;
        }

        if (allAgentBranches.Count == 0)
            return;

        // Build the set of branches that have open PRs from the (already-fetched, non-truncated)
        // agentDonePrs input. This is the sole branch-protection guard — no independent API scan
        // is needed because wasInputTruncated was checked above.
        var branchesWithOpenPr = new HashSet<string>(
            agentDonePrs.Select(pr => pr.BranchName),
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
                    "StaleBranchCleaner: cannot extract issue ID from branch {BranchName} — skipping",
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
                    "StaleBranchCleaner: failed to fetch issue {IssueId} for branch {BranchName} cleanup — skipping: {Error}",
                    issueId, branchName, ex.Message);
                continue;
            }

            if (issue.Labels.Any(l => AgentLabels.HousekeepingActiveLabels.Contains(l)))
            {
                _logger.Debug(
                    "StaleBranchCleaner: issue {IssueId} for branch {BranchName} has active label — skipping cleanup",
                    issueId, branchName);
                continue;
            }

            // Safe to delete
            try
            {
                // Emit Housekeeping.BranchDelete span for each branch actually deleted.
                // Only fires when the branch passes all guards and deletion is attempted.
                // TODO: The span is currently closed before DeleteBranchAsync is awaited (using block ends
                // after the two SetTag calls). The span records ~0 duration and cannot reflect a deletion
                // failure. Fix: move DeleteBranchAsync inside the using block, or switch to `using var`
                // statement form so the span stays alive until the end of the try block.
                using (var deleteActivity = PipelineTelemetry.ActivitySource.StartActivity("Housekeeping.BranchDelete"))
                {
                    deleteActivity?.SetTag("branch_name", branchName);
                    deleteActivity?.SetTag("issue_id", issueId);
                }
                await repoProvider.DeleteBranchAsync(branchName, ct);
                PipelineTelemetry.HousekeepingBranchDeleted.Add(1, repoTag);
                _logger.Information(
                    "StaleBranchCleaner: deleted stale branch {BranchName} (issue {IssueId})",
                    branchName, issueId);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex,
                    "StaleBranchCleaner: failed to delete branch {BranchName}: {Error}",
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
}
