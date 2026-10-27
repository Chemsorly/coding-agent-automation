using CodingAgent.Contracts;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Handles the agent-idle state transition after a job completes or is rejected.
/// Extracted from <see cref="AgentJobLifecycleService.HandleJobCompletedAsync"/> to keep
/// that method a thin dispatcher. Covers two sub-paths:
/// <list type="bullet">
///   <item><description>Agent not null: clears in-memory fields and fires three fire-and-forget Redis updates.</description></item>
///   <item><description>Agent null but run has an AgentId: best-effort fallback to prevent the agent staying Busy indefinitely.</description></item>
/// </list>
/// Handles both the job-completion path (called from
/// <see cref="AgentJobLifecycleService.HandleJobCompletedAsync"/>) and the job-rejection path
/// (called from <see cref="AgentJobLifecycleService"/> via <c>ResetAgentToIdle</c>).
/// </summary>
internal sealed class AgentIdleTransitioner
{
    private readonly IAgentHubFacade _facade;
    private readonly ILogger _logger;

    internal AgentIdleTransitioner(IAgentHubFacade facade, ILogger logger)
    {
        _facade = facade;
        _logger = logger;
    }

    /// <summary>
    /// Transitions the agent to <see cref="AgentStatus.Idle"/> after a job completes or is rejected.
    /// Safe to call with null <paramref name="agent"/> or null <paramref name="run"/> — no-ops when
    /// neither source can provide an <see cref="AgentId"/>.
    /// </summary>
    /// <param name="callerContext">Short context string included in fire-and-forget log messages (e.g. "HandleJobCompletedAsync" or "ResetAgentToIdle").</param>
    internal void TransitionToIdle(AgentEntry? agent, PipelineRun? run, string jobIdValue, string callerContext)
    {
        if (agent is not null)
        {
            // Completion/rejection path off-lock null-clear (category b — see LayerBoundaryTests.ActiveJobIdBareWriteExemptions).
            agent.ActiveJobId = null;
            agent.OrphanRestoredAt = null;
            // Capture UtcNow once so the in-memory field and the Redis write use the identical timestamp.
            var now = DateTimeOffset.UtcNow;
            agent.LastJobCompletedAt = now;
            _facade.UpdateAgentFieldFireAndForget(agent.AgentId, AgentFieldNames.ActiveJobId, null, _logger, callerContext);
            _facade.UpdateAgentFieldFireAndForget(agent.AgentId, AgentFieldNames.OrphanRestoredAt, null, _logger, callerContext);
            _facade.UpdateAgentFieldFireAndForget(agent.AgentId, AgentFieldNames.LastJobCompletedAt, now.ToString("O"), _logger, callerContext);
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);
        }
        else if (run?.AgentId is not null)
        {
            // Fallback: agent lookup returned null (connection dropped, hash expired) but we know the
            // AgentId from the run. Attempt to clear agent state to prevent it from being locked in
            // Busy indefinitely until ReconciliationService.EnforceTimeoutsAsync times it out.
            var agentId = new AgentId(run.AgentId);
            _facade.UpdateAgentFieldFireAndForget(agentId, AgentFieldNames.ActiveJobId, null, _logger, $"{callerContext} (run fallback path)");
            _facade.TransitionStatus(agentId, AgentStatus.Idle);
            _logger.Warning(
                "[{CallerContext}] agent lookup returned null for job {JobId} (agentId={AgentId}) — clearing state via run fallback to prevent Busy lock",
                callerContext, jobIdValue, LogSanitizer.SanitizeForLog(run.AgentId));
        }
    }
}
