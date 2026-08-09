using System.Collections.Concurrent;
using System.Text.Json;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Manages feedback data caching for consolidation runs.
/// Prepares, stores, retrieves, and clears feedback data used during harness suggestion dispatch.
/// </summary>
public sealed class ConsolidationFeedbackCache : IConsolidationFeedbackCache
{
    private readonly ILogger _logger;
    private readonly IConsolidationRunStore _runStore;
    private readonly IPipelineRunHistoryService _runHistoryService;
    private readonly ConcurrentDictionary<string, string> _feedbackDataCache = new();

    public ConsolidationFeedbackCache(
        ILogger logger,
        IConsolidationRunStore runStore,
        IPipelineRunHistoryService runHistoryService)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(runHistoryService);

        _logger = logger;
        _runStore = runStore;
        _runHistoryService = runHistoryService;
    }

    /// <inheritdoc />
    public async Task PrepareFeedbackDataAsync(ConsolidationRun run, CancellationToken ct)
    {
        try
        {
            var sinceUtc = await GetLastSuccessfulHarnessRunTimestampAsync(ct);

            var allRuns = await _runHistoryService.GetRunHistoryAsync(ct);
            var feedbackEntries = allRuns
                .Where(r => r.Feedback is not null && r.StartedAtOffset > sinceUtc)
                .Select(r => r.Feedback!)
                .ToList();

            if (feedbackEntries.Count == 0)
            {
                _logger.Information("No new RunFeedback entries found since {SinceUtc} for harness suggestions", sinceUtc);
                return;
            }

            var feedbackJson = JsonSerializer.Serialize(feedbackEntries, PipelineJsonOptions.Default);
            _logger.Information(
                "Prepared {Count} RunFeedback entries (since {SinceUtc}) for harness suggestion analysis",
                feedbackEntries.Count, sinceUtc);

            // Store feedback data — will be used when building ConsolidationJobMessage for dispatch
            _feedbackDataCache[run.RunId] = feedbackJson;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to prepare feedback data for harness suggestions");
        }
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset> GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken ct = default)
    {
        var allRuns = await _runStore.LoadAllRunsAsync(ct);

        var latestRun = allRuns
            .Where(r => r.Type == ConsolidationRunType.HarnessSuggestions
                     && r.Status == ConsolidationRunStatus.Succeeded
                     && r.CompletedAtUtc.HasValue)
            .MaxBy(r => r.CompletedAtUtc!.Value);

        return latestRun?.CompletedAtUtc ?? DateTimeOffset.MinValue;
    }

    /// <inheritdoc />
    public string? GetFeedbackDataForRun(RunId runId)
    {
        _feedbackDataCache.TryGetValue(runId.Value, out var data);
        return data;
    }

    /// <inheritdoc />
    public void ClearFeedbackDataForRun(RunId runId)
    {
        _feedbackDataCache.TryRemove(runId.Value, out _);
    }
}
