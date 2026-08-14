using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgentWebUI.Hubs;

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
        var kind = GetLabelTargetKind(run);

        _logger.Information(
            "RequestLabelChange: job {JobId} requesting label {Label} for issue {IssueIdentifier} (agent={AgentId}, currentStep={CurrentStep})",
            jobId.Value, newLabel, run.IssueIdentifier, run.AgentId, run.CurrentStep);

        await SwapLabelAsync(run, newLabel, kind);
    }

    // ── Token refresh ───────────────────────────────────────────────────

    /// <summary>
    /// Generates a fresh short-lived token via <see cref="IAgentTokenRefreshService"/>.
    /// Supports both SignalR mode (PipelineRun in memory) and K8s mode (WorkItem payload in DB).
    /// </summary>
    [RequiresActiveJob]
    public Task<TokenRefreshResponse> RequestTokenRefresh(JobId jobId, ProviderKind providerKind)
        => _tokenRefreshService.RefreshTokenAsync(jobId.Value, providerKind, CancellationToken.None);

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
    /// Validates the job ID, finds the run, loads the issue provider config, and creates the provider.
    /// </summary>
    /// <exception cref="HubException">Thrown when the job ID is invalid or the provider config is not found.</exception>
    private async Task<(PipelineRun Run, IIssueProvider Provider)> ResolveIssueProviderForRunAsync(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        var run = _facade.GetRun(jobId);
        if (run is null)
            throw new HubException($"No active run found for job {jobId}");

        // TODO: Thread the caller-supplied CancellationToken (or a SignalR connection-lifetime token)
        // through LoadProviderConfigsAsync instead of CancellationToken.None. The ct parameter is
        // forwarded to the provider operation delegate but the config-loading step that precedes it
        // cannot currently be cancelled, giving callers a false impression that the full call chain
        // is cancellable. Requires updating the method signature to accept and propagate ct here.
        var issueConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == run.IssueProviderConfigId);
        if (issueConfig is null)
            throw new HubException($"Issue provider config '{run.IssueProviderConfigId}' not found for job {jobId}");

        return (run, _facade.CreateIssueProvider(issueConfig));
    }
}
