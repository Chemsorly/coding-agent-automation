using System.Diagnostics;
using System.Text.Json;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR;
using OpenTelemetry.Trace;

namespace CodingAgentWebUI.Hubs;

public sealed partial class AgentHub
{
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
    /// Agent rejects a job. Cleans up the orphaned run and reverts the label so the
    /// pipeline loop can re-discover and re-dispatch the issue.
    /// This should be rare after the atomic agent reservation fix in SelectAgent.
    /// </summary>
    public async Task JobRejected(string jobId, string reason)
    {
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        _logger.Warning("Agent {AgentId} rejected job {JobId}: {Reason}", agent?.AgentId, jobId, reason);

        // Clean up the orphaned run so the issue can be re-dispatched
        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            _facade.RemoveRun(jobId);
            _facade.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

            // Revert label to agent:error — not agent:next to avoid infinite dispatch loops
            // in case of misconfiguration. Human intervention needed to retry.
            try
            {
                await SwapLabelAsync(run, AgentLabels.Error, GetLabelTargetKind(run));
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to revert label for rejected run {JobId} (issue {IssueIdentifier})",
                    jobId, run.IssueIdentifier);
            }

            _logger.Warning("Cleaned up rejected run {JobId} for issue {IssueIdentifier} (step={Step}, agent={AgentId}). " +
                "This indicates a dispatch race condition — investigate if recurring.",
                jobId, run.IssueIdentifier, run.CurrentStep, run.AgentId);
        }
        else
        {
            _logger.Warning("Agent rejected job {JobId} but no active run found — may have been cleaned up already", jobId);
        }

        // Transition agent back to Idle (it may still be marked Busy from reservation)
        if (agent is not null)
        {
            agent.ActiveJobId = null;
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);

            // Signal drain service — agent is idle and may pick up a different job
            _facade.Signal();
        }
    }

    /// <summary>
    /// Agent reports job completion. Updates the PipelineRun, persists to history,
    /// transitions agent to Idle, and signals the drain service for next dispatch.
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportJobCompleted(string jobId, JobCompletionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Hub.ReportJobCompleted");
        activity?.SetTag("job_id", jobId);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        var run = _facade.GetRun(jobId);

        if (run is not null)
        {
            // Update run with completion data
            JobCompletionMapper.Apply(run, payload);

            activity?.SetTag("success", payload.FinalStep == PipelineStep.Completed);

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
            // Only accept FinalLabel if it is a known agent label; ignore arbitrary values.
            var finalLabel = payload.FinalLabel is not null && AgentLabels.All.Contains(payload.FinalLabel)
                ? payload.FinalLabel
                : null;
            var label = finalLabel ?? payload.FinalStep switch
            {
                PipelineStep.Failed => AgentLabels.Error,
                PipelineStep.Completed => AgentLabels.Done,
                PipelineStep.Cancelled => AgentLabels.Cancelled,
                _ => null
            };

            if (label is not null)
            {
                await SwapLabelAsync(run, label, GetLabelTargetKind(run));
            }

            // Post issue feedback comment if present (non-fatal)
            await PostIssueFeedbackCommentAsync(run);
        }

        // Transition agent to Idle and clear active job
        if (agent is not null)
        {
            agent.ActiveJobId = null;
            agent.OrphanRestoredAt = null;
            agent.LastJobCompletedAt = DateTimeOffset.UtcNow;
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);

            // Mark issue as no longer processing in the dispatcher
            if (run is not null)
                _facade.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

            // Signal the drain service to attempt dispatch for this now-idle agent
            _facade.Signal();
        }
    }

    // ── Real-time status ────────────────────────────────────────────────

    /// <summary>
    /// Updates the PipelineRun's CurrentStep and HighWaterMark, applies optional step metadata, notifies UI.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportStepTransition(string jobId, PipelineStep step, DateTimeOffset timestamp, Dictionary<string, string>? metadata = null)
    {
        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            run.CurrentStep = step;

            // Update HighWaterMark — only advance, never go backward
            // Exclude terminal states (Completed, Failed, Cancelled) from high water mark
            if (step < PipelineStep.Completed && step > run.HighWaterMark)
                run.HighWaterMark = step;

            // Apply step metadata from the agent (carries data from the just-completed step)
            if (metadata is { Count: > 0 })
                ApplyStepMetadata(run, metadata);

            _logger.Debug("Job {JobId} step transition → {Step}", jobId, step);
            _orchestration.NotifyChange();
        }

        // Clear orphan-restored flag: agent is actively progressing on this job
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        if (agent is { OrphanRestoredAt: not null })
        {
            _logger.Information(
                "Agent {AgentId} reported progress on job {JobId}, clearing orphan-restored state",
                agent.AgentId, jobId);
            agent.OrphanRestoredAt = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies key-value metadata from step transitions to the PipelineRun.
    /// Keys use a flat naming convention (e.g., "BranchName", "BaselineHealthPassed").
    /// </summary>
    private static void ApplyStepMetadata(PipelineRun run, Dictionary<string, string> metadata)
    {
        foreach (var (key, value) in metadata)
        {
            switch (key)
            {
                case "BranchName":
                    run.BranchName = value;
                    break;
                case "BaselineHealthPassed":
                    run.BaselineHealthPassed = bool.TryParse(value, out var bhp) ? bhp : null;
                    break;
                case "AnalysisSkipped":
                    run.AnalysisSkipped = bool.TryParse(value, out var ask) && ask;
                    break;
                case "FilesChangedCount":
                    if (int.TryParse(value, out var fcc)) run.FilesChangedCount = fcc;
                    break;
                case "LinesAdded":
                    if (int.TryParse(value, out var la)) run.LinesAdded = la;
                    break;
                case "LinesRemoved":
                    if (int.TryParse(value, out var lr)) run.LinesRemoved = lr;
                    break;
                case "CodeReviewIterationsCompleted":
                    if (int.TryParse(value, out var cric)) run.CodeReviewIterationsCompleted = cric;
                    break;
                case "CodeReviewIterationsTotal":
                    if (int.TryParse(value, out var crit)) run.CodeReviewIterationsTotal = crit;
                    break;
                case "CodeReviewIterationInProgress":
                    if (int.TryParse(value, out var crip)) run.CodeReviewIterationInProgress = crip;
                    break;
                case "OpenIssuesDownloaded":
                    if (int.TryParse(value, out var oid)) run.OpenIssuesDownloaded = oid;
                    break;
                case "DecompositionSubIssuesCreated":
                    if (int.TryParse(value, out var dsic)) run.DecompositionSubIssuesCreated = dsic;
                    break;
                case "DecompositionSubIssuesAttempted":
                    if (int.TryParse(value, out var dsia)) run.DecompositionSubIssuesAttempted = dsia;
                    break;
            }
        }
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
    /// Executes a label swap on the entity (issue or PR) via <see cref="ILabelSwapper"/>.
    /// Routes to the correct provider based on <paramref name="targetKind"/>.
    /// </summary>
    [RequiresActiveJob]
    public async Task RequestLabelChange(string jobId, string newLabel, int targetKind = 0)
    {
        ArgumentNullException.ThrowIfNull(newLabel);

        var run = _facade.GetRun(jobId);
        if (run is null)
        {
            _logger.Warning("RequestLabelChange for unknown run {JobId}", jobId);
            return;
        }

        if (!string.IsNullOrEmpty(newLabel) && !AgentLabels.All.Contains(newLabel))
        {
            _logger.Warning("Agent requested invalid label '{Label}' for job {JobId}, ignoring", newLabel, jobId);
            return;
        }

        // Derive targetKind from the run's RunType rather than trusting the caller-supplied value.
        // This prevents a buggy or compromised agent from routing label operations to the wrong entity.
        var kind = GetLabelTargetKind(run);

        await SwapLabelAsync(run, newLabel, kind);
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
            targetConfig = await _facade.GetProviderConfigByIdAsync(run.BrainProviderConfigId, ProviderKind.Repository, CancellationToken.None);
        }

        if (targetConfig is null)
        {
            // Default: use the work repo config (covers Repository kind and fallback)
            targetConfig = await _facade.GetProviderConfigByIdAsync(run.RepoProviderConfigId, ProviderKind.Repository, CancellationToken.None);
        }

        if (targetConfig is null)
            throw new HubException($"Provider config not found for job {jobId} (kind: {providerKind})");

        // GitHub App auth: generate a short-lived scoped token via JWT exchange
        if (targetConfig.Settings.ContainsKey(ProviderSettingKeys.PrivateKeyBase64))
        {
            var (token, expiresAt) = await _tokenVending.GenerateAgentTokenAsync(targetConfig, CancellationToken.None);

            _logger.Information("Token refreshed for job {JobId} (kind: {ProviderKind}), expires at {ExpiresAt}",
                jobId, providerKind, expiresAt);

            return new TokenRefreshResponse { Token = token, ExpiresAt = expiresAt };
        }

        // GitLab PAT / static token: return the access token directly (no vending needed)
        if (targetConfig.Settings.TryGetValue(ProviderSettingKeys.AccessToken, out var accessToken)
            && !string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.Information("Returning static access token for job {JobId} (kind: {ProviderKind})",
                jobId, providerKind);

            // Use a far-future expiry since PATs don't expire through this mechanism
            return new TokenRefreshResponse { Token = accessToken, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        }

        // Fallback: check if a pre-vended token already exists in settings
        if (targetConfig.Settings.TryGetValue(ProviderSettingKeys.Token, out var existingToken)
            && !string.IsNullOrWhiteSpace(existingToken))
        {
            _logger.Information("Returning existing token for job {JobId} (kind: {ProviderKind})",
                jobId, providerKind);

            return new TokenRefreshResponse { Token = existingToken, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        }

        throw new HubException($"Provider config for job {jobId} (kind: {providerKind}) has no supported authentication method. " +
            "Expected 'privateKeyBase64' (GitHub App), 'accessToken' (GitLab PAT), or 'token'.");
    }

    // ── Decomposition issue operations (proxied through orchestrator) ──

    /// <summary>
    /// Creates a new issue via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.CreateIssueAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public async Task<CreatedIssueResult> RequestCreateIssue(string jobId, string title, string body, IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(labels);

        var (run, issueProvider) = await ResolveIssueProviderForRunAsync(jobId);
        await using (issueProvider)
        {
            try
            {
                return await issueProvider.CreateIssueAsync(title, body, labels, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "RequestCreateIssue failed for job {JobId}", jobId);
                throw new HubException($"Failed to create issue for job {jobId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Creates a new issue via a specific issue provider (for cross-repo decomposition routing).
    /// Called by the agent's <c>OrchestratorProxy.CreateIssueForProviderAsync</c> when the
    /// decomposed issue's <c>targetRepository</c> resolves to a different template's issue provider.
    /// </summary>
    [RequiresActiveJob]
    public async Task<CreatedIssueResult> RequestCreateIssueForProvider(
        string jobId, string issueProviderConfigId, string title, string body, IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(issueProviderConfigId);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(labels);

        var run = _facade.GetRun(jobId);
        if (run is null)
            throw new HubException($"No active run found for job {jobId}");

        var issueConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == issueProviderConfigId);
        if (issueConfig is null)
            throw new HubException($"Issue provider config '{issueProviderConfigId}' not found for cross-repo routing in job {jobId}");

        await using var issueProvider = _facade.CreateIssueProvider(issueConfig);
        try
        {
            return await issueProvider.CreateIssueAsync(title, body, labels, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "RequestCreateIssueForProvider failed for job {JobId}, provider {ProviderId}",
                jobId, issueProviderConfigId);
            throw new HubException($"Failed to create issue for job {jobId} via provider {issueProviderConfigId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Lists open issues with optional label filtering via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.ListOpenIssuesAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public async Task<PagedResult<IssueSummary>> RequestListOpenIssues(string jobId, int page, int pageSize, IReadOnlyList<string>? labels)
    {
        var (run, issueProvider) = await ResolveIssueProviderForRunAsync(jobId);
        await using (issueProvider)
        {
            try
            {
                return await issueProvider.ListOpenIssuesAsync(page, pageSize, labels, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "RequestListOpenIssues failed for job {JobId}", jobId);
                throw new HubException($"Failed to list open issues for job {jobId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Gets full issue details by identifier via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.GetIssueAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public async Task<IssueDetail> RequestGetIssue(string jobId, string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var (run, issueProvider) = await ResolveIssueProviderForRunAsync(jobId);
        await using (issueProvider)
        {
            try
            {
                return await issueProvider.GetIssueAsync(identifier, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "RequestGetIssue failed for job {JobId}, identifier {Identifier}", jobId, identifier);
                throw new HubException($"Failed to get issue '{identifier}' for job {jobId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Lists all comments on an issue via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.ListCommentsAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public async Task<IReadOnlyList<IssueComment>> RequestListComments(string jobId, string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        var (run, issueProvider) = await ResolveIssueProviderForRunAsync(jobId);
        await using (issueProvider)
        {
            try
            {
                return await issueProvider.ListCommentsAsync(identifier, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "RequestListComments failed for job {JobId}, identifier {Identifier}", jobId, identifier);
                throw new HubException($"Failed to list comments for issue '{identifier}' on job {jobId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Updates an existing comment by ID via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.UpdateCommentAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public async Task RequestUpdateComment(string jobId, string issueId, string commentId, string body)
    {
        ArgumentNullException.ThrowIfNull(issueId);
        ArgumentNullException.ThrowIfNull(commentId);
        ArgumentNullException.ThrowIfNull(body);

        var (run, issueProvider) = await ResolveIssueProviderForRunAsync(jobId);
        await using (issueProvider)
        {
            try
            {
                await issueProvider.UpdateCommentAsync(issueId, commentId, body, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "RequestUpdateComment failed for job {JobId}, issue {IssueId}, comment {CommentId}", jobId, issueId, commentId);
                throw new HubException($"Failed to update comment '{commentId}' on issue '{issueId}' for job {jobId}: {ex.Message}");
            }
        }
    }

    // ── Pipeline-local private helpers ──────────────────────────────────

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
