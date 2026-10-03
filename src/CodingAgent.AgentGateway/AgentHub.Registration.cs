using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgent.AgentGateway;

public sealed partial class AgentHub
{
    // ── Registration ────────────────────────────────────────────────────

    /// <summary>
    /// Registers an agent in the registry. Validates that the <c>agentId</c> in the message
    /// matches the <c>agentId</c> query parameter from the connection and the authenticated identity.
    /// </summary>
    public async Task RegisterAgent(AgentRegistrationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var queryAgentId = Context.GetHttpContext()?.Request.Query["agentId"].ToString();
        if (!string.Equals(message.AgentId.Value, queryAgentId, StringComparison.Ordinal))
        {
            // TODO: SanitizeForLog returns "" when message.AgentId.Value is null, whereas the old code
            // logged the AgentId struct directly (falling back to ToString()). If AgentId.Value is null
            // the log entry will show an empty string instead of a meaningful identifier. Callers should
            // ensure AgentId.Value is never null before reaching this point, or SanitizeForLog should
            // preserve a null/empty indicator rather than silently collapsing it to "".
            // HubException messages are sanitized too: SignalR's dispatcher logs every failed hub
            // invocation at Error with the exception attached, so the message reaches server logs.
            _logger.Warning(
                "RegisterAgent rejected — message agentId '{MessageAgentId}' does not match query param '{QueryAgentId}'",
                SanitizeForLog(message.AgentId.Value), SanitizeForLog(queryAgentId));
            throw new HubException($"AgentId mismatch: message has '{SanitizeForLog(message.AgentId.Value)}' but connection has '{SanitizeForLog(queryAgentId)}'");
        }

        // Defense-in-depth: validate authenticated identity matches registration
        var authenticatedAgentId = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(authenticatedAgentId) && authenticatedAgentId != "agent" &&
            !string.Equals(message.AgentId.Value, authenticatedAgentId, StringComparison.Ordinal))
        {
            _logger.Warning(
                "RegisterAgent rejected — authenticated as '{AuthenticatedAgentId}' but registering as '{MessageAgentId}'",
                SanitizeForLog(authenticatedAgentId), SanitizeForLog(message.AgentId.Value));
            throw new HubException($"AgentId mismatch: authenticated as '{authenticatedAgentId}' but registering as '{SanitizeForLog(message.AgentId.Value)}'");
        }

