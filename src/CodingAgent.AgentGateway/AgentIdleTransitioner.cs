using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Handles the agent-idle state transition after a job completes.
/// Extracted from <see cref="AgentJobLifecycleService.HandleJobCompletedAsync"/> to keep
/// that method a thin dispatcher. Covers two sub-paths:
/// <list type="bullet">
///   <item><description>Agent not null: clears in-memory fields and fires three fire-and-forget Redis updates.</description></item>
///   <item><description>Agent null but run has an AgentId: best-effort fallback to prevent the agent staying Busy indefinitely.</description></item>
/// </list>
/// This class does NOT handle the job-rejection path — that remains in
/// <see cref="AgentJobLifecycleService.ResetAgentToIdle"/>.
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
    /// Transitions the agent to <see cref="AgentStatus.Idle"/> after a job completes.
    /// Safe to call with null <paramref name="agent"/> or null <paramref name="run"/> — no-ops when
    /// neither source can provide an <see cref="AgentId"/>.
    /// </summary>
    internal void TransitionToIdle(AgentEntry? agent, PipelineRun? run, string jobIdValue)
    {
        if (agent is not null)
        {
            // Completion path off-lock null-clear (category b — see LayerBoundaryTests.ActiveJobIdBareWriteExemptions).
            agent.ActiveJobId = null;
            agent.OrphanRestoredAt = null;
            // TODO: [WARNING] DateTimeOffset.UtcNow is captured twice: once for the in-memory field
            // below and once for the Redis fire-and-forget write two lines further down. A context
            // switch between the two calls can produce different timestamps, so the in-memory snapshot
            // and the persisted Redis field may diverge by microseconds. Capture UtcNow once into a
            // local variable and use it for both assignments to guarantee they are identical.
            // (DotNetSpecialist review finding — same latent pattern exists in ResetAgentToIdle.)
            agent.LastJobCompletedAt = DateTimeOffset.UtcNow;
            _facade.UpdateAgentFieldFireAndForget(agent.AgentId, AgentFieldNames.ActiveJobId, null, _logger, "HandleJobCompletedAsync");
            _facade.UpdateAgentFieldFireAndForget(agent.AgentId, AgentFieldNames.OrphanRestoredAt, null, _logger, "HandleJobCompletedAsync");
            _facade.UpdateAgentFieldFireAndForget(agent.AgentId, AgentFieldNames.LastJobCompletedAt, DateTimeOffset.UtcNow.ToString("O"), _logger, "HandleJobCompletedAsync");
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);
        }
        else if (run?.AgentId is not null)
        {
            // Fallback: agent lookup returned null (connection dropped, hash expired) but we know the
            // AgentId from the run. Attempt to clear agent state to prevent it from being locked in
            // Busy indefinitely until ReconciliationService.EnforceTimeoutsAsync times it out.
            var agentId = new AgentId(run.AgentId);
            _facade.UpdateAgentFieldFireAndForget(agentId, AgentFieldNames.ActiveJobId, null, _logger, "HandleJobCompletedAsync (run fallback path)");
            _facade.TransitionStatus(agentId, AgentStatus.Idle);
            _logger.Warning(
                "HandleJobCompletedAsync: agent lookup returned null for job {JobId} (agentId={AgentId}) — clearing state via run fallback to prevent Busy lock",
                jobIdValue, run.AgentId);
        }
    }
}
