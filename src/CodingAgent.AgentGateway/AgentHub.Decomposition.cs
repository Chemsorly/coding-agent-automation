using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgent.AgentGateway;

public sealed partial class AgentHub
{
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

        var filteredLabels = FilterLabelsForCreation(labels, _logger, jobId.Value);
        return ExecuteWithIssueProviderAsync<CreatedIssueResult>(jobId.Value, "create issue",
            (provider, ct) => provider.CreateIssueAsync(title, body, filteredLabels, ct));
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

        var run = _facade.GetRun(jobId);
        if (run is null)
            throw new HubException($"No active run found for job {jobId.Value}");

        // TODO: Thread a SignalR connection-lifetime CancellationToken through the async I/O calls
        // below (LoadProviderConfigsAsync and the scope check's project and template loads) instead of
        // using CancellationToken.None. If the agent disconnects during the scope-check phase, these awaits
        // will run to completion against a now-dead connection context. This method is the only
        // location in the decomposition file with multiple uncancellable async I/O calls.
        var issueConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var issueConfig = issueConfigs.TryGetProviderConfig(issueProviderConfigId);
        if (issueConfig is null)
            throw new HubException($"Issue provider config '{SanitizeForLog(issueProviderConfigId)}' not found for cross-repo routing in job {jobId.Value}");

        // Scope check: the run's own tracker is always in scope. Any other tracker is in scope only for
        // a project epic (see IsInProjectEpicScopeAsync); a repo epic, or a run without a project, may
        // create issues only in its own tracker.
        if (issueProviderConfigId != run.IssueProviderConfigId
            && !await IsInProjectEpicScopeAsync(run, issueProviderConfigId))
        {
            throw new HubException($"Provider '{SanitizeForLog(issueProviderConfigId)}' is not in the scope of job {jobId.Value}: only a project epic may create issues in the trackers of its project's templates");
        }