        // If an agent with the same ID is already connected with a different connectionId,
        // force-disconnect the old connection before re-registering.
        // Exception: when the reconnecting agent has the SAME hostname as the existing entry
        // (i.e. it is the same pod) and carries no ActiveJob while the existing entry has one,
        // this is a mid-run kiro-cli sub-process restart — the old connection is still being used
        // by the running pipeline's OrchestratorProxy. Killing it here severs all subsequent hub
        // calls (RequestGetIssue, etc.) on that connection without any server-side error log.
        // Skip ForceDisconnect to preserve the pipeline connection.
        // A different hostname unambiguously identifies a new pod (pod replacement), regardless of
        // whether message.ActiveJob is null — the new pod may not yet have a job assignment at
        // registration time. In that case the guard does NOT skip ForceDisconnect: a new pod must
        // always evict the stale connection of the old pod it is replacing.
        var existingEntry = _facade.GetByAgentId(message.AgentId);
        var preserveExistingConnectionId = false;
        if (existingEntry is not null && existingEntry.ConnectionId != Context.ConnectionId
            && existingEntry.Status != AgentStatus.Disconnected)
        {
            // TODO: [WARNING] Null-hostname mixed-deployment risk: `AgentRegistrationMessage.Hostname`
            // is declared `required` but MessagePack does NOT enforce `required` at deserialization —
            // an older agent binary that does not send Key(1) will produce a null `message.Hostname`.
            // Likewise, `existingEntry.Hostname` may be null if the entry was persisted by a pre-fix
            // registration. A null hostname on either side means pod identity cannot be confirmed —
            // treat as "different pod" and fall through to ForceDisconnect. The null-safe comparison
            // (`is not null` guards on both sides) prevents `null == null` from evaluating to `true`
            // and reintroducing the original bug under mixed-deployment or pre-fix registry state.
            if (message.ActiveJob is null
                && existingEntry.ActiveJobId is not null
                && message.Hostname is not null
                && existingEntry.Hostname is not null
                && message.Hostname == existingEntry.Hostname)
            {
                // Mid-run kiro-cli reconnect: preserve the active pipeline connection.
                // Setting preserveExistingConnectionId=true keeps the old connection ID in
                // _connectionIndex so AgentAuthorizationFilter continues to resolve hub calls
                // arriving on that connection (e.g. RequestGetIssue called by OrchestratorProxy).
                // Without this, the registry silently evicts conn-A and all subsequent hub calls
                // on that connection are rejected as "reconnect-race" at Debug level — producing
                // zero Warning/Error logs while the run fails (issue #2758).
                // TODO: [WARNING] After skipping ForceDisconnect, _facade.Register below replaces
                // the registry entry with the new connection ID (conn-new). The old connection
                // (conn-old) remains live and is still used by the running pipeline's
                // OrchestratorProxy, but any server-side lookup of the agent by AgentId will now
                // return conn-new. Server-push messages dispatched by AgentId lookup during the
                // window between this guard skip and the pipeline's completion on the old connection
                // will be misdirected to conn-new. Impact is limited because current pipeline calls
                // are agent-initiated (agent calls RequestGetIssue, etc.) rather than server-pushed,
                // but this gap should be addressed if server-push patterns are added in future.
                _logger.Warning(
                    "RegisterAgent: agent {AgentId} reconnected without ActiveJob while job {JobId} is in flight — skipping ForceDisconnect to preserve pipeline connection",
                    message.AgentId, existingEntry.ActiveJobId);
                preserveExistingConnectionId = true;
            }
            else
            {
                _logger.Information("Agent {AgentId} re-registered (connection={NewConn}), force-disconnecting old connection {OldConn}",
                    message.AgentId, Context.ConnectionId, existingEntry.ConnectionId);
                try
                {
                    await Clients.Client(existingEntry.ConnectionId).ForceDisconnect();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to send ForceDisconnect to old connection {OldConn} for agent {AgentId}",
                        existingEntry.ConnectionId, message.AgentId);
                }
            }
        }

        _facade.Register(message, Context.ConnectionId, preserveExistingConnectionId);

        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "unknown";
        _logger.Information(
            "Agent registered: AgentId={AgentId} ServiceName={ServiceName} ConnectionId={ConnectionId}",
            message.AgentId, serviceName, Context.ConnectionId);

        // Recovery accepts the reported active job only for the agent's own work item, and only
        // then records the agent on the run (run.AgentId — in K8s dispatch mode AgentAcceptedRunAsync
        // is not called, so registration is where a dispatched run gets its agent).
        var recovery = await _orphanRecoveryService.RecoverOrphanedStateAsync(message, message.AgentId, Context.ConnectionAborted);

        // First pickup: the agent has now actually picked up the dispatched run, so this is the
        // moment to move the issue (or PR, for reviews) agent:next → agent:in-progress. Until now it
        // stayed agent:next while the WorkItem sat Pending in the queue (DistributionResult.Queued
        // contract). Recovery reports only a first pickup, so a pod-replacement reconnect does not
        // re-swap. Best-effort: a label-swap failure must not break registration or force-disconnect
        // the agent.
        if (recovery.FirstPickupRun is { } pickedUpRun && Guid.TryParse(pickedUpRun.RunId, out _))
        {
            _logger.Debug("RegisterAgent: agent {AgentId} picked up run {RunId}", message.AgentId, pickedUpRun.RunId);
            try
            {
                await SwapLabelAsync(pickedUpRun, AgentLabels.InProgress);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex,
                    "RegisterAgent: failed to swap label to agent:in-progress for run {RunId} (non-fatal)", pickedUpRun.RunId);
            }
        }
    }

    /// <summary>
    /// Deregisters an agent from the registry.
    /// Only allows the caller to deregister their own agent identity.
    /// </summary>
    public Task DeregisterAgent(AgentId agentId)
    {
        // TODO: Replace ArgumentNullException.ThrowIfNull(agentId.Value) with
        // ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId)).
        // ThrowIfNull on a struct field reports "Value" as the parameter name in exceptions, not "agentId".
        ArgumentNullException.ThrowIfNull(agentId.Value);

        // Security: verify caller owns this agentId (prevents cross-agent deregistration)
        var callerAgent = _facade.GetByConnectionId(Context.ConnectionId);
        if (callerAgent is null || !string.Equals(callerAgent.AgentId.Value, agentId.Value, StringComparison.Ordinal))
        {
            _logger.Warning(
                "DeregisterAgent rejected — caller connection {ConnectionId} does not own agent {AgentId}",
                Context.ConnectionId, SanitizeForLog(agentId.Value));
            return Task.CompletedTask;
        }

        _facade.Deregister(agentId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Agent signals it is ready for the next job. Triggers job dequeue.
    /// </summary>
    public Task AgentReady(AgentId agentId)
    {
        // TODO: Replace ArgumentNullException.ThrowIfNull(agentId.Value) with
        // ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId)) — see DeregisterAgent.
        ArgumentNullException.ThrowIfNull(agentId.Value);

        // Security: verify caller owns this agentId (prevents spurious drain signals)
        var callerAgent = _facade.GetByConnectionId(Context.ConnectionId);
        if (callerAgent is null || !string.Equals(callerAgent.AgentId.Value, agentId.Value, StringComparison.Ordinal))
        {
            _logger.Warning(
                "AgentReady rejected — caller connection {ConnectionId} does not own agent {AgentId}",
                Context.ConnectionId, SanitizeForLog(agentId.Value));
            return Task.CompletedTask;
        }

        _logger.Information("Agent {AgentId} signaled ready", agentId.Value);
        return Task.CompletedTask;
    }
}
