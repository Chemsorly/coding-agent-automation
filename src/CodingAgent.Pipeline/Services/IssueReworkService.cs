using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Implements <see cref="IIssueReworkService"/>: for each conflicted PR whose branch has no
/// active run, extracts linked issues and swaps eligible issue labels to <c>agent:next</c>
/// to trigger a rework dispatch run.
/// </summary>
public sealed class IssueReworkService : IIssueReworkService
{
    private readonly ILogger _logger;

    public IssueReworkService(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task TriggerConflictReworkAsync(
        IReadOnlyList<PullRequestSummary> sorted,
        IReadOnlyDictionary<int, PrMergeabilityStatus> mergeabilityMap,
        IReadOnlySet<string> activeRunBranches,
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
                    "IssueReworkService: PR #{PrNumber} is conflicted but branch '{Branch}' has an active run — skipping rework swap",
                    pr.Number, pr.BranchName);
                continue;
            }

            await TriggerReworkAsync(repoProvider, issueProvider, issueProviderId, pr, repoTag, ct);
        }
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
                "IssueReworkService: failed to extract linked issues for PR #{PrNumber}: {Error}",
                pr.Number, ex.Message);
            return;
        }

        if (linkedIssues.Count == 0)
        {
            _logger.Information(
                "IssueReworkService: PR #{PrNumber} is conflicted but has no linked issues — skipping rework",
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
    /// (see <see cref="AgentLabels.HousekeepingActiveLabels"/>) or an abandonment label
    /// (see <see cref="AgentLabels.HousekeepingTerminalReworkBlockers"/>),
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
                "IssueReworkService: failed to fetch issue {IssueId} linked to PR #{PrNumber}: {Error}",
                issueIdString, prNumber, ex.Message);
            return;
        }

        if (issue.Labels.Any(l => AgentLabels.HousekeepingActiveLabels.Contains(l)))
        {
            _logger.Debug(
                "IssueReworkService: issue {IssueId} linked to conflicted PR #{PrNumber} already has an active label — skipping rework swap",
                issueIdString, prNumber);
            return;
        }

        if (issue.Labels.Any(l => AgentLabels.HousekeepingTerminalReworkBlockers.Contains(l)))
        {
            _logger.Debug(
                "IssueReworkService: issue {IssueId} linked to conflicted PR #{PrNumber} has an abandonment label (agent:wont-do or agent:cancelled) — skipping rework swap",
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
                identifier: issueIdString,
                currentLabels: issue.Labels);

            PipelineTelemetry.HousekeepingConflictReworkTriggered.Add(1, repoTag);
            _logger.Information(
                "IssueReworkService: re-queued issue {IssueId} for rework due to merge conflict on PR #{PrNumber} (issueProvider: {IssueProviderId})",
                issueIdString, prNumber, issueProviderId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "IssueReworkService: failed to swap label on issue {IssueId} linked to PR #{PrNumber}: {Error}",
                issueIdString, prNumber, ex.Message);
        }
    }
}
