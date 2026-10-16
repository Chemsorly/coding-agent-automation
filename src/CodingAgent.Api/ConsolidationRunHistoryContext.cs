using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Api;

/// <summary>
/// Delegates to <see cref="CodingAgent.Pipeline.Services.ConsolidationRunHistoryContext"/>.
/// Kept for backward compatibility; the canonical class now lives in the Pipeline project.
/// </summary>
internal static class ConsolidationRunHistoryContext
{
    /// <inheritdoc cref="CodingAgent.Pipeline.Services.ConsolidationRunHistoryContext.ConsolidationHistoryPageSize"/>
    internal const int ConsolidationHistoryPageSize =
        CodingAgent.Pipeline.Services.ConsolidationRunHistoryContext.ConsolidationHistoryPageSize;

    /// <inheritdoc cref="CodingAgent.Pipeline.Services.ConsolidationRunHistoryContext.MaxFeedbackEntries"/>
    internal const int MaxFeedbackEntries =
        CodingAgent.Pipeline.Services.ConsolidationRunHistoryContext.MaxFeedbackEntries;

    /// <inheritdoc cref="CodingAgent.Pipeline.Services.ConsolidationRunHistoryContext.GetLastSuccessfulRunAsync"/>
    public static Task<DateTimeOffset?> GetLastSuccessfulRunAsync(
        IPipelineRunHistoryService history, IssueIdentifier scopeKey, CancellationToken ct) =>
        CodingAgent.Pipeline.Services.ConsolidationRunHistoryContext.GetLastSuccessfulRunAsync(history, scopeKey, ct);

    /// <inheritdoc cref="CodingAgent.Pipeline.Services.ConsolidationRunHistoryContext.BuildFeedbackDataJsonAsync"/>
    public static Task<string?> BuildFeedbackDataJsonAsync(
        IPipelineRunHistoryService history, DateTimeOffset? since, CancellationToken ct) =>
        CodingAgent.Pipeline.Services.ConsolidationRunHistoryContext.BuildFeedbackDataJsonAsync(history, since, ct);
}
