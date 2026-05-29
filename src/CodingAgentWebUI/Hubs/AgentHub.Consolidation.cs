using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Hubs;

public sealed partial class AgentHub
{
    // ── Model fetch ─────────────────────────────────────────────────────

    /// <summary>
    /// Receives the result of a FetchModels request from an agent.
    /// </summary>
    public Task ReportFetchModelsResult(FetchModelsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        _modelFetchService.CompleteRequest(response);
        return Task.CompletedTask;
    }

    // ── Consolidation ───────────────────────────────────────────────────

    /// <summary>
    /// Agent reports consolidation job completion. Updates the consolidation run status,
    /// persists harness suggestions if present, and increments badge count for refactoring issues.
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportConsolidationComplete(ConsolidationJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        _logger.Information("Consolidation job {JobId} completed by agent {AgentId}: success={Success}",
            result.JobId, agent?.AgentId, result.Success);

        // Sum token usage from review, refinement, and diff summary calls
        var totalTokens = SumTokenUsage(result.ReviewTokenUsage, result.RefinementTokenUsage, result.DiffSummaryTokenUsage);

        // Update the consolidation run status
        try
        {
            var status = result.Success
                ? Pipeline.Models.ConsolidationRunStatus.Succeeded
                : Pipeline.Models.ConsolidationRunStatus.Failed;
            var summary = result.Success ? result.Summary : result.ErrorMessage;

            // WARNING 9: CancellationToken.None is intentional here — these are fast file I/O
            // operations that should complete even if the agent connection drops. The consolidation
            // run state must be persisted regardless of connection lifecycle.
            await _consolidationService.UpdateRunAsync(result.JobId, status, summary, CancellationToken.None, totalTokens);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update consolidation run {JobId} status", result.JobId);
        }

        // For harness suggestions: persist the suggestions file
        if (result.HarnessSuggestions is not null)
        {
            try
            {
                // CancellationToken.None: same rationale as above — suggestions must be persisted
                await _consolidationService.SaveHarnessSuggestionsAsync(result.HarnessSuggestions, CancellationToken.None);
                _logger.Information("Persisted harness suggestions from consolidation job {JobId}", result.JobId);

                // Increment badge count for harness suggestions
                _badgeService.IncrementBy(result.HarnessSuggestions.Suggestions.Count);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to persist harness suggestions for consolidation job {JobId}", result.JobId);
            }
        }

        // For refactoring: increment badge count for created issues
        if (result.CreatedIssues is { Count: > 0 })
        {
            _badgeService.IncrementBy(result.CreatedIssues.Count);
            _logger.Information("Refactoring consolidation job {JobId} created {Count} issue(s)",
                result.JobId, result.CreatedIssues.Count);
        }

        // Transition agent back to Idle
        if (agent is not null)
        {
            agent.ActiveJobId = null;
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);
            _facade.Signal();
        }

        _orchestration.NotifyChange();
    }

    // ── Consolidation-local private helpers ─────────────────────────────

    /// <summary>
    /// Sums the TotalTokens (InputTokens + OutputTokens + ReasoningTokens) from the provided
    /// token usage records. Null records are treated as zero.
    /// </summary>
    private static long SumTokenUsage(params Pipeline.Models.TokenUsage?[] usages)
    {
        long total = 0;
        foreach (var usage in usages)
        {
            if (usage is not null)
                total += usage.TotalTokens;
        }
        return total;
    }
}
