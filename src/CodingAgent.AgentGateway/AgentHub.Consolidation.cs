using CodingAgent.Contracts;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.AgentGateway;

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

        // Validation: accept only when the caller is a registered agent, has an active
        // consolidation job, and the reported JobId matches that job exactly.
        // Reject when: agent is null (narrow race after disconnect), ActiveJobId is null
        // (idle agent — duplicate report or stale retry), or JobId does not match.
        if (agent is null || agent.ActiveJobId is null
            || !string.Equals(agent.ActiveJobId, result.JobId, StringComparison.Ordinal))
        {
            var activeJobId = agent?.ActiveJobId ?? "NULL";
            _logger.Warning(
                "ReportConsolidationComplete rejected — job {JobId} not active on agent {AgentId} (active: {ActiveJobId})",
                SanitizeForLog(result.JobId), agent?.AgentId ?? "NULL", activeJobId);
            return $"REJECTED: agentId={agent?.AgentId ?? "NULL"}, activeJobId={activeJobId}";
        }

        _logger.Information("Consolidation job {JobId} completed by agent {AgentId}: success={Success}",
            SanitizeForLog(result.JobId), agent.AgentId, result.Success);

        // Transition agent to Idle BEFORE delegating to slow I/O
        // (validation above guarantees agent is non-null here)
        agent.ActiveJobId = null; // local snapshot update
        _ = _facade.UpdateAgentFieldAsync(agent.AgentId, AgentFieldNames.ActiveJobId, null); // distributed write
        _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);

        // Delegate all consolidation business logic to the facade service (T10)
        return await _consolidationOps.HandleConsolidationCompleteAsync(result, agent, CancellationToken.None);
    }
}
