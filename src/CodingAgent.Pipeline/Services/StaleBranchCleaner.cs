using System.Collections.Concurrent;
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
        string repoProviderId,
        KeyValuePair<string, object?> repoTag,
        bool enabled,
        int cleanupIntervalMinutes,
        bool wasInputTruncated,
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
            //
            // TODO: [WARNING] The timestamp is also committed before the wasInputTruncated check
            // in RunBranchCleanupAsync fires. When input is truncated, no cleanup occurs but the
            // cadence window is still consumed. The next poll within cleanupIntervalMinutes will
            // skip cleanup silently (interval guard fires). This means the Warning log appears only
            // once per interval rather than on every cycle where truncation persists. Operators who
            // do not act after the first Warning will not see repeated Warnings until the interval
            // expires. Consistent with the pre-existing cadence trade-off above — no correctness
            // impact — but reduces persistent-truncation visibility.
            _lastCleanupAt[repoProviderId] = now;
            await RunBranchCleanupAsync(repoProvider, issueProvider, agentDonePrs,
                repoTag, wasInputTruncated, ct);
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
        bool wasInputTruncated,
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
                "StaleBranchCleaner: failed to list agent branches for cleanup: {Error}", ex.Message);
            return;
        }

        if (allAgentBranches.Count == 0)
            return;

        // If the agentDonePrs input was truncated by the ClosedLoopMaxPagesToFetch cap, open PRs
        // beyond the cap are absent from the list. Using a truncated list for branch protection
        // could cause a live PR's branch to be deleted. Skip the cleanup cycle and warn so operators
        // know to raise ClosedLoopMaxPagesToFetch.
        if (wasInputTruncated)
        {
            _logger.Warning(
                "StaleBranchCleaner: agentDonePrs input was truncated (ClosedLoopMaxPagesToFetch cap reached) — " +
                "skipping branch cleanup this cycle to avoid false-positive deletions. " +
                "Raise ClosedLoopMaxPagesToFetch to enable cleanup in repos with many open agent PRs.");
            return;
        }

        // Build the branch-protection set directly from the (non-truncated) agentDonePrs input.
        // Using a case-insensitive comparer matches Git's branch naming behaviour.
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
