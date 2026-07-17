using System.Text.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgentWebUI.Hubs;

public sealed partial class AgentHub
{
    // ── Job lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Agent acknowledges job acceptance. Transitions agent to Busy and WorkItem to Running.
    /// </summary>
    [RequiresActiveJob]
    public async Task JobAccepted(JobId jobId)
    {
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        await _lifecycleService.HandleJobAcceptedAsync(jobId, agent, CancellationToken.None);
    }

    /// <summary>
    /// Agent rejects a job. Cleans up the orphaned run and reverts the label so the
    /// pipeline loop can re-discover and re-dispatch the issue.
    /// This should be rare after the atomic agent reservation fix in SelectAgent.
    /// </summary>
    [RequiresActiveJob]
    public async Task JobRejected(JobId jobId, string reason)
    {
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        await _lifecycleService.HandleJobRejectedAsync(jobId, agent, reason, CancellationToken.None);
    }

    /// <summary>
    /// Agent reports job completion. Updates the PipelineRun, persists to history,
    /// transitions agent to Idle, and signals the drain service for next dispatch.
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportJobCompleted(JobId jobId, JobCompletionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        await _lifecycleService.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);
    }

    // ── Real-time status ────────────────────────────────────────────────

    /// <summary>
    /// Updates the PipelineRun's CurrentStep and HighWaterMark, applies optional step metadata, notifies UI.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportStepTransition(JobId jobId, PipelineStep step, DateTimeOffset timestamp, Dictionary<string, string>? metadata = null)
    {
        _lifecycleService.HandleStepTransition(jobId, step, timestamp, metadata);

        // Clear orphan-restored flag: agent is actively progressing on this job
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        if (agent is { OrphanRestoredAt: not null })
        {
            _logger.Information(
                "Agent {AgentId} reported progress on job {JobId}, clearing orphan-restored state",
                agent.AgentId, jobId.Value);
            agent.OrphanRestoredAt = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reports the result of brain repository synchronization so the UI can display context status.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportBrainSyncResult(JobId jobId, bool contextLoaded, int knowledgeFileCount)
    {
        var run = _facade.GetRun(jobId.Value);
        if (run is not null)
        {
            run.BrainContextLoaded = contextLoaded;
            run.BrainKnowledgeFileCount = knowledgeFileCount;
            _logger.Debug("Job {JobId} brain sync result: loaded={Loaded}, files={FileCount}",
                jobId.Value, contextLoaded, knowledgeFileCount);
            _orchestration.NotifyChange();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Enqueues output lines into the run's OutputRingBuffer and the run's OutputLines queue.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportOutputLines(JobId jobId, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var buffer = _facade.GetOutputBuffer(jobId.Value);
        buffer.AddRange(lines);

        // Also add to the run's OutputLines for UI streaming
        var run = _facade.GetRun(jobId.Value);
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
    public Task ReportChatEntry(JobId jobId, ChatRole role, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var run = _facade.GetRun(jobId.Value);
        run?.ChatHistory.Enqueue(new ChatEntry { Role = role, Content = content, Timestamp = DateTime.UtcNow });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Updates the run's quality gate report and history.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportQualityGateResult(JobId jobId, QualityGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var run = _facade.GetRun(jobId.Value);
        if (run is not null)
        {
            run.LatestQualityReport = report;
            run.QualityGateHistory.Enqueue(report);
            _logger.Information("Job {JobId} quality gate result received", jobId.Value);
        }

        return Task.CompletedTask;
    }

    // ── Issue operations (proxied through orchestrator) ─────────────────

    /// <summary>
    /// Formats and posts a comment on the GitHub issue via <see cref="IIssueProvider"/>.
    /// Uses existing comment formatters based on <paramref name="commentType"/>.
    /// </summary>
    [RequiresActiveJob]
    public async Task RequestPostComment(JobId jobId, CommentType commentType, CommentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var run = _facade.GetRun(jobId.Value);
        if (run is null)
        {
            _logger.Warning("RequestPostComment for unknown run {JobId}", jobId.Value);
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
    /// Executes a label swap on the entity (issue or PR) via <see cref="ILabelSwapper"/>.
    /// Routes to the correct provider based on <paramref name="targetKind"/>.
    /// </summary>
    [RequiresActiveJob]
    public async Task RequestLabelChange(JobId jobId, string newLabel, int targetKind = 0)
    {
        ArgumentNullException.ThrowIfNull(newLabel);

        var run = _facade.GetRun(jobId.Value);
        if (run is null)
        {
            _logger.Warning("RequestLabelChange for unknown run {JobId}", jobId.Value);
            return;
        }

        if (!string.IsNullOrEmpty(newLabel) && !AgentLabels.All.Contains(newLabel))
        {
            _logger.Warning("Agent requested invalid label '{Label}' for job {JobId}, ignoring", newLabel, jobId.Value);
            return;
        }

        // Derive targetKind from the run's RunType rather than trusting the caller-supplied value.
        // This prevents a buggy or compromised agent from routing label operations to the wrong entity.
        var kind = GetLabelTargetKind(run);

        _logger.Information(
            "RequestLabelChange: job {JobId} requesting label {Label} for issue {IssueIdentifier} (agent={AgentId}, currentStep={CurrentStep})",
            jobId.Value, newLabel, run.IssueIdentifier, run.AgentId, run.CurrentStep);

        await SwapLabelAsync(run, newLabel, kind);
    }

    // ── Token refresh ───────────────────────────────────────────────────

    /// <summary>
    /// Generates a fresh short-lived token via <see cref="ITokenVendingService"/>.
    /// Supports both SignalR mode (PipelineRun in memory) and K8s mode (WorkItem payload in DB).
    /// </summary>
    [RequiresActiveJob]
    public async Task<TokenRefreshResponse> RequestTokenRefresh(JobId jobId, ProviderKind providerKind)
    {
        // Resolve provider config IDs — from PipelineRun (SignalR mode) or WorkItem payload (K8s mode)
        string? repoProviderConfigId;
        string? brainProviderConfigId;

        var run = _facade.GetRun(jobId.Value);
        if (run is not null)
        {
            repoProviderConfigId = run.RepoProviderConfigId;
            brainProviderConfigId = run.BrainProviderConfigId;
        }
        else
        {
            // K8s mode fallback: resolve from WorkItem payload in DB
            var configIds = await _facade.GetWorkItemProviderConfigIdsAsync(jobId.Value, CancellationToken.None);
            if (configIds is null)
            {
                _logger.Warning("No active run or work item found for job {JobId}", jobId.Value);
                throw new HubException($"No active run or work item found for job {jobId.Value}");
            }

            repoProviderConfigId = configIds.Value.RepoProviderConfigId;
            brainProviderConfigId = configIds.Value.BrainProviderConfigId;

            if (string.IsNullOrEmpty(repoProviderConfigId))
            {
                _logger.Warning("WorkItem {JobId} has no repoProviderConfigId in payload", jobId.Value);
                throw new HubException($"WorkItem {jobId.Value} has no repoProviderConfigId in payload");
            }
        }

        // Resolve the correct provider config based on the requested kind.
        // Brain repos need their own scoped token (different repository scope).
        ProviderConfig? targetConfig = null;

        if (providerKind == ProviderKind.Brain && !string.IsNullOrEmpty(brainProviderConfigId))
        {
            targetConfig = await _facade.GetProviderConfigByIdAsync(brainProviderConfigId, ProviderKind.Repository, CancellationToken.None);
        }

        if (targetConfig is null)
        {
            // Default: use the work repo config (covers Repository kind and fallback)
            targetConfig = await _facade.GetProviderConfigByIdAsync(repoProviderConfigId!, ProviderKind.Repository, CancellationToken.None);
        }

        if (targetConfig is null)
        {
            _logger.Warning("Provider config not found for job {JobId} (kind: {ProviderKind})", jobId.Value, providerKind);
            throw new HubException($"Provider config not found for job {jobId.Value} (kind: {providerKind})");
        }

        // GitHub App auth: generate a short-lived scoped token via JWT exchange
        if (targetConfig.Settings.ContainsKey(ProviderSettingKeys.PrivateKeyBase64))
        {
            var (token, expiresAt) = await _tokenVending.GenerateAgentTokenAsync(targetConfig, CancellationToken.None);

            _logger.Information("Token refreshed for job {JobId} (kind: {ProviderKind}), expires at {ExpiresAt}",
                jobId.Value, providerKind, expiresAt);

            return new TokenRefreshResponse { Token = token, ExpiresAt = expiresAt };
        }

        // GitLab PAT / static token: return the access token directly (no vending needed)
        if (targetConfig.Settings.TryGetValue(ProviderSettingKeys.AccessToken, out var accessToken)
            && !string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.Information("Returning static access token for job {JobId} (kind: {ProviderKind})",
                jobId.Value, providerKind);

            // Use a far-future expiry since PATs don't expire through this mechanism
            return new TokenRefreshResponse { Token = accessToken, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        }

        // Fallback: check if a pre-vended token already exists in settings
        if (targetConfig.Settings.TryGetValue(ProviderSettingKeys.Token, out var existingToken)
            && !string.IsNullOrWhiteSpace(existingToken))
        {
            _logger.Information("Returning existing token for job {JobId} (kind: {ProviderKind})",
                jobId.Value, providerKind);

            return new TokenRefreshResponse { Token = existingToken, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        }

        _logger.Warning("Provider config for job {JobId} (kind: {ProviderKind}) has no supported authentication method", jobId.Value, providerKind);
        throw new HubException($"Provider config for job {jobId.Value} (kind: {providerKind}) has no supported authentication method. " +
            "Expected 'privateKeyBase64' (GitHub App), 'accessToken' (GitLab PAT), or 'token'.");
    }

    // ── Decomposition issue operations (proxied through orchestrator) ──

    /// <summary>
    /// Creates a new issue via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.CreateIssueAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public Task<CreatedIssueResult> RequestCreateIssue(JobId jobId, string title, string body, IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(labels);

        return ExecuteWithIssueProviderAsync<CreatedIssueResult>(jobId.Value, "create issue",
            (provider, ct) => provider.CreateIssueAsync(title, body, labels, ct));
    }

    /// <summary>
    /// Creates a new issue via a specific issue provider (for cross-repo decomposition routing).
    /// Called by the agent's <c>OrchestratorProxy.CreateIssueForProviderAsync</c> when the
    /// decomposed issue's <c>targetRepository</c> resolves to a different template's issue provider.
    /// </summary>
    [RequiresActiveJob]
    public async Task<CreatedIssueResult> RequestCreateIssueForProvider(
        JobId jobId, string issueProviderConfigId, string title, string body, IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(issueProviderConfigId);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(labels);

        var run = _facade.GetRun(jobId.Value);
        if (run is null)
            throw new HubException($"No active run found for job {jobId.Value}");

        var issueConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == issueProviderConfigId);
        if (issueConfig is null)
            throw new HubException($"Issue provider config '{issueProviderConfigId}' not found for cross-repo routing in job {jobId.Value}");

        await using var issueProvider = _facade.CreateIssueProvider(issueConfig);
        try
        {
            return await issueProvider.CreateIssueAsync(title, body, labels, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "RequestCreateIssueForProvider failed for job {JobId}, provider {ProviderId}",
                jobId.Value, issueProviderConfigId);
            throw new HubException($"Failed to create issue for job {jobId.Value} via provider {issueProviderConfigId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Lists open issues with optional label filtering via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.ListOpenIssuesAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public Task<PagedResult<IssueSummary>> RequestListOpenIssues(JobId jobId, int page, int pageSize, IReadOnlyList<string>? labels)
    {
        return ExecuteWithIssueProviderAsync<PagedResult<IssueSummary>>(jobId.Value, "list open issues",
            (provider, ct) => provider.ListOpenIssuesAsync(page, pageSize, labels, ct));
    }

    /// <summary>
    /// Lists closed issues with optional label filtering and date cutoff via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.ListClosedIssuesAsync</c> during decomposition runs
    /// to include recently-closed sibling issues in agent context.
    /// </summary>
    [RequiresActiveJob]
    public Task<PagedResult<IssueSummary>> RequestListClosedIssues(JobId jobId, int page, int pageSize, IReadOnlyList<string>? labels, DateTime? since)
    {
        return ExecuteWithIssueProviderAsync<PagedResult<IssueSummary>>(jobId.Value, "list closed issues",
            (provider, ct) => provider.ListClosedIssuesAsync(page, pageSize, labels, since, ct));
    }

    /// <summary>
    /// Gets full issue details by identifier via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.GetIssueAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public Task<IssueDetail> RequestGetIssue(JobId jobId, string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        return ExecuteWithIssueProviderAsync<IssueDetail>(jobId.Value, $"get issue '{identifier}'",
            (provider, ct) => provider.GetIssueAsync(identifier, ct));
    }

    /// <summary>
    /// Lists all comments on an issue via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.ListCommentsAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public Task<IReadOnlyList<IssueComment>> RequestListComments(JobId jobId, string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        return ExecuteWithIssueProviderAsync<IReadOnlyList<IssueComment>>(jobId.Value, $"list comments for issue '{identifier}'",
            (provider, ct) => provider.ListCommentsAsync(identifier, ct));
    }

    /// <summary>
    /// Updates an existing comment by ID via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.UpdateCommentAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public Task RequestUpdateComment(JobId jobId, string issueId, string commentId, string body)
    {
        ArgumentNullException.ThrowIfNull(issueId);
        ArgumentNullException.ThrowIfNull(commentId);
        ArgumentNullException.ThrowIfNull(body);

        return ExecuteWithIssueProviderAsync(jobId.Value, $"update comment '{commentId}' on issue '{issueId}'",
            (provider, ct) => provider.UpdateCommentAsync(issueId, commentId, body, ct));
    }

    // ── Pipeline-local private helpers ──────────────────────────────────

    // TODO: Add unit tests for ExecuteWithIssueProviderAsync to verify error-handling behavior
    // (wrapping exceptions as HubException), proper disposal of the provider on failure,
    // and correct propagation of the cancellation token to the delegate.

    /// <summary>
    /// Executes an issue provider operation with standard resolve/dispose/error-handling boilerplate.
    /// </summary>
    private async Task<T> ExecuteWithIssueProviderAsync<T>(
        string jobId,
        string operationName,
        Func<IIssueProvider, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        var (_, issueProvider) = await ResolveIssueProviderForRunAsync(jobId);
        await using (issueProvider)
        {
            try
            {
                return await operation(issueProvider, ct);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "{Operation} failed for job {JobId}", operationName, jobId);
                throw new HubException($"Failed to {operationName} for job {jobId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Executes a void issue provider operation with standard resolve/dispose/error-handling boilerplate.
    /// </summary>
    private async Task ExecuteWithIssueProviderAsync(
        string jobId,
        string operationName,
        Func<IIssueProvider, CancellationToken, Task> operation,
        CancellationToken ct = default)
    {
        var (_, issueProvider) = await ResolveIssueProviderForRunAsync(jobId);
        await using (issueProvider)
        {
            try
            {
                await operation(issueProvider, ct);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "{Operation} failed for job {JobId}", operationName, jobId);
                throw new HubException($"Failed to {operationName} for job {jobId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Resolves the <see cref="IIssueProvider"/> for the given job's run configuration.
    /// Validates the job ID, finds the run, loads the issue provider config, and creates the provider.
    /// </summary>
    /// <exception cref="HubException">Thrown when the job ID is invalid or the provider config is not found.</exception>
    private async Task<(PipelineRun Run, IIssueProvider Provider)> ResolveIssueProviderForRunAsync(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        var run = _facade.GetRun(jobId);
        if (run is null)
            throw new HubException($"No active run found for job {jobId}");

        var issueConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == run.IssueProviderConfigId);
        if (issueConfig is null)
            throw new HubException($"Issue provider config '{run.IssueProviderConfigId}' not found for job {jobId}");

        return (run, _facade.CreateIssueProvider(issueConfig));
    }

    /// <summary>
    /// Builds a gate comment (not-ready or wont-do) from the assessment JSON.
    /// Falls back to the raw JSON if deserialization fails.
    /// </summary>
    /// <remarks>
    /// Currently only invoked via <see cref="RequestPostComment"/> when the agent sends
    /// <see cref="CommentType.GateRejection"/> or <see cref="CommentType.GateWontDo"/>.
    /// In practice, <see cref="CodingAgentWebUI.Pipeline.Services.AgentPhaseExecutor"/>
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
                    ? Pipeline.Services.AgentPhaseExecutor.BuildWontDoComment(assessment)
                    : Pipeline.Services.AgentPhaseExecutor.BuildNotReadyComment(assessment);
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
}
