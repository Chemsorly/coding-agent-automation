using CodingAgent.Infrastructure.Common;
using CodingAgent.Orchestration.Health;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

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
    /// When Redis is configured and this replica is not the one waiting for the request,
    /// the result is stored in Redis for the waiting replica to poll.
    /// </summary>
    Task CompleteModelFetchRequestAsync(FetchModelsResponse response);

    /// <summary>
    /// Handles consolidation job completion: updates run status, persists harness
    /// suggestions, increments badge count, writes pipeline run history via
    /// <see cref="IRunLifecycleManager"/>, and notifies change listeners.
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
    private readonly IRunLifecycleManager _lifecycleManager;
    private readonly IOrchestratorRunService _runService;
    private readonly ILogger _logger;

    public HubConsolidationOperations(
        ModelFetchService modelFetchService,
        IConsolidationService consolidationService,
        ConsolidationBadgeService badgeService,
        IChangeNotifier changeNotifier,
        IRunLifecycleManager lifecycleManager,
        IOrchestratorRunService runService,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(modelFetchService);
        ArgumentNullException.ThrowIfNull(consolidationService);
        ArgumentNullException.ThrowIfNull(badgeService);
        ArgumentNullException.ThrowIfNull(changeNotifier);
        ArgumentNullException.ThrowIfNull(lifecycleManager);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(logger);

        _modelFetchService = modelFetchService;
        _consolidationService = consolidationService;
        _badgeService = badgeService;
        _changeNotifier = changeNotifier;
        _lifecycleManager = lifecycleManager;
        _runService = runService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CompleteModelFetchRequestAsync(FetchModelsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        await _modelFetchService.CompleteRequestAsync(response);
    }

    /// <inheritdoc />
    public async Task<string> HandleConsolidationCompleteAsync(
        ConsolidationJobResult result,
        AgentEntry? agent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        // result.JobId has already been validated by the hub: the caller must be a registered
        // agent with an active consolidation job whose JobId matches result.JobId.
        var sanitizedJobId = LogSanitizer.SanitizeForLog(result.JobId);
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

        // Route through RunLifecycleManager to write pipeline run history and transition WorkItem.
        // RunLifecycleManager.CompleteRunAsync/FailRunAsync will internally call RemoveRun (which is
        // a no-op if the ghost run is not in memory) and AddRunToHistoryAsync.
        // RunLifecycleManager already skips the label swap for consolidation runs via the
        // IssueProviderConfigId == ConsolidationConstants.ProviderConfigId guard in CompleteRunAsync.
        try
        {
            var runId = new RunId(result.JobId);

            // Fix [CRITICAL]: Set ConsolidationResultSummary on the in-memory PipelineRun BEFORE
            // RunLifecycleManager.RemoveRun claims it. RemoveRun is called inside CompleteRunAsync /
            // FailRunAsync; after RemoveRun the run instance still exists but is no longer in the
            // store — AddRunToHistoryAsync is called on the same instance immediately after, so the
            // summary written here is serialised into PipelineRunSummary.ConsolidationResultSummary
            // and persisted. Without this step the field is null and the history row renders "—".
            // (review-findings-correctness.md CRITICAL:L299, L412)
            var inMemoryRun = _runService.GetRun(runId);
            if (inMemoryRun is not null)
            {
                if (result.Success)
                    inMemoryRun.ConsolidationResultSummary = result.Summary;
                else
                    // Map the error message to ConsolidationResultSummary so it surfaces in the
                    // history row's Summary column. FailureReason is also set by FailRunCoreAsync,
                    // but that field is not rendered in the Consolidation history row markup.
                    inMemoryRun.ConsolidationResultSummary = result.ErrorMessage;

                // Write the change back: with Redis, GetRun returns a copy, and RemoveRun inside
                // CompleteRunAsync/FailRunAsync reads the stored run again.
                _runService.ReplaceRun(inMemoryRun);
            }
            else
            {
                _logger.Debug(
                    "HubConsolidationOperations: in-memory run {JobId} not found — ConsolidationResultSummary not set (run may have been removed by another path)",
                    result.JobId);
            }

            if (result.Success)
            {
                await _lifecycleManager.CompleteRunAsync(runId, WorkItemStatus.Succeeded, ct);
                _logger.Information("Consolidation run {JobId} CompleteRunAsync completed", result.JobId);
            }
            else
            {
                var errorMsg = result.ErrorMessage ?? "Consolidation run failed";
                await _lifecycleManager.FailRunAsync(runId, errorMsg, ct);
                _logger.Information("Consolidation run {JobId} FailRunAsync completed", result.JobId);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to route consolidation run {JobId} through RunLifecycleManager (non-fatal)", result.JobId);
        }

        if (result.HarnessSuggestions is not null)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await _consolidationService.SaveHarnessSuggestionsAsync(result.HarnessSuggestions, ct);
                _logger.Information("Consolidation run {JobId} SaveHarnessSuggestionsAsync completed in {ElapsedMs}ms", sanitizedJobId, sw.ElapsedMilliseconds);
                _badgeService.IncrementBy(result.HarnessSuggestions.Suggestions.Count);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to persist harness suggestions for consolidation job {JobId}", sanitizedJobId);
            }
        }

        if (result.CreatedIssues is { Count: > 0 })
        {
            _badgeService.IncrementBy(result.CreatedIssues.Count);
            _logger.Information("Refactoring consolidation job {JobId} created {Count} issue(s)",
                sanitizedJobId, result.CreatedIssues.Count);
        }

        return debugInfo;
    }
}
