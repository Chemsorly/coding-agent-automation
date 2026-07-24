using System.Text.Json;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// SignalR hub hosted at <c>/hubs/agent</c>. Agents connect as clients and invoke
/// server-side methods for registration, status reporting, issue operations, and job lifecycle.
/// Implements <see cref="Hub{T}"/> with <see cref="IAgentHubClient"/> for strongly-typed
/// client method invocations (AssignJob, CancelJob).
/// </summary>
public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
{
    private readonly IAgentHubFacade _facade;
    // TODO: Replace concrete PipelineOrchestrationService with IChangeNotifier once AgentHub.Chat.cs
    // methods (NotifyChatResponse, NotifyChatCompleted) are moved behind a narrow interface.
    // AgentHub.Pipeline.cs and AgentHub.Consolidation.cs only call NotifyChange() and could use IChangeNotifier.
    private readonly PipelineOrchestrationService _orchestration;
    private readonly ModelFetchService _modelFetchService;
    private readonly IConsolidationService _consolidationService;
    private readonly ConsolidationBadgeService _badgeService;
    private readonly IHubIssueOperations _issueOps;
    private readonly IAgentJobLifecycleService _lifecycleService;
    private readonly IAgentTokenRefreshService _tokenRefreshService;
    private readonly IGateCommentFormatter _gateCommentFormatter;
    private readonly IAgentOrphanRecoveryService _orphanRecoveryService;
    private readonly ILogger _logger;

    public AgentHub(
        IAgentHubFacade facade,
        PipelineOrchestrationService orchestration,
        ModelFetchService modelFetchService,
        IConsolidationService consolidationService,
        ConsolidationBadgeService badgeService,
        IHubIssueOperations issueOps,
        IAgentJobLifecycleService lifecycleService,
        IAgentTokenRefreshService tokenRefreshService,
        IGateCommentFormatter gateCommentFormatter,
        ILogger logger,
        IAgentOrphanRecoveryService? orphanRecoveryService = null)
    {
        _facade = facade;
        _orchestration = orchestration;
        _modelFetchService = modelFetchService;
        _consolidationService = consolidationService;
        _badgeService = badgeService;
        _issueOps = issueOps;
        _lifecycleService = lifecycleService;
        _tokenRefreshService = tokenRefreshService;
        _gateCommentFormatter = gateCommentFormatter;
        // TODO: Make IAgentOrphanRecoveryService a required (non-nullable) constructor parameter.
        // The service is registered in DI as a singleton — the nullable fallback couples the Hub
        // to the concrete type and creates an unmanaged instance if DI misconfiguration occurs.
        // It also forces AgentHub to keep the concrete PipelineOrchestrationService import just
        // to pass it as IChangeNotifier in the fallback construction below.
        _orphanRecoveryService = orphanRecoveryService ?? new AgentOrphanRecoveryService(facade, orchestration, logger);
        _logger = logger;
    }

    /// <summary>
    /// Validates that the connecting agent provided an <c>agentId</c> query parameter.
    /// Rejects the connection if missing.
    /// </summary>
    public override Task OnConnectedAsync()
    {
        var agentId = Context.GetHttpContext()?.Request.Query["agentId"].ToString();
        if (string.IsNullOrWhiteSpace(agentId))
        {
            _logger.Warning("Connection {ConnectionId} rejected — missing agentId query parameter", Context.ConnectionId);
            Context.Abort();
            return Task.CompletedTask;
        }

        _logger.Information("Agent connection established: agentId={AgentId}, connectionId={ConnectionId}", agentId, Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    /// <summary>
    /// Transitions the disconnected agent to <see cref="AgentStatus.Disconnected"/> in the registry.
    /// </summary>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        if (agent is not null)
        {
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Disconnected);
            _logger.Information(
                "Agent {AgentId} disconnected (connectionId={ConnectionId}, activeJobId={ActiveJobId}, exception={Exception})",
                agent.AgentId, Context.ConnectionId, agent.ActiveJobId ?? "none", exception?.Message ?? "none");
        }

        return base.OnDisconnectedAsync(exception);
    }

    // ── Registration ────────────────────────────────────────────────────

    /// <summary>
    /// Registers an agent in the registry. Validates that the <c>agentId</c> in the message
    /// matches the <c>agentId</c> query parameter from the connection and the authenticated identity.
    /// </summary>
    public async Task RegisterAgent(AgentRegistrationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var queryAgentId = Context.GetHttpContext()?.Request.Query["agentId"].ToString();
        if (!string.Equals(message.AgentId, queryAgentId, StringComparison.Ordinal))
        {
            _logger.Warning(
                "RegisterAgent rejected — message agentId '{MessageAgentId}' does not match query param '{QueryAgentId}'",
                message.AgentId, queryAgentId);
            throw new HubException($"AgentId mismatch: message has '{message.AgentId}' but connection has '{queryAgentId}'");
        }

        // Defense-in-depth: validate authenticated identity matches registration
        var authenticatedAgentId = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(authenticatedAgentId) && authenticatedAgentId != "agent" &&
            !string.Equals(message.AgentId, authenticatedAgentId, StringComparison.Ordinal))
        {
            _logger.Warning(
                "RegisterAgent rejected — authenticated as '{AuthenticatedAgentId}' but registering as '{MessageAgentId}'",
                authenticatedAgentId, message.AgentId);
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

        await _orphanRecoveryService.RecoverOrphanedStateAsync(message, message.AgentId);
    }

    /// <summary>
    /// Deregisters an agent from the registry.
    /// Only allows the caller to deregister their own agent identity.
    /// </summary>
    public Task DeregisterAgent(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        // Security: verify caller owns this agentId (prevents cross-agent deregistration)
        var callerAgent = _facade.GetByConnectionId(Context.ConnectionId);
        if (callerAgent is null || !string.Equals(callerAgent.AgentId, agentId, StringComparison.Ordinal))
        {
            _logger.Warning(
                "DeregisterAgent rejected — caller connection {ConnectionId} does not own agent {AgentId}",
                Context.ConnectionId, agentId);
            return Task.CompletedTask;
        }

        _facade.Deregister(agentId);
        return Task.CompletedTask;
    }

    // ── Heartbeat ───────────────────────────────────────────────────────

    /// <summary>
    /// Updates the agent's heartbeat timestamp in the registry.
    /// When the agent reports an active pipeline step matching the run's current step,
    /// also refreshes <see cref="PipelineRun.LastStepChangeAt"/> to prevent the progress
    /// timeout from killing agents legitimately waiting in long-running steps (e.g., ExternalCi polling).
    /// Does NOT refresh when <c>CurrentStep</c> is null — preserving stuck-agent detection (#788).
    /// </summary>
    public Task Heartbeat(HeartbeatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Security: verify caller owns this agentId (prevents heartbeat spoofing)
        var callerAgent = _facade.GetByConnectionId(Context.ConnectionId);
        if (callerAgent is null || !string.Equals(callerAgent.AgentId, message.AgentId, StringComparison.Ordinal))
        {
            _logger.Warning(
                "Heartbeat rejected — caller connection {ConnectionId} does not own agent {AgentId}",
                Context.ConnectionId, message.AgentId);
            return Task.CompletedTask;
        }

        _facade.UpdateHeartbeat(message.AgentId, message.Timestamp);

        // If the agent reports an active pipeline step, treat as progress evidence.
        // When CurrentStep is null the agent considers itself idle (job done locally) —
        // don't reset the clock so the progress timeout can still detect stuck-in-Busy (#788).
        if (message.CurrentStep is not null)
        {
            var agent = _facade.GetByAgentId(message.AgentId);
            if (agent?.ActiveJobId is not null)
            {
                var run = _facade.GetRun(agent.ActiveJobId);
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

        return Task.CompletedTask;
    }

    /// <summary>
    /// Agent signals it is ready for the next job. Triggers job dequeue.
    /// </summary>
    public Task AgentReady(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        // Security: verify caller owns this agentId (prevents spurious drain signals)
        var callerAgent = _facade.GetByConnectionId(Context.ConnectionId);
        if (callerAgent is null || !string.Equals(callerAgent.AgentId, agentId, StringComparison.Ordinal))
        {
            _logger.Warning(
                "AgentReady rejected — caller connection {ConnectionId} does not own agent {AgentId}",
                Context.ConnectionId, agentId);
            return Task.CompletedTask;
        }

        _logger.Information("Agent {AgentId} signaled ready", agentId);
        _facade.Signal();
        return Task.CompletedTask;
    }

    // ── Shared private helpers ──────────────────────────────────────────

    /// <summary>
    /// Swaps the agent label on the entity (issue or PR) using the shared issue operations service.
    /// Routes based on <paramref name="targetKind"/>: Issue → IssueProviderConfigId, PullRequest → RepoProviderConfigId.
    /// </summary>
    private Task SwapLabelAsync(PipelineRun run, string newLabel, LabelTargetKind targetKind)
        => _issueOps.SwapLabelAsync(run, newLabel, targetKind);

    /// <summary>
    /// Posts a comment on the issue using the shared issue operations service.
    /// Returns the comment URL if available.
    /// </summary>
    private Task<string?> PostCommentViaIssueProviderAsync(PipelineRun run, string body)
        => _issueOps.PostCommentViaIssueProviderAsync(run, body);

    /// <summary>
    /// Determines the correct <see cref="LabelTargetKind"/> based on the run's <see cref="PipelineRun.RunType"/>.
    /// </summary>
    private static LabelTargetKind GetLabelTargetKind(PipelineRun run) => run.LabelTargetKind;
}