        await using var issueProvider = _facade.CreateIssueProvider(issueConfig);
        try
        {
            // TODO: CreateIssueAsync also uses CancellationToken.None — extend the fix above to cover
            // this call as well when threading a SignalR connection-lifetime token through this method.
            var filteredLabels = FilterLabelsForCreation(labels, _logger, jobId.Value);
            return await issueProvider.CreateIssueAsync(title, body, filteredLabels, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "RequestCreateIssueForProvider failed for job {JobId}, provider {ProviderId}",
                jobId.Value, issueProviderConfigId);
            throw new HubException($"Failed to create issue for job {jobId.Value} via provider {issueProviderConfigId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether <paramref name="run"/> may create an issue in <paramref name="issueProviderConfigId"/>, a tracker
    /// other than its own. Only a project epic's decomposition may: a <see cref="PipelineRunType.Decomposition"/>
    /// run bound to its project's epic tracker, creating the issue in the tracker of an enabled template of
    /// that project. Other runs bound to the same tracker (for example when it is also a template's tracker)
    /// may not.
    /// </summary>
    private async Task<bool> IsInProjectEpicScopeAsync(PipelineRun run, string issueProviderConfigId)
    {
        if (run.RunType != PipelineRunType.Decomposition || string.IsNullOrEmpty(run.ProjectId))
            return false;

        var project = await _facade.GetProjectByIdAsync(run.ProjectId, CancellationToken.None);
        if (project is null || !project.IsEpicTracker(run.IssueProviderConfigId))
            return false;

        var templates = await _facade.LoadTemplatesForProjectAsync(run.ProjectId, CancellationToken.None);
        return templates.Any(t => t.Enabled && t.IssueProviderId == issueProviderConfigId);
    }

    /// <summary>
    /// Lists open issues with optional label filtering via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.ListOpenIssuesAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public async Task<PagedResult<IssueSummary>> RequestListOpenIssues(JobId jobId, int page, int pageSize, IReadOnlyList<string>? labels)
    {
        try
        {
            return await ExecuteWithIssueProviderAsync<PagedResult<IssueSummary>>(jobId.Value, "list open issues",
                (provider, ct) => provider.ListOpenIssuesAsync(page, pageSize, labels, ct));
        }
        catch (Exception ex)
        {
            throw new HubException(
                $"RequestListOpenIssues failed for job {jobId.Value} (page={page}, pageSize={pageSize}): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Lists closed issues with optional label filtering and date cutoff via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.ListClosedIssuesAsync</c> during decomposition runs
    /// to include recently-closed sibling issues in agent context.
    /// </summary>
    [RequiresActiveJob]
    public async Task<PagedResult<IssueSummary>> RequestListClosedIssues(JobId jobId, int page, int pageSize, IReadOnlyList<string>? labels, DateTime? since)
    {
        try
        {
            return await ExecuteWithIssueProviderAsync<PagedResult<IssueSummary>>(jobId.Value, "list closed issues",
                (provider, ct) => provider.ListClosedIssuesAsync(page, pageSize, labels, since, ct));
        }
        catch (Exception ex)
        {
            throw new HubException(
                $"RequestListClosedIssues failed for job {jobId.Value} (page={page}, pageSize={pageSize}): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets full issue details by identifier via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.GetIssueAsync</c>.
    /// Logs a warning when the requested identifier differs from the run's own issue (audit trail for 1G-006).
    /// </summary>
    [RequiresActiveJob]
    public async Task<IssueDetail> RequestGetIssue(JobId jobId, string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        // Security audit (1G-006): log when an agent reads an issue other than its own.
        // This is not blocked — decomposition and cross-repo workflows legitimately read
        // related issues — but it is logged at Debug level for auditability.
        var run = _facade.GetRun(jobId);
        if (run is not null
            && !string.IsNullOrEmpty(run.IssueIdentifier)
            && !string.Equals(identifier, run.IssueIdentifier, StringComparison.Ordinal))
        {
            _logger.Debug(
                "RequestGetIssue: agent {AgentId} reading issue '{RequestedIdentifier}' " +
                "(own issue: '{OwnIdentifier}', job {JobId})",
                run.AgentId, SanitizeForLog(identifier), SanitizeForLog(run.IssueIdentifier), jobId.Value);
        }

        try
        {
            return await ExecuteWithIssueProviderAsync<IssueDetail>(jobId.Value, $"get issue '{identifier}'",
                (provider, ct) => provider.GetIssueAsync(identifier, ct));
        }
        catch (Exception ex)
        {
            // Log at Error so the server-side cause of "Failed to invoke 'RequestGetIssue'"
            // is always visible in Grafana regardless of which failure path produced it.
            // ExecuteWithIssueProviderAsync logs provider-level exceptions (GitHub API, etc.)
            // at Error before re-throwing as HubException. This catch handles the remaining paths:
            // ResolveIssueProviderForRunAsync failures (missing run, missing provider config) that
            // escape the inner try/catch and arrive here as HubException without prior Error-level
            // logging — the comment in the previous revision was aspirational, not accurate.
            _logger.Error(ex,
                "RequestGetIssue failed for job {JobId}, identifier '{Identifier}'",
                jobId.Value, SanitizeForLog(identifier));
            throw new HubException(
                $"RequestGetIssue failed for job {jobId.Value}, identifier '{SanitizeForLog(identifier)}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Lists all comments on an issue via the run's configured <see cref="IIssueProvider"/>.
    /// Called by the agent's <c>OrchestratorProxy.ListCommentsAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public async Task<IReadOnlyList<IssueComment>> RequestListComments(JobId jobId, string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        try
        {
            return await ExecuteWithIssueProviderAsync<IReadOnlyList<IssueComment>>(jobId.Value, $"list comments for issue '{identifier}'",
                (provider, ct) => provider.ListCommentsAsync(identifier, ct));
        }
        catch (Exception ex)
        {
            throw new HubException(
                $"RequestListComments failed for job {jobId.Value}, identifier '{SanitizeForLog(identifier)}': {ex.Message}", ex);
        }
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
            (provider, ct) =>
            {
                if (!long.TryParse(commentId, out var parsedCommentId))
                    throw new ArgumentException(
                        $"Invalid comment identifier: '{commentId}'. Expected a numeric comment ID.",
                        nameof(commentId));
                return provider.UpdateCommentAsync(issueId, parsedCommentId, body, ct);
            });
    }

    // ── Label filter helper ───────────────────────────────────────────────

    /// <summary>
    /// Filters a caller-supplied label list for use on issue creation.
    /// This is stricter than <c>RequestLabelChange</c> (which allows all <see cref="AgentLabels.All"/>
    /// except <see cref="AgentLabels.DispatchGatedLabels"/>): on creation only
    /// <see cref="AgentLabels.Next"/> and <see cref="AgentLabels.Generated"/> are permitted
    /// from the <c>agent:*</c> namespace. All other <c>agent:*</c> labels require explicit
    /// human or pipeline action and must not be set at creation time.
    /// </summary>
    private static IReadOnlyList<string> FilterLabelsForCreation(
        IReadOnlyList<string> labels, Serilog.ILogger logger, string jobId)
    {
        // TODO: The StartsWith guard uses OrdinalIgnoreCase but the equality checks below use
        // ordinal (case-sensitive) ==. A label like "Agent:Next" passes the prefix check but fails
        // both equality guards and is silently dropped instead of being kept. Fix by using
        // string.Equals(label, AgentLabels.Next, StringComparison.OrdinalIgnoreCase) for both
        // equality checks. The security property (dropping disallowed labels) is not affected —
        // only the keep-path for mixed-case allowed labels is broken.
        static bool IsAllowed(string label) =>
            !label.StartsWith("agent:", StringComparison.OrdinalIgnoreCase)
            || label == AgentLabels.Next
            || label == AgentLabels.Generated;

        var allowed = new List<string>();
        foreach (var label in labels)
        {
            if (IsAllowed(label))
                allowed.Add(label);
            else
                logger.Warning(
                    "Agent requested disallowed label '{Label}' on issue creation for job {JobId} — dropped",
                    label, jobId);
        }
        return allowed;
    }
}
