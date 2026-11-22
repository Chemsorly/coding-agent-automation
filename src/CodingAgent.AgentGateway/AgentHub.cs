using CodingAgent.Infrastructure.Common;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// SignalR hub hosted at <c>/hubs/agent</c>. Agents connect as clients and invoke
/// server-side methods for registration, status reporting, issue operations, and job lifecycle.
/// Implements <see cref="Hub{T}"/> with <see cref="IAgentHubClient"/> for strongly-typed
/// client method invocations (CancelJob, AssignChatPrompt, CancelChat, RequestFetchModels, ForceDisconnect).
/// </summary>
public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
{
    private readonly IAgentHubFacade _facade;
    private readonly IChatNotifier _chatNotifier;
    private readonly IChangeNotifier _changeNotifier;
    private readonly IHubConsolidationOperations _consolidationOps;
    private readonly IHubIssueOperations _issueOps;
    private readonly IAgentJobLifecycleService _lifecycleService;
    private readonly IAgentTokenRefreshService _tokenRefreshService;
    private readonly IAgentOrphanRecoveryService _orphanRecoveryService;
    private readonly ILogger _logger;
    private readonly IHubContext<UiHub> _uiContext;
    private readonly IHubTriageOperations? _triageOps;
    private readonly ICredentialUsageObserver? _usageObserver;

    /// <summary>
    /// Primary constructor used by SignalR's hub activator (ActivatorUtilities).
    /// A single constructor is required — multiple constructors cause
    /// <see cref="InvalidOperationException"/> at connection time.
    /// </summary>
    public AgentHub(AgentHubDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        _facade = deps.Facade;
        _chatNotifier = deps.ChatNotifier;
        _changeNotifier = deps.ChangeNotifier;
        _consolidationOps = deps.ConsolidationOps;
        _issueOps = deps.IssueOps;
        _lifecycleService = deps.LifecycleService;
        _tokenRefreshService = deps.TokenRefreshService;
        _orphanRecoveryService = deps.OrphanRecoveryService;
        _logger = deps.Logger;
        _uiContext = deps.UiContext;
        _triageOps = deps.TriageOps;
        _usageObserver = deps.UsageObserver;
    }

    /// <summary>
    /// Validates that the connecting agent provided an <c>agentId</c> query parameter. Only agents use this
    /// hub; the UI connects to <see cref="UiHub"/> (spec 051 D7).
    /// </summary>
    public override Task OnConnectedAsync()
    {
        var agentId = Context.GetHttpContext()?.Request.Query["agentId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(agentId))
        {
            _logger.Warning("Connection {ConnectionId} rejected — missing agentId query parameter", Context.ConnectionId);
            Context.Abort();
            return Task.CompletedTask;
        }

        _logger.Information("Agent connection established: agentId={AgentId}, connectionId={ConnectionId}", SanitizeForLog(agentId), Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    /// <summary>
    /// Handles agent disconnection. For ephemeral chat pods (identified by <c>chat=true</c> label),
    /// the entry is fully removed from the registry via <c>Deregister</c> — there is no reconnect
    /// for chat pods, so a <c>Disconnected</c> entry would persist indefinitely (issue #2109).
    /// For persistent worker agents, the entry is transitioned to <see cref="AgentStatus.Disconnected"/>
    /// so the existing reconnect and orphan-recovery flow can proceed unchanged.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        if (agent is not null)
        {
            // Guard for mid-run kiro-cli sub-process reconnect (issue #2758):
            // When RegisterAgent takes the mid-run branch (skips ForceDisconnect), it calls
            // Register with preserveExistingConnectionId=true, which keeps conn-A in _connectionIndex
            // alongside the new conn-B. When conn-A later closes, GetByConnectionId(conn-A) returns
            // the agent entry — but that entry's ConnectionId is already conn-B (the new primary
            // connection). Calling TransitionStatus(Disconnected) here would incorrectly mark the
            // still-live agent as Disconnected, triggering spurious orphan recovery and potentially
            // allowing re-dispatch of a running job.
            // Safe because both implementations guarantee agent.ConnectionId == conn-B after
            // Register(conn-B) completes: DistributedAgentRegistryService overwrites _localSnapshot
            // with the conn-B entry; AgentRegistryService mutates existing.ConnectionId = conn-B.
            if (agent.ConnectionId != Context.ConnectionId)
            {
                _logger.Information(
                    "Agent {AgentId} stale connection {StaleConn} closed (active connection: {ActiveConn}) — skipping status transition",
                    agent.AgentId, Context.ConnectionId, agent.ConnectionId);
                _facade.RemoveFromConnectionIndex(Context.ConnectionId);
                await base.OnDisconnectedAsync(exception);
                return;
            }

            // Cross-replica reconnect guard (issue #3554):
            // The #2758 guard above only catches same-replica reconnects, where
            // agent.ConnectionId != Context.ConnectionId because Register already updated the
            // local snapshot to conn-B on this replica. In a cross-replica reconnect, replica A's
            // local snapshot still holds conn-A (only replica B has written conn-B to Redis), so
            // agent.ConnectionId == Context.ConnectionId passes the guard above. We must read the
            // authoritative store to detect this case: if the current Redis entry shows a different
            // ConnectionId, the agent has already registered on another connection on another replica
            // and we must not change its status.
            // Note: string.Equals with Ordinal is equivalent to the != operator used in the #2758
            // guard — both perform ordinal comparison on SignalR-generated connection ID strings.
            AgentEntry? current;
            try
            {
                current = await _facade.GetByAgentIdAsync(agent.AgentId, CancellationToken.None);
            }
            catch (Exception redisEx) when (redisEx is not OperationCanceledException)
            {
                // If the authoritative store is unavailable, treat the entry as unknown and fall
                // through to the existing handling (TransitionStatus / Deregister). A Redis outage
                // at disconnect time is fail-open: the agent is marked Disconnected as if the entry
                // had expired, which is safe. The alternative — propagating the exception — would
                // prevent base.OnDisconnectedAsync from running and leave SignalR bookkeeping in a
                // partially-cleaned-up state.
                _logger.Warning(redisEx,
                    "OnDisconnectedAsync: GetByAgentIdAsync failed for agent {AgentId} — treating as unknown, proceeding",
                    agent.AgentId);
                current = null;
            }
            if (current is not null && !string.Equals(current.ConnectionId, Context.ConnectionId, StringComparison.Ordinal))
            {
                _logger.Information(
                    "Agent {AgentId} connection {ConnectionId} closed, but the agent is registered on connection {CurrentConnectionId}; skipping",
                    agent.AgentId, Context.ConnectionId, current.ConnectionId);
                await base.OnDisconnectedAsync(exception);
                return;
            }
            // Proceed: entry is null (TTL-expired / deregistered) or connection IDs match.

            // TODO: Verify that AgentEntry.Labels is IReadOnlyList<string> or another immutable/thread-safe
            // collection type. If it is a mutable List<T>, concurrent writes (e.g., from a registry update
            // on another thread) could cause InvalidOperationException during this iteration.
            // See review finding: DotNetSpecialist [WARNING] AgentHub.cs:95
            bool isChatAgent = agent.Labels.Any(l =>
                string.Equals(l, "chat=true", StringComparison.Ordinal));

            if (isChatAgent)
            {
                _facade.Deregister(agent.AgentId);
                _logger.Information(
                    "Chat agent {AgentId} deregistered on disconnect (connectionId={ConnectionId})",
                    agent.AgentId, Context.ConnectionId);
            }
            else
            {
                _facade.TransitionStatus(agent.AgentId, AgentStatus.Disconnected);
                // Elevate to Warning when an active job is interrupted — helps trace abrupt disconnects
                // mid-run. Include BusySince so the log shows how long the agent had been working.
                if (agent.ActiveJobId is not null)
                {
                    _logger.Warning(
                        "Agent {AgentId} disconnected with active job (connectionId={ConnectionId}, activeJobId={ActiveJobId}, busySince={BusySince}, orphanRestoredAt={OrphanRestoredAt}, exception={Exception})",
                        agent.AgentId, Context.ConnectionId, agent.ActiveJobId,
                        agent.BusySince?.ToString("O") ?? "none",
                        agent.OrphanRestoredAt?.ToString("O") ?? "none",
                        exception?.Message ?? "none");
                }
                else
                {
                    _logger.Information(
                        "Agent {AgentId} disconnected (connectionId={ConnectionId}, activeJobId=none, exception={Exception})",
                        agent.AgentId, Context.ConnectionId, exception?.Message ?? "none");
                }
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ── Heartbeat ───────────────────────────────────────────────────────

    /// <summary>
    /// Updates the agent's heartbeat timestamp in the registry.
    /// When the agent reports an active pipeline step matching the run's current step,
    /// also refreshes <see cref="PipelineRun.LastStepChangeAt"/> to prevent the progress
    /// timeout from killing agents legitimately waiting in long-running steps (e.g., ExternalCi polling).
    /// When the agent reports a different non-terminal step, corrects the run to that step.
    /// Does NOT refresh when <c>CurrentStep</c> is null — preserving stuck-agent detection (#788).
    /// </summary>
    public async Task Heartbeat(HeartbeatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Security: verify caller owns this agentId (prevents heartbeat spoofing)
        var callerAgent = _facade.GetByConnectionId(Context.ConnectionId);
        if (callerAgent is null || !string.Equals(callerAgent.AgentId.Value, message.AgentId.Value, StringComparison.Ordinal))
        {
            _logger.Warning(
                "Heartbeat rejected — caller connection {ConnectionId} does not own agent {AgentId}",
                Context.ConnectionId, SanitizeForLog(message.AgentId.Value));
            return;
        }

        _facade.UpdateHeartbeat(message.AgentId, message.Timestamp);

        // If the agent reports an active pipeline step, treat as progress evidence.
        // When CurrentStep is null the agent considers itself idle (job done locally) —
        // don't reset the clock so the progress timeout can still detect stuck-in-Busy (#788).
        if (message.CurrentStep is not null)
        {
            var agent = await _facade.GetByAgentIdAsync(message.AgentId, Context.ConnectionAborted);
            if (agent?.ActiveJobId is not null)
            {
                var run = _facade.GetRun(agent.ActiveJobId);

                // The agent is mid-run but the API has no run record for it — Redis lost its data while
                // the agent stayed connected. Nothing rebuilds the run except the agent registering
                // again with its active job, so close the connection to make it do so. A terminal step
                // is left alone: a finishing agent must not get its run recreated.
                if (run is null
                    && !message.CurrentStep.Value.IsTerminal()
                    && await IsRestorableRunAsync(agent, Context.ConnectionAborted))
                {
                    _logger.Warning(
                        "Heartbeat from agent {AgentId}: active job {JobId} has no run record, closing the connection so the agent re-registers and the run is restored",
                        message.AgentId.Value, agent.ActiveJobId);
                    CloseConnectionSoAgentReRegisters();
                    return;
                }

                // A step report sent while the hub was disconnected is lost, and a restored run starts at the step it registered
                // with. The heartbeat carries the agent's real step, so correct the run here.
                if (run is not null
                    && run.CurrentStep != message.CurrentStep
                    && !message.CurrentStep.Value.IsTerminal()
                    && !run.CurrentStep.IsTerminal())
                {
                    _logger.Information(
                        "Heartbeat from agent {AgentId}: job {JobId} is at {RunStep} but the agent reports {AgentStep}, correcting the run",
                        message.AgentId.Value, agent.ActiveJobId, run.CurrentStep, message.CurrentStep.Value);
                    var correctedAt = message.Timestamp <= DateTimeOffset.UtcNow ? message.Timestamp : DateTimeOffset.UtcNow;
                    _lifecycleService.HandleStepTransition((JobId)agent.ActiveJobId, message.CurrentStep.Value, correctedAt, null);
                    return;
                }

                if (run is not null && run.CurrentStep == message.CurrentStep)
                {
                    // Clamp to server time to prevent a misbehaving agent from sending far-future timestamps
                    var clampedTimestamp = message.Timestamp <= DateTimeOffset.UtcNow
                        ? message.Timestamp
                        : DateTimeOffset.UtcNow;
                    run.LastStepChangeAt = clampedTimestamp;

                    // Persist progress to DB for cross-replica timeout enforcement (throttled)
                    _ = _facade.TouchLastProgressAsync(agent.ActiveJobId, clampedTimestamp, CancellationToken.None);
                }
            }
        }
    }

    // ── Shared private helpers ──────────────────────────────────────────

    /// <summary>
    /// True when the agent re-registering would get the run it is working on rebuilt: the work item
    /// exists, is the agent's own, and is not the consolidation task type (consolidation recovery
    /// tracks the job without creating a run, so closing the connection would only repeat every
    /// heartbeat). Mirrors the checks in <c>AgentOrphanRecoveryService.RestoreActiveJobAsync</c>.
    /// Returns false, never throws, when the work item cannot be read — a heartbeat must not fail for this.
    /// </summary>
    private async Task<bool> IsRestorableRunAsync(AgentEntry agent, CancellationToken ct)
    {
        if (!_facade.CanVerifyWorkItems) return false;
        try
        {
            var record = await _facade.GetWorkItemRunRecordAsync(agent.ActiveJobId!, ct);
            return record is not null
                && record.TaskType != WorkItemTaskType.Consolidation
                && record.IsOwnedBy(agent.AgentId.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Heartbeat: could not read the work item of job {JobId}", agent.ActiveJobId);
            return false;
        }
    }

    /// <summary>
    /// Swaps the agent label on the entity (issue or PR) using the shared issue operations service.
    /// The target entity kind is derived from <see cref="PipelineRun.LabelTargetKind"/> inside the service.
    /// </summary>
    // TODO: Thread a meaningful CancellationToken (e.g. Context.ConnectionAborted) through these
    // agent-initiated wrappers. Currently uses default (CancellationToken.None) — fix tracked as
    // a follow-up to issue #2473 which addressed the completion-path bookkeeping token.
    private Task SwapLabelAsync(PipelineRun run, string newLabel)
        => _issueOps.SwapLabelAsync(run, newLabel);

    /// <summary>
    /// Posts a comment on the issue using the shared issue operations service.
    /// Returns the comment URL if available.
    /// </summary>
    // TODO: Thread a meaningful CancellationToken (e.g. Context.ConnectionAborted) through these
    // agent-initiated wrappers. Currently uses default (CancellationToken.None) — fix tracked as
    // a follow-up to issue #2473 which addressed the completion-path bookkeeping token.
    private Task<string?> PostCommentViaIssueProviderAsync(PipelineRun run, string body)
        => _issueOps.PostCommentViaIssueProviderAsync(run, body);

    /// <summary>
    /// Strips newline characters from a user-supplied string before it is written to a log entry,
    /// preventing log injection / log forging attacks.
    /// Delegates to the shared <see cref="CodingAgent.Infrastructure.Common.LogSanitizer"/> utility.
    /// </summary>
    private static string SanitizeForLog(string? value) => LogSanitizer.SanitizeForLog(value);
}
