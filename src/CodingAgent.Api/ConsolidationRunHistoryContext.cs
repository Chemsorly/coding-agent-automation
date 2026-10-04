using System.Text.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api;

/// <summary>
/// Reads what a consolidation run needs from the run history at delivery time: when the last run of the
/// same type and scope succeeded (brain consolidation focuses on what changed since), and for harness
/// suggestions the run feedback collected since their last successful run.
/// </summary>
/// <remarks>
/// A consolidation run's history entry has the work item's <c>{type}:{scope}</c> key as its issue identifier
/// (see <c>PipelineRunFactory.CreateFromWorkItem</c>), so matching on it gives the same scope as the
/// trigger's duplicate check: one brain, one template, or "global".
/// </remarks>
internal static class ConsolidationRunHistoryContext
{
    /// <summary>How many of the newest successful consolidation runs are searched for the scope's last success.</summary>
    internal const int ConsolidationHistoryPageSize = 100;

    /// <summary>The most feedback entries handed to a harness suggestion run (newest first).</summary>
    internal const int MaxFeedbackEntries = 200;

    /// <summary>
    /// Returns when the last consolidation with the key <paramref name="scopeKey"/> succeeded, or null when
    /// none of the newest <see cref="ConsolidationHistoryPageSize"/> successful consolidations has it.
    /// </summary>
    public static async Task<DateTimeOffset?> GetLastSuccessfulRunAsync(
        IPipelineRunHistoryService history, IssueIdentifier scopeKey, CancellationToken ct)
    {
        var runs = await history.GetRunHistoryAsync(
            1, ConsolidationHistoryPageSize, feedbackOnly: false, finalStep: PipelineStep.Completed,
            projectId: null, since: null, runType: PipelineRunType.Consolidation, ct);

        return runs.Items
            .Where(r => r.IssueIdentifier == scopeKey)
            .Max(r => r.CompletedAtOffset);
    }

    /// <summary>
    /// Serializes the feedback of the runs started since <paramref name="since"/> (all runs when null),
    /// at most <see cref="MaxFeedbackEntries"/>, newest first. Returns null when there is none.
    /// </summary>
    public static async Task<string?> BuildFeedbackDataJsonAsync(
        IPipelineRunHistoryService history, DateTimeOffset? since, CancellationToken ct)
    {
        var runs = await history.GetRunHistoryAsync(
            1, MaxFeedbackEntries, feedbackOnly: true, finalStep: null,
            projectId: null, since: since, runType: null, ct);

        var feedback = runs.Items
            .Where(r => r.Feedback is not null)
            .Select(r => r.Feedback!)
            .ToList();

        return feedback.Count == 0 ? null : JsonSerializer.Serialize(feedback, PipelineJsonOptions.Default);
    }
}
