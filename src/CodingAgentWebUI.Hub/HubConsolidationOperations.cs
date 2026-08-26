using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hub;

/// <summary>
/// Facade for consolidation-related hub operations: model fetch result handling
/// and consolidation job completion processing.
///
/// Extracts the consolidation cluster from <see cref="AgentHub"/> so that
/// <see cref="AgentHubDependencies"/> shrinks from 13 to 10 members and the
/// three consolidation-only services (<see cref="ModelFetchService"/>,
/// <see cref="IConsolidationService"/>, <see cref="ConsolidationBadgeService"/>)
/// are no longer injected directly into the hub.
///
/// T10 (arch-audit 2026-08-22).
/// </summary>
public interface IHubConsolidationOperations
{
    /// <summary>
    /// Completes a pending model fetch request by delivering the response
    /// to the waiting <see cref="ModelFetchService"/> continuation.
    /// </summary>
    void CompleteModelFetchRequest(FetchModelsResponse response);

    /// <summary>
    /// Handles consolidation job completion: updates run status, persists harness
    /// suggestions, increments badge count, and notifies change listeners.
    /// Returns a debug info string for E2E test observability.
    /// </summary>
    Task<string> HandleConsolidationCompleteAsync(
        ConsolidationJobResult result,
        AgentEntry? agent,
        CancellationToken ct = default);
}

/// <summary>
/// Default implementation of <see cref="IHubConsolidationOperations"/>.
/// </summary>
internal sealed class HubConsolidationOperations : IHubConsolidationOperations
{
    private readonly ModelFetchService _modelFetchService;
    private readonly IConsolidationService _consolidationService;
    private readonly ConsolidationBadgeService _badgeService;
    private readonly IChangeNotifier _changeNotifier;
    private readonly ILogger _logger;

    public HubConsolidationOperations(
        ModelFetchService modelFetchService,
        IConsolidationService consolidationService,
        ConsolidationBadgeService badgeService,
        IChangeNotifier changeNotifier,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(modelFetchService);
        ArgumentNullException.ThrowIfNull(consolidationService);
        ArgumentNullException.ThrowIfNull(badgeService);
        ArgumentNullException.ThrowIfNull(changeNotifier);
        ArgumentNullException.ThrowIfNull(logger);

        _modelFetchService = modelFetchService;
        _consolidationService = consolidationService;
        _badgeService = badgeService;
        _changeNotifier = changeNotifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public void CompleteModelFetchRequest(FetchModelsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        _modelFetchService.CompleteRequest(response);
    }

    /// <inheritdoc />
    public async Task<string> HandleConsolidationCompleteAsync(
        ConsolidationJobResult result,
        AgentEntry? agent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var debugInfo = $"agentFound={agent is not null}, agentId={agent?.AgentId ?? "NULL"}, activeJobId={agent?.ActiveJobId ?? "NULL"}";
        _logger.Debug("HubConsolidationOperations.HandleConsolidationComplete ENTRY: {DebugInfo}", debugInfo);

        // Transition agent to Idle BEFORE slow I/O operations
        if (agent is not null)
        {
            agent.ActiveJobId = null;
            // Note: The distributed write (_facade.UpdateAgentFieldAsync) is performed by the
            // calling hub method (AgentHub.Consolidation.cs) before delegating here.
            // The facade's TransitionStatus is called by the Hub before delegating here.
        }

        _changeNotifier.NotifyChange();

        var totalTokens = SumTokenUsage(result.ReviewTokenUsage, result.RefinementTokenUsage, result.DiffSummaryTokenUsage);

        // Update the consolidation run status
        try
        {
            var status = result.Success
                ? Pipeline.Models.ConsolidationRunStatus.Succeeded
                : Pipeline.Models.ConsolidationRunStatus.Failed;
            var summary = result.Success ? result.Summary : result.ErrorMessage;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _consolidationService.UpdateRunAsync(result.JobId, status, summary, ct, totalTokens);
            _logger.Information("Consolidation run {JobId} UpdateRunAsync completed in {ElapsedMs}ms", result.JobId, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update consolidation run {JobId} status", result.JobId);
        }

        if (result.HarnessSuggestions is not null)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await _consolidationService.SaveHarnessSuggestionsAsync(result.HarnessSuggestions, ct);
                _logger.Information("Consolidation run {JobId} SaveHarnessSuggestionsAsync completed in {ElapsedMs}ms", result.JobId, sw.ElapsedMilliseconds);
                _badgeService.IncrementBy(result.HarnessSuggestions.Suggestions.Count);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to persist harness suggestions for consolidation job {JobId}", result.JobId);
            }
        }

        if (result.CreatedIssues is { Count: > 0 })
        {
            _badgeService.IncrementBy(result.CreatedIssues.Count);
            _logger.Information("Refactoring consolidation job {JobId} created {Count} issue(s)",
                result.JobId, result.CreatedIssues.Count);
        }

        return debugInfo;
    }

    private static long SumTokenUsage(params TokenUsage?[] usages)
        => usages.Where(u => u is not null).Sum(u => u!.TotalTokens);
}
