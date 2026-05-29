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
                "Agent {AgentId} disconnected (connectionId={ConnectionId}, exception={Exception})",
                agent.AgentId, Context.ConnectionId, exception?.Message ?? "none");
        }

        return base.OnDisconnectedAsync(exception);
    }

    // ── Registration ────────────────────────────────────────────────────

    /// <summary>
    /// Registers an agent in the registry. Validates that the <c>agentId</c> in the message
    /// matches the <c>agentId</c> query parameter from the connection.
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

        _facade.Register(message, Context.ConnectionId);
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
    /// </summary>
    public Task Heartbeat(HeartbeatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _facade.UpdateHeartbeat(message.AgentId, message.Timestamp);
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
