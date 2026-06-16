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
    private readonly ITokenVendingService _tokenVending;
    private readonly PipelineOrchestrationService _orchestration;
    private readonly ModelFetchService _modelFetchService;
    private readonly IConsolidationService _consolidationService;
    private readonly ConsolidationBadgeService _badgeService;
    private readonly ILabelSwapper _labelSwapper;
    private readonly ILogger _logger;

    public AgentHub(
        IAgentHubFacade facade,
        ITokenVendingService tokenVending,
        PipelineOrchestrationService orchestration,
        ModelFetchService modelFetchService,
        IConsolidationService consolidationService,
        ConsolidationBadgeService badgeService,
        ILabelSwapper labelSwapper,
        ILogger logger)
    {
        _facade = facade;
        _tokenVending = tokenVending;
        _orchestration = orchestration;
        _modelFetchService = modelFetchService;
        _consolidationService = consolidationService;
        _badgeService = badgeService;
        _labelSwapper = labelSwapper;
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
    public Task RegisterAgent(AgentRegistrationMessage message)
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

        _facade.Register(message, Context.ConnectionId);

        // Re-track active job from agent state (handles orchestrator restart scenario)
        if (message.ActiveJob is not null)
        {
            var existingRun = _facade.GetRun(message.ActiveJob.RunId);
            if (existingRun is null)
            {
                // Check history — don't re-register a completed run
                var inHistory = _facade.GetRunHistory()
                    .Any(r => r.RunId == message.ActiveJob.RunId);

                if (!inHistory)
                {
                    #pragma warning disable CS0618 // StartedAt is obsolete but required for PipelineRun construction
                    var restoredRun = new PipelineRun
                    {
                        RunId = message.ActiveJob.RunId,
                        IssueIdentifier = message.ActiveJob.IssueIdentifier,
                        IssueTitle = message.ActiveJob.IssueTitle,
                        IssueProviderConfigId = message.ActiveJob.IssueProviderConfigId,
                        RepoProviderConfigId = message.ActiveJob.RepoProviderConfigId,
                        AgentProviderConfigId = message.ActiveJob.AgentProviderConfigId,
                        BrainProviderConfigId = message.ActiveJob.BrainProviderConfigId,
                        PipelineProviderConfigId = message.ActiveJob.PipelineProviderConfigId,
                        StartedAt = message.ActiveJob.StartedAt.UtcDateTime,
                        StartedAtOffset = message.ActiveJob.StartedAt,
                        LastStepChangeAt = DateTimeOffset.UtcNow,
                        CurrentStep = message.ActiveJob.CurrentStep,
                        AgentId = message.AgentId,
                        InitiatedBy = message.ActiveJob.InitiatedBy,
                        ResolvedProfileId = message.ActiveJob.ResolvedProfileId,
                        ProjectId = message.ActiveJob.ProjectId,
                        ProjectName = message.ActiveJob.ProjectName,
                        RunType = message.ActiveJob.RunType,
                        RepositoryName = message.ActiveJob.RepositoryName,
                        ModelName = message.ActiveJob.ModelName
                    };
                    #pragma warning restore CS0618

                    _facade.AddRun(restoredRun);

                    // Set agent as busy with this job
                    var restoredEntry = _facade.GetByAgentId(message.AgentId);
                    if (restoredEntry is not null)
                    {
                        restoredEntry.ActiveJobId = message.ActiveJob.RunId;
                        _facade.TransitionStatus(message.AgentId, AgentStatus.Busy);
                    }

                    _logger.Information(
                        "Restored active run {RunId} for agent {AgentId} (issue {IssueIdentifier}, step {Step}) — orchestrator state recovery",
                        message.ActiveJob.RunId, message.AgentId, message.ActiveJob.IssueIdentifier, message.ActiveJob.CurrentStep);

                    _orchestration.NotifyChange();
                }
                else
                {
                    _logger.Information(
                        "Agent {AgentId} reported active job {RunId} but it's already in history — ignoring stale state",
                        message.AgentId, message.ActiveJob.RunId);
                }
            }
            else
            {
                _logger.Debug("Agent {AgentId} active job {RunId} already tracked", message.AgentId, message.ActiveJob.RunId);
            }
        }

        // Detect orphaned runs: if the orchestrator tracks active runs for this agent
        // but the agent registered without an active job, restore the ActiveJobId on the
        // registry entry so the HeartbeatMonitor grace period logic can handle cleanup.
        // This avoids immediately failing runs when an agent has a brief network blip.
        var entry = _facade.GetByAgentId(message.AgentId);
        if (entry is { ActiveJobId: null })
        {
            var orphanedRuns = _facade.GetActiveRunsByAgent(message.AgentId);
            if (orphanedRuns.Count > 0)
            {
                // Re-check: another thread (DrainService) may have assigned a job between
                // the Register call and now. If so, don't overwrite.
                if (entry.ActiveJobId is not null)
                {
                    _logger.Information(
                        "Agent {AgentId} acquired job {ActiveJobId} between registration and orphan check, skipping orphan restoration",
                        message.AgentId, entry.ActiveJobId);
                }
                else
                {
                    // Restore the most recent orphaned run as the active job so the
                    // disconnect grace period timer applies. If the agent truly lost the job,
                    // the HeartbeatMonitor will fail it after the grace period expires.
                    var mostRecent = orphanedRuns[^1];
                    lock (entry.SyncRoot)
                    {
                        entry.ActiveJobId = mostRecent.RunId;
                        entry.OrphanRestoredAt = DateTimeOffset.UtcNow;
                    }
                    _facade.TransitionStatus(message.AgentId, AgentStatus.Busy);

                    _logger.Warning(
                        "Agent {AgentId} re-registered without active job but orchestrator tracks {OrphanCount} orphaned run(s). " +
                        "Restoring run {RunId} (issue {IssueIdentifier}) as active — HeartbeatMonitor will clean up if agent does not resume.",
                        message.AgentId, orphanedRuns.Count, mostRecent.RunId, mostRecent.IssueIdentifier);
                }
            }
            else
            {
                _logger.Information(
                    "Agent {AgentId} registered with no active job and no orphaned runs (status={Status})",
                    message.AgentId, entry.Status);
            }
        }
        else if (entry is { ActiveJobId: not null })
        {
            // Crash recovery detection: agent registered without an active job but the
            // registry already restored ActiveJobId (from its own prior state in the update factory).
            // This means the agent lost its in-memory state (container restart) while the orchestrator
            // still thinks it's working. Set OrphanRestoredAt so HeartbeatMonitor Phase 1.5 will
            // fail the run after the grace period if the agent doesn't report progress.
            if (message.ActiveJob is null && entry.OrphanRestoredAt is null)
            {
                lock (entry.SyncRoot)
                {
                    entry.OrphanRestoredAt = DateTimeOffset.UtcNow;
                }
                _logger.Warning(
                    "Agent {AgentId} re-registered without active job but orchestrator has {JobId} assigned (crash recovery). " +
                    "Setting OrphanRestoredAt — HeartbeatMonitor will fail run after grace period if agent does not resume.",
                    message.AgentId, entry.ActiveJobId);
            }
            else
            {
                _logger.Information(
                    "Agent {AgentId} registered with active job {ActiveJobId} (status={Status})",
                    message.AgentId, entry.ActiveJobId, entry.Status);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deregisters an agent from the registry.
    /// </summary>
    public Task DeregisterAgent(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
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

        var agent = _facade.GetByAgentId(agentId);
        if (agent is null)
        {
            _logger.Warning("AgentReady received for unknown agent {AgentId}", agentId);
            return Task.CompletedTask;
        }

        _logger.Information("Agent {AgentId} signaled ready", agentId);
        _facade.Signal();
        return Task.CompletedTask;
    }

    // ── Shared private helpers ──────────────────────────────────────────

    /// <summary>
    /// Swaps the agent label on the entity (issue or PR) using the appropriate provider.
    /// Routes based on <paramref name="targetKind"/>: Issue → IssueProviderConfigId, PullRequest → RepoProviderConfigId.
    /// </summary>
    private Task SwapLabelAsync(PipelineRun run, string newLabel, LabelTargetKind targetKind)
    {
        var providerConfigId = targetKind == LabelTargetKind.PullRequest
            ? run.RepoProviderConfigId
            : run.IssueProviderConfigId;

        return _labelSwapper.SwapLabelAsync(providerConfigId, run.IssueIdentifier, newLabel, targetKind, CancellationToken.None);
    }

    /// <summary>
    /// Posts a comment on the issue using the issue provider from the run's config.
    /// </summary>
    private async Task PostCommentViaIssueProviderAsync(PipelineRun run, string body)
    {
        try
        {
            var issueConfig = await _facade.GetProviderConfigByIdAsync(run.IssueProviderConfigId, ProviderKind.Issue, CancellationToken.None);
            if (issueConfig is null)
            {
                _logger.Warning("Issue provider config '{ConfigId}' not found for run {RunId}", run.IssueProviderConfigId, run.RunId);
                return;
            }

            await using var issueProvider = _facade.CreateIssueProvider(issueConfig);
            await issueProvider.PostCommentAsync(run.IssueIdentifier, body, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to post comment on issue {IssueIdentifier} for run {RunId}", run.IssueIdentifier, run.RunId);
        }
    }

    /// <summary>
    /// Determines the correct <see cref="LabelTargetKind"/> based on the run's <see cref="PipelineRun.RunType"/>.
    /// </summary>
    private static LabelTargetKind GetLabelTargetKind(PipelineRun run)
        => run.RunType == PipelineRunType.Review ? LabelTargetKind.PullRequest : LabelTargetKind.Issue;

    /// <summary>
    /// Posts issue-level feedback as a comment on the GitHub issue if present.
    /// Non-fatal: logs warning on failure and continues.
    /// </summary>
    private async Task PostIssueFeedbackCommentAsync(PipelineRun run)
    {
        try
        {
            var comment = FeedbackCommentFormatter.FormatComment(run.Feedback?.Issue);
            if (comment is null)
                return;

            await PostCommentViaIssueProviderAsync(run, comment);
            _logger.Information("Posted issue feedback comment for run {RunId} on issue {IssueIdentifier}",
                run.RunId, run.IssueIdentifier);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to post issue feedback comment for run {RunId} on issue {IssueIdentifier}",
                run.RunId, run.IssueIdentifier);
        }
    }
}
