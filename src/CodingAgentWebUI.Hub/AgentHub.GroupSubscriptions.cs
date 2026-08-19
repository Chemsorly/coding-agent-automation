using Microsoft.AspNetCore.SignalR;

namespace CodingAgentWebUI.Hub;

public sealed partial class AgentHub
{
    // ── UI group subscriptions ──────────────────────────────────────────

    /// <summary>
    /// Adds the caller's connection to the <c>run-{jobId}</c> SignalR group so that
    /// subsequent push events (<see cref="IAgentHubUiClient"/>) are delivered to it.
    ///
    /// Ownership check (Req 5.3a): an agent-authenticated caller is only allowed to
    /// observe runs assigned to themselves. Operator-authenticated callers (those with
    /// no <c>agentId</c> query parameter) may subscribe to any run.
    /// </summary>
    public async Task SubscribeToRun(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        // Agent connections carry an agentId query parameter. UI/operator connections do not.
        var callerAgentId = Context.GetHttpContext()?.Request.Query["agentId"].ToString();
        if (!string.IsNullOrEmpty(callerAgentId))
        {
            // Caller is an agent connection — enforce per-run ownership (Req 5.3a).
            var run = _facade.GetRun(new Pipeline.Models.JobId(jobId));

            // Reject if: the run exists AND the run is not assigned to this agent.
            if (run is not null && !string.Equals(run.AgentId, callerAgentId, StringComparison.Ordinal))
            {
                _logger.Warning(
                    "SubscribeToRun rejected — agent {AgentId} is not assigned to run {JobId}",
                    callerAgentId, jobId);
                throw new HubException("Not authorized for this run.");
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"run-{jobId}");
        _logger.Debug("Connection {ConnectionId} subscribed to run-{JobId}", Context.ConnectionId, jobId);
    }

    /// <summary>
    /// Removes the caller's connection from the <c>run-{jobId}</c> group.
    /// Called when the UI navigates away from a run page.
    /// </summary>
    public Task UnsubscribeFromRun(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, $"run-{jobId}");
    }
}
