using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgentWebUI.Hub;

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
            // TODO: The HubException message below interpolates message.AgentId (unsanitized struct)
            // while the log above uses SanitizeForLog. The exception is returned to the caller (not
            // written to server logs), so log injection is not the risk here, but the inconsistency
            // between sanitized log args and unsanitized exception message text may confuse future
            // maintainers about the threat model. Consider applying the same sanitization or documenting
            // why they intentionally differ.
            _logger.Warning(
                "RegisterAgent rejected — message agentId '{MessageAgentId}' does not match query param '{QueryAgentId}'",
                SanitizeForLog(message.AgentId.Value), SanitizeForLog(queryAgentId));
            throw new HubException($"AgentId mismatch: message has '{message.AgentId}' but connection has '{queryAgentId}'");
        }

        // Defense-in-depth: validate authenticated identity matches registration
        var authenticatedAgentId = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(authenticatedAgentId) && authenticatedAgentId != "agent" &&
            !string.Equals(message.AgentId.Value, authenticatedAgentId, StringComparison.Ordinal))
        {
            _logger.Warning(
                "RegisterAgent rejected — authenticated as '{AuthenticatedAgentId}' but registering as '{MessageAgentId}'",
                SanitizeForLog(authenticatedAgentId), SanitizeForLog(message.AgentId.Value));
            throw new HubException($"AgentId mismatch: authenticated as '{authenticatedAgentId}' but registering as '{message.AgentId}'");
        }

        // If an agent with the same ID is already connected with a different connectionId,
        // force-disconnect the old connection before re-registering.
        var existingEntry = _facade.GetByAgentId(message.AgentId);
        if (existingEntry is not null && existingEntry.ConnectionId != Context.ConnectionId
            && existingEntry.Status != AgentStatus.Disconnected)
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

        _facade.Register(message, Context.ConnectionId);

        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "unknown";
        _logger.Information(
            "Agent registered: AgentId={AgentId} ServiceName={ServiceName} ConnectionId={ConnectionId}",
            message.AgentId, serviceName, Context.ConnectionId);

        await _orphanRecoveryService.RecoverOrphanedStateAsync(message, message.AgentId);
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
                Context.ConnectionId, agentId.Value);
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
                Context.ConnectionId, agentId.Value);
            return Task.CompletedTask;
        }

        _logger.Information("Agent {AgentId} signaled ready", agentId.Value);
        _facade.Signal();
        return Task.CompletedTask;
    }
}
