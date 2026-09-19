using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgent.AgentGateway;

public sealed partial class AgentHub
{
    // ── Issue operations (proxied through orchestrator) ─────────────────

    /// <summary>
    /// Formats and posts a comment on the GitHub issue via <see cref="IIssueProvider"/>.
    /// Uses existing comment formatters based on <paramref name="commentType"/>.
    /// </summary>
    [RequiresActiveJob]
    public async Task RequestPostComment(JobId jobId, CommentType commentType, CommentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var run = _facade.GetRun(jobId);
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
                commentBody = _gateCommentFormatter.FormatGateComment(payload.AssessmentJson, isWontDo: false);
                break;

            case CommentType.GateWontDo:
                commentBody = _gateCommentFormatter.FormatGateComment(payload.AssessmentJson, isWontDo: true);
                break;

            default:
                _logger.Warning("Unknown comment type {CommentType} for job {JobId}", commentType, jobId);
                return;
        }

        await PostCommentViaIssueProviderAsync(run, commentBody);
    }

    /// <summary>
    /// Executes a label swap on the entity (issue or PR) via <see cref="ILabelService"/>.
    /// Routes to the correct provider based on <paramref name="targetKind"/>.
    /// </summary>
    [RequiresActiveJob]
    public async Task RequestLabelChange(JobId jobId, string newLabel, int targetKind = 0)
    {
        ArgumentNullException.ThrowIfNull(newLabel);

        var run = _facade.GetRun(jobId);
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

        if (!string.IsNullOrEmpty(newLabel) && AgentLabels.DispatchGatedLabels.Contains(newLabel))
        {
            _logger.Warning(
                "Agent requested gated label '{Label}' for job {JobId} — requires human approval, ignoring",
                newLabel, jobId.Value);
            return;
        }

        // Derive targetKind from the run's RunType rather than trusting the caller-supplied value.
        // This prevents a buggy or compromised agent from routing label operations to the wrong entity.
        // Note: the target kind is derived inside AgentIssueOperations.SwapLabelAsync from run.LabelTargetKind.

        _logger.Information(
            "RequestLabelChange: job {JobId} requesting label {Label} for issue {IssueIdentifier} (agent={AgentId}, currentStep={CurrentStep})",
            jobId.Value, newLabel, run.IssueIdentifier, run.AgentId, run.CurrentStep);

        await SwapLabelAsync(run, newLabel);
    }

    // ── Token refresh ───────────────────────────────────────────────────

    /// <summary>
    /// Generates a fresh short-lived token via <see cref="IAgentTokenRefreshService"/>.
    /// Supports both SignalR mode (PipelineRun in memory) and K8s mode (WorkItem payload in DB).
    /// </summary>
    [RequiresActiveJob]
    public Task<TokenRefreshResponse> RequestTokenRefresh(JobId jobId, ProviderKind providerKind, bool includeIssuePermission = false)
        => _tokenRefreshService.RefreshTokenAsync(jobId.Value, providerKind, CancellationToken.None, includeIssuePermission);

    // ── Issue ops private helpers ───────────────────────────────────────

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
    /// Validates the job ID, finds the run (or falls back to the DB WorkItem), loads the issue
    /// provider config, and creates the provider.
    /// </summary>
    /// <returns>
    /// A tuple of <c>(PipelineRun? Run, IIssueProvider Provider)</c>. <c>Run</c> may be null when
    /// the in-memory run is absent but the WorkItem exists in the database (cross-replica miss or
    /// mid-run restart). Callers that do not need the run should discard it; callers that require
    /// a non-null run must handle the null case explicitly.
    /// </returns>
    /// <exception cref="HubException">Thrown when the job ID is invalid, the run and WorkItem are
    /// both absent, or the provider config is not found.</exception>
    private async Task<(PipelineRun? Run, IIssueProvider Provider)> ResolveIssueProviderForRunAsync(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        var run = _facade.GetRun(jobId);

        string issueProviderConfigId;
        if (run is not null)
        {
            issueProviderConfigId = run.IssueProviderConfigId;
        }
        else
        {
            // Fallback: resolve from WorkItem payload in DB (no in-memory run found).
            // This mirrors the pattern in AgentTokenRefreshService.ResolveProviderConfigIdsAsync
            // and handles cross-replica state misses and mid-run kiro-cli sub-process restarts.
            _logger.Warning(
                "ResolveIssueProviderForRunAsync: no active run found for job {JobId} — attempting DB fallback",
                jobId);

            var meta = await _facade.GetWorkItemIssueMetadataAsync(new JobId(jobId), CancellationToken.None);
            // TODO: [WARNING] CancellationToken.None is passed here for the same reason as the
            // LoadProviderConfigsAsync call below — the method signature does not currently accept
            // a cancellation token. If a SignalR connection is aborted while this DB query is in
            // flight, it cannot be cancelled and will run to completion. When the method signature
            // is updated to accept and propagate a token (see the existing TODO below), this call
            // site should be updated at the same time.
            if (meta is null)
            {
                _logger.Warning(
                    "ResolveIssueProviderForRunAsync: no active run or work item found for job {JobId}",
                    jobId);
                throw new HubException($"No active run or work item found for job {jobId}");
            }

            issueProviderConfigId = meta.Value.IssueProviderConfigId;
        }

        // TODO: Thread the caller-supplied CancellationToken (or a SignalR connection-lifetime token)
        // through LoadProviderConfigsAsync instead of CancellationToken.None. The ct parameter is
        // forwarded to the provider operation delegate but the config-loading step that precedes it
        // cannot currently be cancelled, giving callers a false impression that the full call chain
        // is cancellable. Requires updating the method signature to accept and propagate ct here.
        var issueConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var issueConfig = issueConfigs.TryGetProviderConfig(issueProviderConfigId);
        if (issueConfig is null)
        {
            _logger.Warning(
                "ResolveIssueProviderForRunAsync: issue provider config {IssueProviderConfigId} not found for job {JobId}",
                issueProviderConfigId, jobId);
            throw new HubException($"Issue provider config '{issueProviderConfigId}' not found for job {jobId}");
        }

        return (run, _facade.CreateIssueProvider(issueConfig));
    }
}
