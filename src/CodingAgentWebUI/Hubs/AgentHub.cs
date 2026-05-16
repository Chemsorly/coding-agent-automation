using System.Text.Json;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
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
public sealed class AgentHub : Hub<IAgentHubClient>, IAgentHub
{
    private readonly IAgentHubFacade _facade;
    private readonly ITokenVendingService _tokenVending;
    private readonly PipelineOrchestrationService _orchestration;
    private readonly ModelFetchService _modelFetchService;
    private readonly IConsolidationService _consolidationService;
    private readonly ConsolidationBadgeService _badgeService;
    private readonly IIssueProviderLabelSwapper _labelSwapper;
    private readonly ILogger _logger;

    public AgentHub(
        IAgentHubFacade facade,
        ITokenVendingService tokenVending,
        PipelineOrchestrationService orchestration,
        ModelFetchService modelFetchService,
        IConsolidationService consolidationService,
        ConsolidationBadgeService badgeService,
        IIssueProviderLabelSwapper labelSwapper,
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

    // ── Job lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Agent acknowledges job acceptance. Transitions agent to Busy.
    /// </summary>
    [RequiresActiveJob]
    public Task JobAccepted(string jobId)
    {
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        if (agent is not null)
        {
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Busy);
            _logger.Information("Agent {AgentId} accepted job {JobId}", agent.AgentId, jobId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Agent rejects a job. Orchestrator should select a different agent or re-queue.
    /// </summary>
    public Task JobRejected(string jobId, string reason)
    {
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        _logger.Warning("Agent {AgentId} rejected job {JobId}: {Reason}", agent?.AgentId, jobId, reason);

        // Transition agent back to Idle
        if (agent is not null)
        {
            agent.ActiveJobId = null;
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);

            // Signal drain service — agent is idle and may pick up a different job
            _facade.Signal();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Agent reports job completion. Updates the PipelineRun, persists to history,
    /// transitions agent to Idle, and signals the drain service for next dispatch.
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportJobCompleted(string jobId, JobCompletionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        var run = _facade.GetRun(jobId);

        if (run is not null)
        {
            // Update run with completion data
            run.CurrentStep = payload.FinalStep;
            run.CompletedAt = payload.CompletedAt.UtcDateTime;
            run.FailureReason = payload.FailureReason;
            run.PullRequestUrl = payload.PullRequestUrl;
            run.PullRequestNumber = payload.PullRequestNumber;
            run.IsDraftPr = payload.IsDraftPr;
            run.RetryCount = payload.RetryCount;
            run.FilesChangedCount = payload.FilesChangedCount;
            run.LinesAdded = payload.LinesAdded;
            run.LinesRemoved = payload.LinesRemoved;
            run.BrainUpdatesPushed = payload.BrainUpdatesPushed;
            run.AnalysisRecommendation = payload.AnalysisRecommendation;
            run.AnalysisConcerns = payload.AnalysisConcerns;
            run.AnalysisBlockingIssues = payload.AnalysisBlockingIssues;
            run.BlacklistedFilesDetected = payload.BlacklistedFilesDetected;
            run.CodeReviewAgentsRun = payload.CodeReviewAgentsRun;
            Interlocked.Exchange(ref run.CodeReviewCriticalCount, payload.CodeReviewCriticalCount);
            Interlocked.Exchange(ref run.CodeReviewWarningCount, payload.CodeReviewWarningCount);
            Interlocked.Exchange(ref run.CodeReviewSuggestionCount, payload.CodeReviewSuggestionCount);
            run.Feedback = payload.Feedback;
            run.TotalTokens = payload.TotalTokens;
            run.TotalCost = payload.TotalCost;

            // Persist to history and remove from active runs
            _facade.AddRunToHistory(run);
            _facade.RemoveRun(jobId);

            _logger.Information(
                "Job {JobId} completed: step={FinalStep}, PR={PullRequestUrl}",
                jobId, payload.FinalStep, payload.PullRequestUrl ?? "none");

            _orchestration.NotifyChange();

            // Swap label based on final outcome (non-fatal).
            // The agent may also attempt a label swap via RequestLabelChange during its own
            // error handling, but that call can race with this handler (run already removed).
            // This is the authoritative swap that guarantees correctness.
            if (payload.FinalStep == PipelineStep.Failed)
            {
                await SwapLabelViaIssueProviderAsync(run, AgentLabels.Error);
            }
            else if (payload.FinalStep == PipelineStep.Completed)
            {
                await SwapLabelViaIssueProviderAsync(run, AgentLabels.Done);
            }
            else if (payload.FinalStep == PipelineStep.Cancelled)
            {
                await SwapLabelViaIssueProviderAsync(run, AgentLabels.Cancelled);
            }

            // Post issue feedback comment if present (non-fatal)
            await PostIssueFeedbackCommentAsync(run);
        }

        // Transition agent to Idle and clear active job
        if (agent is not null)
        {
            agent.ActiveJobId = null;
            agent.LastJobCompletedAt = DateTimeOffset.UtcNow;
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);

            // Mark issue as no longer processing in the dispatcher
            if (run is not null)
                _facade.MarkIssueComplete(run.IssueIdentifier);

            // Signal the drain service to attempt dispatch for this now-idle agent
            _facade.Signal();
        }
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

    // ── Real-time status ────────────────────────────────────────────────

    /// <summary>
    /// Updates the PipelineRun's CurrentStep and HighWaterMark, notifies UI.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportStepTransition(string jobId, PipelineStep step, DateTimeOffset timestamp)
    {
        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            run.CurrentStep = step;

            // Update HighWaterMark — only advance, never go backward
            // Exclude terminal states (Completed, Failed, Cancelled) from high water mark
            if (step < PipelineStep.Completed && step > run.HighWaterMark)
                run.HighWaterMark = step;

            _logger.Debug("Job {JobId} step transition → {Step}", jobId, step);
            _orchestration.NotifyChange();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reports the result of brain repository synchronization so the UI can display context status.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportBrainSyncResult(string jobId, bool contextLoaded, int knowledgeFileCount)
    {
        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            run.BrainContextLoaded = contextLoaded;
            run.BrainKnowledgeFileCount = knowledgeFileCount;
            _logger.Debug("Job {JobId} brain sync result: loaded={Loaded}, files={FileCount}",
                jobId, contextLoaded, knowledgeFileCount);
            _orchestration.NotifyChange();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Enqueues output lines into the run's OutputRingBuffer and the run's OutputLines queue.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportOutputLines(string jobId, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var buffer = _facade.GetOutputBuffer(jobId);
        buffer.AddRange(lines);

        // Also add to the run's OutputLines for UI streaming
        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            foreach (var line in lines)
                run.OutputLines.Enqueue(line);

            _orchestration.NotifyChange();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a chat entry to the run's chat history.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportChatEntry(string jobId, ChatRole role, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var run = _facade.GetRun(jobId);
        run?.ChatHistory.Enqueue(new ChatEntry { Role = role, Content = content, Timestamp = DateTime.UtcNow });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Updates the run's quality gate report and history.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportQualityGateResult(string jobId, QualityGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            run.LatestQualityReport = report;
            run.QualityGateHistory.Enqueue(report);
            _logger.Information("Job {JobId} quality gate result received", jobId);
        }

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

    // ── Issue operations (proxied through orchestrator) ─────────────────

    /// <summary>
    /// Formats and posts a comment on the GitHub issue via <see cref="IIssueProvider"/>.
    /// Uses existing comment formatters based on <paramref name="commentType"/>.
    /// </summary>
    [RequiresActiveJob]
    public async Task RequestPostComment(string jobId, CommentType commentType, CommentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var run = _facade.GetRun(jobId);
        if (run is null)
        {
            _logger.Warning("RequestPostComment for unknown run {JobId}", jobId);
            return;
        }

        string commentBody;
        switch (commentType)
        {
            case CommentType.Analysis:
                commentBody = payload.AnalysisMarkdown ?? string.Empty;
                break;

            case CommentType.GateRejection:
                commentBody = BuildGateComment(payload.AssessmentJson, isWontDo: false);
                break;

            case CommentType.GateWontDo:
                commentBody = BuildGateComment(payload.AssessmentJson, isWontDo: true);
                break;

            default:
                _logger.Warning("Unknown comment type {CommentType} for job {JobId}", commentType, jobId);
                return;
        }

        await PostCommentViaIssueProviderAsync(run, commentBody);
    }

    /// <summary>
    /// Executes a label swap on the issue via <see cref="IIssueProvider"/>.
    /// </summary>
    [RequiresActiveJob]
    public async Task RequestLabelChange(string jobId, string newLabel)
    {
        ArgumentNullException.ThrowIfNull(newLabel);

        var run = _facade.GetRun(jobId);
        if (run is null)
        {
            _logger.Warning("RequestLabelChange for unknown run {JobId}", jobId);
            return;
        }

        await SwapLabelViaIssueProviderAsync(run, newLabel);
    }

    // ── Token refresh ───────────────────────────────────────────────────

    /// <summary>
    /// Generates a fresh short-lived token via <see cref="ITokenVendingService"/>.
    /// </summary>
    [RequiresActiveJob]
    public async Task<TokenRefreshResponse> RequestTokenRefresh(string jobId, ProviderKind providerKind)
    {
        var run = _facade.GetRun(jobId);
        if (run is null)
            throw new HubException($"No active run found for job {jobId}");

        // Resolve the correct provider config based on the requested kind.
        // Brain repos need their own scoped token (different repository scope).
        ProviderConfig? targetConfig = null;

        if (providerKind == ProviderKind.Brain && !string.IsNullOrEmpty(run.BrainProviderConfigId))
        {
            var repoConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
            targetConfig = repoConfigs.FirstOrDefault(c => c.Id == run.BrainProviderConfigId);
        }

        if (targetConfig is null)
        {
            // Default: use the work repo config (covers Repository kind and fallback)
            var repoConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
            targetConfig = repoConfigs.FirstOrDefault(c => c.Id == run.RepoProviderConfigId);
        }

        if (targetConfig is null)
            throw new HubException($"Provider config not found for job {jobId} (kind: {providerKind})");

        var (token, expiresAt) = await _tokenVending.GenerateAgentTokenAsync(targetConfig, CancellationToken.None);

        _logger.Information("Token refreshed for job {JobId} (kind: {ProviderKind}), expires at {ExpiresAt}",
            jobId, providerKind, expiresAt);

        return new TokenRefreshResponse { Token = token, ExpiresAt = expiresAt };
    }

    // ── Interactive chat ─────────────────────────────────────────────────

    /// <summary>
    /// Receives streamed chat response lines from an agent during interactive chat.
    /// Broadcasts to the orchestration service for UI consumption.
    /// </summary>
    public Task ReportChatResponse(ChatResponseMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        _logger.Debug("Chat response received for session {SessionId}: {LineCount} lines",
            message.SessionId, message.Lines.Count);

        _orchestration.NotifyChatResponse(message.SessionId, message.Lines);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Signals that a chat prompt execution has completed on the agent.
    /// Does NOT transition the agent to Idle — the chat session remains active
    /// until the orchestrator sends CancelChat (End Chat / navigate away).
    /// </summary>
    public Task ReportChatCompleted(ChatCompletedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        if (agent is not null)
        {
            _logger.Information("Chat prompt completed for session {SessionId} on agent {AgentId} (exit={ExitCode})",
                message.SessionId, agent.AgentId, message.ExitCode);
        }

        _orchestration.NotifyChatCompleted(message.SessionId, message.ExitCode, message.Error);
        return Task.CompletedTask;
    }

    // ── Model fetch ─────────────────────────────────────────────────────

    /// <summary>
    /// Receives the result of a FetchModels request from an agent.
    /// </summary>
    public Task ReportFetchModelsResult(FetchModelsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        _modelFetchService.CompleteRequest(response);
        return Task.CompletedTask;
    }

    // ── Consolidation ───────────────────────────────────────────────────

    /// <summary>
    /// Agent reports consolidation job completion. Updates the consolidation run status,
    /// persists harness suggestions if present, and increments badge count for refactoring issues.
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportConsolidationComplete(ConsolidationJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        _logger.Information("Consolidation job {JobId} completed by agent {AgentId}: success={Success}",
            result.JobId, agent?.AgentId, result.Success);

        // Update the consolidation run status
        try
        {
            var status = result.Success
                ? Pipeline.Models.ConsolidationRunStatus.Succeeded
                : Pipeline.Models.ConsolidationRunStatus.Failed;
            var summary = result.Success ? result.Summary : result.ErrorMessage;

            // WARNING 9: CancellationToken.None is intentional here — these are fast file I/O
            // operations that should complete even if the agent connection drops. The consolidation
            // run state must be persisted regardless of connection lifecycle.
            await _consolidationService.UpdateRunAsync(result.JobId, status, summary, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update consolidation run {JobId} status", result.JobId);
        }

        // For harness suggestions: persist the suggestions file
        if (result.HarnessSuggestions is not null)
        {
            try
            {
                // CancellationToken.None: same rationale as above — suggestions must be persisted
                await _consolidationService.SaveHarnessSuggestionsAsync(result.HarnessSuggestions, CancellationToken.None);
                _logger.Information("Persisted harness suggestions from consolidation job {JobId}", result.JobId);

                // Increment badge count for harness suggestions
                _badgeService.IncrementBy(result.HarnessSuggestions.Suggestions.Count);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to persist harness suggestions for consolidation job {JobId}", result.JobId);
            }
        }

        // For refactoring: increment badge count for created issues
        if (result.CreatedIssues is { Count: > 0 })
        {
            _badgeService.IncrementBy(result.CreatedIssues.Count);
            _logger.Information("Refactoring consolidation job {JobId} created {Count} issue(s)",
                result.JobId, result.CreatedIssues.Count);
        }

        // Transition agent back to Idle
        if (agent is not null)
        {
            agent.ActiveJobId = null;
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);
            _facade.Signal();
        }

        _orchestration.NotifyChange();
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Builds a gate comment (not-ready or wont-do) from the assessment JSON.
    /// Falls back to the raw JSON if deserialization fails.
    /// </summary>
    /// <remarks>
    /// Currently only invoked via <see cref="RequestPostComment"/> when the agent sends
    /// <see cref="CommentType.GateRejection"/> or <see cref="CommentType.GateWontDo"/>.
    /// In practice, <see cref="CodingAgentWebUI.Pipeline.Services.AgentExecutionOrchestrator"/>
    /// formats gate comments locally and posts via <see cref="CommentType.Analysis"/>,
    /// so this path is currently unused.
    /// </remarks>
    private string BuildGateComment(string? assessmentJson, bool isWontDo)
    {
        if (string.IsNullOrWhiteSpace(assessmentJson))
            return isWontDo ? "## 🚫 Analysis Gate: Won't Do" : "## ⚠️ Analysis Gate: Needs Refinement";

        try
        {
            var assessment = JsonSerializer.Deserialize<AnalysisAssessment>(assessmentJson);
            if (assessment is not null)
            {
                return isWontDo
                    ? Pipeline.Services.AgentExecutionOrchestrator.BuildWontDoComment(assessment)
                    : Pipeline.Services.AgentExecutionOrchestrator.BuildNotReadyComment(assessment);
            }
        }
        catch (JsonException ex)
        {
            _logger.Warning(ex, "Failed to deserialize assessment JSON for gate comment");
        }

        // Fallback: wrap raw JSON in a code block
        return isWontDo
            ? $"## 🚫 Analysis Gate: Won't Do\n\n```json\n{assessmentJson}\n```"
            : $"## ⚠️ Analysis Gate: Needs Refinement\n\n```json\n{assessmentJson}\n```";
    }

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

    /// <summary>
    /// Posts a comment on the issue using the issue provider from the run's config.
    /// </summary>
    private async Task PostCommentViaIssueProviderAsync(PipelineRun run, string body)
    {
        try
        {
            var issueConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
            var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == run.IssueProviderConfigId);
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
    /// Swaps the agent label on the issue using the issue provider from the run's config.
    /// </summary>
    private Task SwapLabelViaIssueProviderAsync(PipelineRun run, string newLabel)
    {
        return _labelSwapper.SwapLabelAsync(run.IssueProviderConfigId, run.IssueIdentifier, newLabel, CancellationToken.None);
    }

}
