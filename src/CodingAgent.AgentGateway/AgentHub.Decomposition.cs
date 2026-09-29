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
    /// Lists open issues via a specific issue provider (for cross-repo deduplication in project epic reruns).
    /// Called by the agent's <c>OrchestratorProxy.ListOpenIssuesForProviderAsync</c> when
    /// <c>DecompositionStep</c> reads already-created sub-issues from template trackers.
    /// Applies the same scope check as <see cref="RequestCreateIssueForProvider"/>: the run's own
    /// tracker is always permitted; a different tracker requires a project epic in scope.
    /// </summary>
    [RequiresActiveJob]
    public async Task<PagedResult<IssueSummary>> RequestListOpenIssuesForProvider(
        JobId jobId, string issueProviderConfigId, int page, int pageSize, IReadOnlyList<string>? labels)
    {
        ArgumentNullException.ThrowIfNull(issueProviderConfigId);

        var run = _facade.GetRun(jobId);
        if (run is null)
            throw new HubException($"No active run found for job {jobId.Value}");

        // TODO: CancellationToken.None is passed to LoadProviderConfigsAsync and ListOpenIssuesAsync below,
        // consistent with the existing RequestCreateIssueForProvider pattern (see line ~47 TODO).
        // A SignalR disconnect during the scope-check or list phase will not be observed until awaits complete.
        // See review finding: AgentHub.Decomposition — CancellationToken.None in RequestListOpenIssuesForProvider.
        var issueConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var issueConfig = issueConfigs.TryGetProviderConfig(issueProviderConfigId);
        if (issueConfig is null)
            throw new HubException($"Issue provider config '{SanitizeForLog(issueProviderConfigId)}' not found for cross-repo listing in job {jobId.Value}");

        if (issueProviderConfigId != run.IssueProviderConfigId
            && !await IsInProjectEpicScopeAsync(run, issueProviderConfigId))
        {
            throw new HubException($"Provider '{SanitizeForLog(issueProviderConfigId)}' is not in the scope of job {jobId.Value}: only a project epic may list issues in the trackers of its project's templates");
        }

        await using var issueProvider = _facade.CreateIssueProvider(issueConfig);
        try
        {
            return await issueProvider.ListOpenIssuesAsync(page, pageSize, labels, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "RequestListOpenIssuesForProvider failed for job {JobId}, provider {ProviderId}",
                jobId.Value, issueProviderConfigId);
            throw new HubException($"Failed to list open issues for job {jobId.Value} via provider {issueProviderConfigId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Lists closed issues via a specific issue provider (for cross-repo deduplication in project epic reruns).
    /// Called by the agent's <c>OrchestratorProxy.ListClosedIssuesForProviderAsync</c>.
    /// Applies the same scope check as <see cref="RequestCreateIssueForProvider"/>.
    /// </summary>
    [RequiresActiveJob]
    public async Task<PagedResult<IssueSummary>> RequestListClosedIssuesForProvider(
        JobId jobId, string issueProviderConfigId, int page, int pageSize, IReadOnlyList<string>? labels, DateTime? since)
    {
        ArgumentNullException.ThrowIfNull(issueProviderConfigId);

        var run = _facade.GetRun(jobId);
        if (run is null)
            throw new HubException($"No active run found for job {jobId.Value}");

        // TODO: CancellationToken.None is passed to LoadProviderConfigsAsync and ListClosedIssuesAsync below,
        // consistent with the existing RequestCreateIssueForProvider pattern (see line ~47 TODO).
        // A SignalR disconnect during the scope-check or list phase will not be observed until awaits complete.
        // See review finding: AgentHub.Decomposition — CancellationToken.None in RequestListClosedIssuesForProvider.
        var issueConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var issueConfig = issueConfigs.TryGetProviderConfig(issueProviderConfigId);
        if (issueConfig is null)
            throw new HubException($"Issue provider config '{SanitizeForLog(issueProviderConfigId)}' not found for cross-repo listing in job {jobId.Value}");

        if (issueProviderConfigId != run.IssueProviderConfigId
            && !await IsInProjectEpicScopeAsync(run, issueProviderConfigId))
        {
            throw new HubException($"Provider '{SanitizeForLog(issueProviderConfigId)}' is not in the scope of job {jobId.Value}: only a project epic may list issues in the trackers of its project's templates");
        }

        await using var issueProvider = _facade.CreateIssueProvider(issueConfig);
        try
        {
            return await issueProvider.ListClosedIssuesAsync(page, pageSize, labels, since, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "RequestListClosedIssuesForProvider failed for job {JobId}, provider {ProviderId}",
                jobId.Value, issueProviderConfigId);
            throw new HubException($"Failed to list closed issues for job {jobId.Value} via provider {issueProviderConfigId}: {ex.Message}");
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
}
