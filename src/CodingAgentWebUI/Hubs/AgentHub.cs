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
    private readonly IRunLifecycleManager _lifecycleManager;
    private readonly ILogger _logger;

    public AgentHub(
        IAgentHubFacade facade,
        ITokenVendingService tokenVending,
        PipelineOrchestrationService orchestration,
        ModelFetchService modelFetchService,
        IConsolidationService consolidationService,
        ConsolidationBadgeService badgeService,
        ILabelSwapper labelSwapper,
        IRunLifecycleManager lifecycleManager,
        ILogger logger)
    {
        _facade = facade;
        _tokenVending = tokenVending;
        _orchestration = orchestration;
        _modelFetchService = modelFetchService;
        _consolidationService = consolidationService;
        _badgeService = badgeService;
        _labelSwapper = labelSwapper;
        _lifecycleManager = lifecycleManager;
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
                    var restoredRun = PipelineRun.Create(
                        runId: message.ActiveJob.RunId,
                        issueIdentifier: message.ActiveJob.IssueIdentifier,
                        issueTitle: message.ActiveJob.IssueTitle,
                        issueProviderConfigId: message.ActiveJob.IssueProviderConfigId,
                        repoProviderConfigId: message.ActiveJob.RepoProviderConfigId,
                        runType: message.ActiveJob.RunType,
                        startedAt: message.ActiveJob.StartedAt,
                        initiatedBy: message.ActiveJob.InitiatedBy,
                        agentId: message.AgentId,
                        agentProviderConfigId: message.ActiveJob.AgentProviderConfigId,
                        brainProviderConfigId: message.ActiveJob.BrainProviderConfigId);
                    restoredRun.CurrentStep = message.ActiveJob.CurrentStep;
                    restoredRun.PipelineProviderConfigId = message.ActiveJob.PipelineProviderConfigId;
                    restoredRun.ResolvedProfileId = message.ActiveJob.ResolvedProfileId;
                    restoredRun.ProjectId = message.ActiveJob.ProjectId;
                    restoredRun.ProjectName = message.ActiveJob.ProjectName;
                    restoredRun.RepositoryName = message.ActiveJob.RepositoryName;
                    restoredRun.ModelName = message.ActiveJob.ModelName;

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
                // Run already exists in-memory (e.g., created by K8s DispatchService with AgentId=null).
                // Ensure the agent is linked to it and transitioned to Busy.
                if (existingRun.AgentId is null)
                    existingRun.AgentId = message.AgentId;

                var trackedEntry = _facade.GetByAgentId(message.AgentId);
                if (trackedEntry is not null && trackedEntry.ActiveJobId is null)
                {
                    trackedEntry.ActiveJobId = message.ActiveJob.RunId;
                    _facade.TransitionStatus(message.AgentId, AgentStatus.Busy);
                }

                _logger.Debug("Agent {AgentId} active job {RunId} already tracked — linked agent to run",
                    message.AgentId, message.ActiveJob.RunId);
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
                // Restore the most recent orphaned run as the active job so the
                // disconnect grace period timer applies. If the agent truly lost the job,
                // the HeartbeatMonitor will fail it after the grace period expires.
                var mostRecent = orphanedRuns[^1];
                lock (entry.SyncRoot)
                {
                    // Atomic check-and-set under lock: if DrainService assigned a job
                    // between GetActiveRunsByAgent and this lock acquisition, don't overwrite.
                    if (entry.ActiveJobId is not null)
                    {
                        _logger.Information(
                            "Agent {AgentId} acquired job {ActiveJobId} between registration and orphan check, skipping orphan restoration",
                            message.AgentId, entry.ActiveJobId);
                    }
                    else
                    {
                        entry.ActiveJobId = mostRecent.RunId;
                        entry.OrphanRestoredAt = DateTimeOffset.UtcNow;
                    }
                }

                if (entry.ActiveJobId == mostRecent.RunId)
                {
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
    /// Returns the comment URL if available.
    /// </summary>
    private async Task<string?> PostCommentViaIssueProviderAsync(PipelineRun run, string body)
    {
        try
        {
            var issueConfig = await _facade.GetProviderConfigByIdAsync(run.IssueProviderConfigId, ProviderKind.Issue, CancellationToken.None);
            if (issueConfig is null)
            {
                _logger.Warning("Issue provider config '{ConfigId}' not found for run {RunId}", run.IssueProviderConfigId, run.RunId);
                return null;
            }

            await using var issueProvider = _facade.CreateIssueProvider(issueConfig);
            // Validate initializes provider state (e.g., GitLab PathWithNamespace) needed for URL construction
            await issueProvider.ValidateAsync(CancellationToken.None);
            return await issueProvider.PostCommentAsync(run.IssueIdentifier, body, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to post comment on issue {IssueIdentifier} for run {RunId}", run.IssueIdentifier, run.RunId);
            return null;
        }
    }

    /// <summary>
    /// Determines the correct <see cref="LabelTargetKind"/> based on the run's <see cref="PipelineRun.RunType"/>.
    /// </summary>
    private static LabelTargetKind GetLabelTargetKind(PipelineRun run)
        => run.RunType == PipelineRunType.Review ? LabelTargetKind.PullRequest : LabelTargetKind.Issue;

    /// <summary>
    /// Posts issue-level feedback as a comment on the GitHub issue if present.
    /// If a PR exists, appends a link to the feedback comment in the PR body.
    /// Non-fatal: logs warning on failure and continues.
    /// </summary>
    private async Task PostIssueFeedbackCommentAsync(PipelineRun run)
    {
        try
        {
            var comment = FeedbackCommentFormatter.FormatComment(run.Feedback?.Issue);
            if (comment is null)
                return;

            var commentUrl = await PostCommentViaIssueProviderAsync(run, comment);
            _logger.Information("Posted issue feedback comment for run {RunId} on issue {IssueIdentifier}",
                run.RunId, run.IssueIdentifier);

            // Append feedback link to PR body if we have both a URL and a PR
            if (commentUrl is not null && !string.IsNullOrEmpty(run.PullRequestNumber))
            {
                await AppendFeedbackLinkToPrBodyAsync(run, commentUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to post issue feedback comment for run {RunId} on issue {IssueIdentifier}",
                run.RunId, run.IssueIdentifier);
        }
    }

    /// <summary>
    /// Appends a feedback comment link section to the existing PR body.
    /// Fetches current body from provider to avoid stale-state overwrites.
    /// Idempotent: skips if feedback section already present.
    /// Non-fatal: logs warning on failure.
    /// </summary>
    private async Task AppendFeedbackLinkToPrBodyAsync(PipelineRun run, string commentUrl)
    {
        try
        {
            // Idempotency guard: don't append twice if retried
            if (run.PullRequestBody?.Contains("## Agent Feedback") == true)
            {
                _logger.Debug("Feedback link already present in PR body for run {RunId}, skipping", run.RunId);
                return;
            }

            var repoConfig = await _facade.GetProviderConfigByIdAsync(run.RepoProviderConfigId, ProviderKind.Repository, CancellationToken.None);
            if (repoConfig is null)
            {
                _logger.Warning("Repo provider config '{ConfigId}' not found for run {RunId}, skipping feedback link", run.RepoProviderConfigId, run.RunId);
                return;
            }

            if (!int.TryParse(run.PullRequestNumber, out var prNumber))
                return;

            await using var repoProvider = _facade.CreateRepositoryProvider(repoConfig);

            // Fetch current body from provider to avoid overwriting external edits
            var currentBody = await repoProvider.GetPullRequestBodyAsync(prNumber, CancellationToken.None)
                              ?? run.PullRequestBody
                              ?? "";

            // Double-check idempotency against remote body (may have been appended by a prior attempt)
            if (currentBody.Contains("## Agent Feedback"))
                return;

            var feedbackSection = $"\n\n## Agent Feedback\n⚠️ Agent posted feedback on the issue [here]({commentUrl}). Read before merging.";
            var newBody = currentBody + feedbackSection;

            await repoProvider.UpdatePullRequestAsync(prNumber, newBody, false, CancellationToken.None);
            run.PullRequestBody = newBody;

            _logger.Information("Appended feedback link to PR #{PrNumber} for run {RunId}", run.PullRequestNumber, run.RunId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to append feedback link to PR #{PrNumber} for run {RunId}", run.PullRequestNumber, run.RunId);
        }
    }
}
