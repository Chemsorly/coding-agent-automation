using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hub;

/// <summary>
/// SignalR hub hosted at <c>/hubs/agent</c>. Agents connect as clients and invoke
/// server-side methods for registration, status reporting, issue operations, and job lifecycle.
/// Implements <see cref="Hub{T}"/> with <see cref="IAgentHubClient"/> for strongly-typed
/// client method invocations (AssignJob, CancelJob).
/// </summary>
public sealed partial class AgentHub : Hub<IAgentHubClient>, IAgentHub
{
    private readonly IAgentHubFacade _facade;
    private readonly IChatNotifier _chatNotifier;
    private readonly IChangeNotifier _changeNotifier;
    private readonly ModelFetchService _modelFetchService;
    private readonly IConsolidationService _consolidationService;
    private readonly ConsolidationBadgeService _badgeService;
    private readonly IHubIssueOperations _issueOps;
    private readonly IAgentJobLifecycleService _lifecycleService;
    private readonly IAgentTokenRefreshService _tokenRefreshService;
    private readonly IGateCommentFormatter _gateCommentFormatter;
    private readonly IAgentOrphanRecoveryService _orphanRecoveryService;
    private readonly ILogger _logger;
    private readonly IHubContext<AgentHub> _uiContext;

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
        _modelFetchService = deps.ModelFetchService;
        _consolidationService = deps.ConsolidationService;
        _badgeService = deps.BadgeService;
        _issueOps = deps.IssueOps;
        _lifecycleService = deps.LifecycleService;
        _tokenRefreshService = deps.TokenRefreshService;
        _gateCommentFormatter = deps.GateCommentFormatter;
        _orphanRecoveryService = deps.OrphanRecoveryService;
        _logger = deps.Logger;
        _uiContext = deps.UiContext;
    }

    /// <summary>
    /// Validates that the connecting agent provided an <c>agentId</c> query parameter.
    /// Operator connections (no <c>agentId</c>) are allowed through — they are authenticated
    /// as "operator" callers for hub group subscriptions (e.g. UI circuits).
    /// </summary>
    public override Task OnConnectedAsync()
    {
        var agentId = Context.GetHttpContext()?.Request.Query["agentId"].ToString();
        if (string.IsNullOrWhiteSpace(agentId))
        {
            // Check auth_kind claim — operator connections are valid without agentId
            var authKind = Context.User?.FindFirst("auth_kind")?.Value;
            if (!string.Equals(authKind, "operator", StringComparison.Ordinal))
            {
                _logger.Warning("Connection {ConnectionId} rejected — missing agentId query parameter and not an operator",
                    Context.ConnectionId);
                Context.Abort();
                return Task.CompletedTask;
            }

            _logger.Debug("Operator connection established: connectionId={ConnectionId}", Context.ConnectionId);
            return base.OnConnectedAsync();
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
        if (callerAgent is null || !string.Equals(callerAgent.AgentId.Value, message.AgentId.Value, StringComparison.Ordinal))
        {
            // TODO: message.AgentId.Value is logged here as a raw string. If AgentId.Value is null,
            // this logs a null literal rather than the struct's ToString() representation (which the
            // original code used). This is a minor semantic change from the refactor — the struct's
            // ToString() would have returned a meaningful fallback string, while .Value logs null.
            // Consider using SanitizeForLog(message.AgentId.Value) or message.AgentId.ToString()
            // for consistent null-safe log output.
            _logger.Warning(
                "Heartbeat rejected — caller connection {ConnectionId} does not own agent {AgentId}",
                Context.ConnectionId, message.AgentId.Value);
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

    // ── Shared private helpers ──────────────────────────────────────────

    /// <summary>
    /// Swaps the agent label on the entity (issue or PR) using the shared issue operations service.
    /// The target entity kind is derived from <see cref="PipelineRun.LabelTargetKind"/> inside the service.
    /// </summary>
    private Task SwapLabelAsync(PipelineRun run, string newLabel)
        => _issueOps.SwapLabelAsync(run, newLabel);

    /// <summary>
    /// Posts a comment on the issue using the shared issue operations service.
    /// Returns the comment URL if available.
    /// </summary>
    private Task<string?> PostCommentViaIssueProviderAsync(PipelineRun run, string body)
        => _issueOps.PostCommentViaIssueProviderAsync(run, body);

    /// <summary>
    /// Strips newline characters from a user-supplied string before it is written to a log entry,
    /// preventing log injection / log forging attacks.
    /// </summary>
    private static string SanitizeForLog(string? value)
        => value?.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) ?? "";
}
