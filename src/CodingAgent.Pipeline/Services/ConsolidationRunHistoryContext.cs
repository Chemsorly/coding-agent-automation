using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Reads what a consolidation run needs from the run history at delivery time: when the last run of the
/// same type and scope succeeded (brain consolidation focuses on what changed since).
/// </summary>
/// <remarks>
/// A consolidation run's history entry has the work item's <c>{type}:{scope}</c> key as its issue identifier
/// (see <c>PipelineRunFactory.CreateFromWorkItem</c>), so matching on it gives the same scope as the
/// trigger's duplicate check: one brain, one template, or "global".
/// </remarks>
public static class ConsolidationRunHistoryContext
{
    /// <summary>How many of the newest successful consolidation runs are searched for the scope's last success.</summary>
    public const int ConsolidationHistoryPageSize = 100;

    /// <summary>
    /// Returns when the last consolidation with the key <paramref name="scopeKey"/> succeeded, or null when
    /// none of the newest <see cref="ConsolidationHistoryPageSize"/> successful consolidations has it.
    /// </summary>
    public static async Task<DateTimeOffset?> GetLastSuccessfulRunAsync(
        IPipelineRunHistoryService history, IssueIdentifier scopeKey, CancellationToken ct)
    {
        var runs = await history.GetRunHistoryAsync(
            new RunHistoryQuery(Page: 1, PageSize: ConsolidationHistoryPageSize, FinalStep: PipelineStep.Completed,
                RunType: PipelineRunType.Consolidation),
            ct);

        return runs.Items
            .Where(r => r.IssueIdentifier == scopeKey)
            .Max(r => r.CompletedAtOffset);
    }
}
