using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Hub;

public sealed partial class AgentHub
{
    // ── Model fetch ─────────────────────────────────────────────────────

    /// <summary>
    /// Receives the result of a FetchModels request from an agent.
    /// </summary>
    public Task ReportFetchModelsResult(FetchModelsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        _consolidationOps.CompleteModelFetchRequest(response);
        return Task.CompletedTask;
    }

    // ── Consolidation ───────────────────────────────────────────────────

    /// <summary>
    /// Agent reports consolidation job completion. Updates the consolidation run status,
    /// persists harness suggestions if present, and increments badge count for refactoring issues.
    /// Delegates all business logic to <see cref="IHubConsolidationOperations"/> (T10).
    /// </summary>
    public async Task<string> ReportConsolidationComplete(ConsolidationJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);

        // Validation: reject if agent has a different active job
        if (agent is not null && agent.ActiveJobId is not null
            && !string.Equals(agent.ActiveJobId, result.JobId, StringComparison.Ordinal))
        {
            _logger.Warning(
                "ReportConsolidationComplete rejected — job {JobId} not assigned to agent {AgentId} (active: {ActiveJobId})",
                result.JobId, agent.AgentId, agent.ActiveJobId);
            return $"REJECTED: agentId={agent.AgentId}, activeJobId={agent.ActiveJobId}";
        }

        _logger.Information("Consolidation job {JobId} completed by agent {AgentId}: success={Success}",
            result.JobId, agent?.AgentId ?? "NULL", result.Success);

        // Transition agent to Idle BEFORE delegating to slow I/O
        if (agent is not null)
        {
            agent.ActiveJobId = null;
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);
        }

        // Delegate all consolidation business logic to the facade service (T10)
        return await _consolidationOps.HandleConsolidationCompleteAsync(result, agent, CancellationToken.None);
    }
}
