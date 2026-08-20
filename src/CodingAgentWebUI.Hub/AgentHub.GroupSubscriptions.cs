using CodingAgentWebUI.Pipeline.Interfaces;
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
    ///
    /// Spec 045 Req 3.4a: immediately pushes the current output backlog to the new subscriber
    /// so navigating to a mid-run page shows existing output without a separate fetch.
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

            // Fail closed. An unknown jobId means the run is not registered on this hub, so
            // nothing establishes that this agent owns it — admitting the subscription would
            // let an agent camp on an arbitrary run id and receive the full output stream
            // (which carries tokens and repository content) once that run starts.
            if (run is null || !string.Equals(run.AgentId, callerAgentId, StringComparison.Ordinal))
            {
                _logger.Warning(
                    "SubscribeToRun rejected — agent {AgentId} is not assigned to run {JobId}",
                    callerAgentId, jobId);
                throw new HubException("Not authorized for this run.");
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"run-{jobId}");
        _logger.Debug("Connection {ConnectionId} subscribed to run-{JobId}", Context.ConnectionId, jobId);

        // Push buffered output lines to the new subscriber immediately (Req 3.4a).
        // This ensures a Blazor circuit navigating to a mid-run page sees existing output
        // without a separate backlog fetch — the normal OnOutputLines handler receives them.
        var buffer = _facade.GetOutputBuffer(new Pipeline.Models.JobId(jobId));
        if (buffer.Count > 0)
        {
            var lines = buffer.GetAll();
            await _uiContext.Clients.Client(Context.ConnectionId)
                .SendAsync(HubMethodNames.OnOutputLines, jobId, lines);
            _logger.Debug("Pushed {LineCount} buffered output lines to new subscriber for run-{JobId}",
                lines.Count, jobId);
        }
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
