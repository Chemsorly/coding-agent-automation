using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
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
            // No in-memory run — cross-replica state miss or mid-run pod restart.
            // Attempt a DB fallback: resolve the issue provider from the WorkItem record
            // and perform the label swap directly, bypassing the in-memory run requirement.
            // This ensures agent:epic (or the current dispatch label) is removed even when
            // the run is not present on this hub replica, preventing the dual-label loop
            // (agent:epic + agent:error) that re-queues the issue on every scheduler poll.
            _logger.Warning(
                "RequestLabelChange for unknown run {JobId} — attempting DB fallback for label {Label}",
                jobId.Value, newLabel);
            await RequestLabelChangeFallbackAsync(jobId.Value, newLabel);
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

    /// <summary>
    /// Fallback label swap for when no in-memory run is found (cross-replica miss).
    /// Resolves the issue provider from the DB WorkItem record and performs the label
    /// swap directly. Non-fatal: catches and logs HubException if the WorkItem is also
    /// absent or the provider config cannot be found.
    /// </summary>
    // TODO (WARNING — .NET Specialist / Security): catch (HubException) is too narrow.
    // CreateIssueProvider can throw NotSupportedException (unregistered provider type),
    // LoadProviderConfigsAsync can throw DbException/HttpRequestException, and
    // AgentLabelOperations.SwapAsync can propagate HttpRequestException/TaskCanceledException
    // from the concrete issue provider's AddLabelAsync/RemoveLabelAsync. Any of these escape
    // the current catch block and surface as an unhandled hub-method exception, contradicting
    // the stated non-fatal contract. Broaden to:
    //   catch (Exception ex) when (ex is not OperationCanceledException)
    // mirroring the pattern in LabelService.SwapLabelAsync. Also add a test where
    // CreateIssueProvider throws NotSupportedException to lock in the corrected behaviour.
    private async Task RequestLabelChangeFallbackAsync(string jobId, string newLabel)
    {
        // TODO (WARNING — Security): jobId is logged verbatim via structured logging throughout
        // this method. A crafted jobId containing newline characters or log-injection payloads
        // (e.g. "legit-id\nINFO: auth=admin bypassed") would be embedded in log output, misleading
        // operators reviewing audit logs. Exploitation requires a compromised agent credential.
        // Mitigate by sanitising jobId before logging (e.g. replace control characters), or by
        // ensuring the structured log sink strips newlines — verify the current sink configuration.

        // Validate the label before attempting the DB lookup — the same guards applied
        // in the in-memory path must apply here too to prevent invalid or gated labels
        // from being applied via the fallback path.
        if (!string.IsNullOrEmpty(newLabel) && !AgentLabels.All.Contains(newLabel))
        {
            _logger.Warning(
                "RequestLabelChange fallback: invalid label '{Label}' for job {JobId}, ignoring",
                newLabel, jobId);
            return;
        }

        if (!string.IsNullOrEmpty(newLabel) && AgentLabels.DispatchGatedLabels.Contains(newLabel))
        {
            _logger.Warning(
                "RequestLabelChange fallback: gated label '{Label}' for job {JobId} — requires human approval, ignoring",
                newLabel, jobId);
            return;
        }

        try
        {
            // ResolveIssueProviderForRunAsync performs the DB WorkItem lookup and provider config
            // resolution. It throws HubException if the WorkItem is absent or config not found.
            var (_, issueProvider) = await ResolveIssueProviderForRunAsync(jobId);
            await using (issueProvider)
            {
                // TODO (WARNING — .NET Specialist): Double DB lookup. ResolveIssueProviderForRunAsync
                // already calls GetWorkItemIssueMetadataAsync internally; we call it again here to
                // get IssueIdentifier, which the resolver discards. Both values are in the same DB
                // row/tuple. Refactor ResolveIssueProviderForRunAsync to return the IssueIdentifier
                // as part of its result to avoid the second round-trip and eliminate the narrow TOCTOU
                // window where the WorkItem could be updated between the two fetches.

                // We don't have a run to get IssueIdentifier from, so read it from the DB metadata.
                // TODO (WARNING — .NET Specialist): CancellationToken.None is used here because
                // SignalR hub methods do not expose a connection-lifetime token through this call
                // path. If the connection is torn down while GetWorkItemIssueMetadataAsync is in
                // progress, the operation cannot be cancelled. Tracked alongside the same pattern
                // used in ResolveIssueProviderForRunAsync (see existing TODO there).
                var meta = await _facade.GetWorkItemIssueMetadataAsync(new JobId(jobId), CancellationToken.None);
                if (meta is null)
                {
                    // Should not happen: ResolveIssueProviderForRunAsync already checked this,
                    // but guard defensively.
                    _logger.Warning(
                        "RequestLabelChange fallback: metadata disappeared for job {JobId}, label swap skipped",
                        jobId);
                    return;
                }

                var issueIdentifier = meta.Value.IssueIdentifier;

                _logger.Information(
                    "RequestLabelChange fallback: swapping label to {Label} for issue {IssueIdentifier} (job {JobId})",
                    newLabel, issueIdentifier, jobId);

                await AgentLabelOperations.SwapAsync(
                    removeLabel: (label, ct) => issueProvider.RemoveLabelAsync(issueIdentifier, label, ct),
                    addLabel: (label, ct) => issueProvider.AddLabelAsync(issueIdentifier, label, ct),
                    newLabel: newLabel,
                    // TODO (WARNING — .NET Specialist): CancellationToken.None is passed because no
                    // connection-lifetime token is available here. AgentLabelOperations.SwapAsync
                    // propagates ct to the add/remove delegates and to the retry Task.Delay backoff
                    // loop (up to 3 attempts). With CancellationToken.None the loop cannot be
                    // interrupted if the SignalR connection is torn down mid-swap. Acceptable as a
                    // best-effort fallback for now; revisit when a connection-scoped token is threaded
                    // through the hub method signature.
                    ct: CancellationToken.None,
                    identifier: issueIdentifier);

                _logger.Information(
                    "RequestLabelChange fallback: label swap completed for issue {IssueIdentifier} (job {JobId})",
                    issueIdentifier, jobId);
            }
        }
        catch (HubException ex)
        {
            // The WorkItem is absent, config is missing, or another HubException occurred.
            // Label swap failures are non-fatal — log and continue. The issue may retain
            // a stale label but will not loop: the scheduler will detect agent:error once
            // the run completes via the normal completion path.
            _logger.Warning(
                "RequestLabelChange fallback failed for job {JobId} (label={Label}): {Message} — label swap skipped",
                jobId, newLabel, ex.Message);
        }
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
            // GetProviderConfigByIdAsync call below — the method signature does not currently accept
            // a cancellation token. If a SignalR connection is aborted while this DB query is in
            // flight, it cannot be cancelled and will run to completion. When the method signature
            // is updated to accept and propagate a token (see the existing TODO below), this call
            // site should be updated at the same time.
            if (meta is null)
            {
                _logger.Error(
                    "ResolveIssueProviderForRunAsync: no active run or work item found for job {JobId} — " +
                    "RequestGetIssue will fail; possible cross-replica state miss or expired Redis entry",
                    jobId);
                throw new HubException($"No active run or work item found for job {jobId}");
            }

            issueProviderConfigId = meta.Value.IssueProviderConfigId;
        }

        // TODO: Thread the caller-supplied CancellationToken (or a SignalR connection-lifetime token)
        // through GetProviderConfigByIdAsync instead of CancellationToken.None. The ct parameter is
        // forwarded to the provider operation delegate but the config-loading step that precedes it
        // cannot currently be cancelled, giving callers a false impression that the full call chain
        // is cancellable. Requires updating the method signature to accept and propagate ct here.
        // NOTE: ConsolidationConstants.ProviderConfigId is a non-GUID sentinel; PostgresConfigurationStore
        // has a !Guid.TryParse guard that silently returns null for such values. Consolidation runs
        // do not reach issue-ops methods so this is safe in practice.
        ProviderConfig issueConfig;
        try
        {
            issueConfig = await ProviderConfigResolver.ResolveRequiredAsync(
                () => _facade.GetProviderConfigByIdAsync(issueProviderConfigId, ProviderKind.Issue, CancellationToken.None),
                issueProviderConfigId, ProviderKind.Issue, _logger);
        }
        catch (InvalidOperationException ex)
        {
            // TODO [WARNING]: The HubException message here only contains ex.Message
            // ("Provider config 'X' (Issue) not found.") which omits the job ID. The original
            // code used $"Issue provider config '{issueProviderConfigId}' not found for job {jobId}",
            // which embedded jobId for operator correlation. Consider passing jobId into the
            // exception message: $"{ex.Message} (job: {jobId})" to restore the diagnostic context
            // without duplicating the resolver's log message.
            throw new HubException(ex.Message);
        }

        return (run, _facade.CreateIssueProvider(issueConfig));
    }
}
