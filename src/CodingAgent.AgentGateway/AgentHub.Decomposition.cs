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
    /// Agent-supplied labels are filtered through <see cref="AgentLabels.FilterForIssueCreation"/>:
    /// only <c>agent:next</c>, <c>agent:generated</c>, and non-agent labels are forwarded.
    /// All other <c>agent:*</c> labels (including <c>agent:epic-approved</c>) are dropped.
    /// </summary>
    [RequiresActiveJob]
    public Task<CreatedIssueResult> RequestCreateIssue(JobId jobId, string title, string body, IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(labels);

        var filteredLabels = FilterLabelsForIssueCreation(labels, jobId.Value);
        return ExecuteWithIssueProviderAsync<CreatedIssueResult>(jobId.Value, "create issue",
            (provider, ct) => provider.CreateIssueAsync(title, body, filteredLabels, ct));
    }

    /// <summary>
    /// Creates a new issue via a specific issue provider (for cross-repo decomposition routing).
    /// Called by the agent's <c>OrchestratorProxy.CreateIssueForProviderAsync</c> when the
    /// decomposed issue's <c>targetRepository</c> resolves to a different template's issue provider.
    /// Agent-supplied labels are filtered through <see cref="AgentLabels.FilterForIssueCreation"/>:
    /// only <c>agent:next</c>, <c>agent:generated</c>, and non-agent labels are forwarded.
    /// All other <c>agent:*</c> labels (including <c>agent:epic-approved</c>) are dropped.
    /// </summary>
    [RequiresActiveJob]
    public async Task<CreatedIssueResult> RequestCreateIssueForProvider(
        JobId jobId, string issueProviderConfigId, string title, string body, IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(issueProviderConfigId);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(labels);

        var (_, issueConfig) = await LoadProviderForCrossRepoAsync(jobId, issueProviderConfigId, "routing");

        var filteredLabels = FilterLabelsForIssueCreation(labels, jobId.Value);
        await using var issueProvider = _facade.CreateIssueProvider(issueConfig);
        try
        {
            // TODO: Thread a SignalR connection-lifetime CancellationToken here instead of CancellationToken.None.
            // A SignalR disconnect will not be observed until this await completes. Tracked in LoadProviderForCrossRepoAsync.
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

        var (_, issueConfig) = await LoadProviderForCrossRepoAsync(jobId, issueProviderConfigId, "listing");

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

        var (_, issueConfig) = await LoadProviderForCrossRepoAsync(jobId, issueProviderConfigId, "listing");

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
    /// Filters agent-supplied labels for issue creation. Keeps <c>agent:next</c>,
    /// <c>agent:generated</c>, and non-agent labels. Drops all other <c>agent:*</c> labels
    /// (including <c>agent:epic-approved</c>, <c>agent:epic</c>, and status labels such as
    /// <c>agent:done</c> and <c>agent:in-progress</c>) and logs a warning for each dropped label.
    /// </summary>
    private IReadOnlyList<string> FilterLabelsForIssueCreation(IReadOnlyList<string> labels, string jobId)
    {
        var filtered = AgentLabels.FilterForIssueCreation(labels);

        // Log a warning for every label that was removed.
        // TODO: The warning loop below duplicates the classification logic of FilterForIssueCreation
        // (re-checking All.Contains / AllowedOnCreation.Contains independently). If FilterForIssueCreation
        // changes its rules the logged warnings will silently drift from the set of actually-dropped labels.
        // A simpler and more maintainable approach is to log labels.Except(filtered) which is guaranteed to
        // stay in sync: foreach (var dropped in labels.Except(filtered)) _logger.Warning(...).
        foreach (var label in labels)
        {
            if (!string.IsNullOrEmpty(label)
                && AgentLabels.All.Contains(label)
                && !AgentLabels.AllowedOnCreation.Contains(label))
            {
                _logger.Warning(
                    "RequestCreateIssue: dropping disallowed agent label '{Label}' from issue creation (job {JobId})",
                    label, jobId);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Resolves the active run and the named issue provider config, then verifies that the provider
    /// is in scope for the job (own tracker is always allowed; a different tracker requires a project
    /// epic — see <see cref="IsInProjectEpicScopeAsync"/>).
    /// </summary>
    /// <param name="operationDescription">Short label used in error messages, e.g. "routing" or "listing".</param>
    /// <returns>The active run and the resolved provider config.</returns>
    /// <exception cref="HubException">Thrown when the run is not found, the provider config is missing,
    /// or the provider is outside the job's project-epic scope.</exception>
    private async Task<(PipelineRun Run, ProviderConfig IssueConfig)> LoadProviderForCrossRepoAsync(
        JobId jobId, string issueProviderConfigId, string operationDescription)
    {
        var run = _facade.GetRun(jobId);
        if (run is null)
            throw new HubException($"No active run found for job {jobId.Value}");

        // TODO: Thread a SignalR connection-lifetime CancellationToken through these async I/O calls
        // instead of CancellationToken.None. A disconnect during scope-check will not be observed
        // until the awaits complete. Applies to LoadProviderConfigsAsync, GetProjectByIdAsync, and
        // LoadTemplatesForProjectAsync (via IsInProjectEpicScopeAsync).
        var issueConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var issueConfig = issueConfigs.TryGetProviderConfig(issueProviderConfigId);
        if (issueConfig is null)
            throw new HubException($"Issue provider config '{SanitizeForLog(issueProviderConfigId)}' not found for cross-repo {operationDescription} in job {jobId.Value}");

        // Scope check: the run's own tracker is always in scope. Any other tracker is in scope only for
        // a project epic (see IsInProjectEpicScopeAsync); a repo epic, or a run without a project, may
        // only use its own tracker.
        if (issueProviderConfigId != run.IssueProviderConfigId
            && !await IsInProjectEpicScopeAsync(run, issueProviderConfigId))
        {
            throw new HubException($"Provider '{SanitizeForLog(issueProviderConfigId)}' is not in the scope of job {jobId.Value}: only a project epic may access the trackers of its project's templates");
        }

        return (run, issueConfig);
    }

    /// <summary>
    /// Whether <paramref name="run"/> may access a tracker other than its own. Only a project epic's
    /// decomposition may: a <see cref="PipelineRunType.Decomposition"/> run bound to its project's
    /// epic tracker, operating in the tracker of an enabled template of that project.
    /// Other runs bound to the same tracker may not.
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
        catch (HubException)
        {
            // TODO: If ProviderConfigResolver.ResolveRequiredAsync ever stops logging at Error for
            // the config-not-found path, this catch arm would silently swallow the diagnostic (the
            // HubException is re-thrown but no re-log is emitted here). Consider adding a narrowed
            // re-log if that internal contract cannot be relied upon.
            // (DotNetSpecialist/Correctness review — AgentHub.Decomposition.cs:220)
            //
            // ExecuteWithIssueProviderAsync or ResolveIssueProviderForRunAsync already logged this.
            // More precisely: ExecuteWithIssueProviderAsync logs at Error before re-throwing;
            // ResolveIssueProviderForRunAsync logs at Error for the "no active run" path; and
            // ProviderConfigResolver.ResolveRequiredAsync (called inside ResolveIssueProviderForRunAsync)
            // logs at Error for the config-not-found path before throwing InvalidOperationException,
            // which ResolveIssueProviderForRunAsync wraps as HubException(ex.Message) — so Error is
            // always emitted before the HubException surfaces here.
            // Re-throw unchanged so CreateSignalRPipeline's "Failed to " predicate fires.
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "RequestListOpenIssues unexpected failure for job {JobId} (page={Page}, pageSize={PageSize})",
                jobId.Value, page, pageSize);
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
        catch (HubException)
        {
            // ExecuteWithIssueProviderAsync or ResolveIssueProviderForRunAsync already logged this.
            // Re-throw unchanged so CreateSignalRPipeline's "Failed to " predicate fires.
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "RequestListClosedIssues unexpected failure for job {JobId} (page={Page}, pageSize={PageSize})",
                jobId.Value, page, pageSize);
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
        catch (HubException)
        {
            // ExecuteWithIssueProviderAsync or ResolveIssueProviderForRunAsync /
            // ProviderConfigResolver.ResolveRequiredAsync already logged this at Error.
            // Re-throw unchanged so CreateSignalRPipeline's "Failed to " predicate fires.
            throw;
        }
        catch (Exception ex)
        {
            // Non-HubException paths: genuinely unexpected failures not covered by the inner helpers.
            _logger.Error(ex,
                "RequestGetIssue unexpected failure for job {JobId}, identifier '{Identifier}'",
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
        catch (HubException)
        {
            // ExecuteWithIssueProviderAsync or ResolveIssueProviderForRunAsync already logged this.
            // Re-throw unchanged so CreateSignalRPipeline's "Failed to " predicate fires.
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "RequestListComments unexpected failure for job {JobId}, identifier '{Identifier}'",
                jobId.Value, SanitizeForLog(identifier));
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
                // No paramName: this lambda's parameters are (provider, ct); the message names the comment ID.
                if (!long.TryParse(commentId, out var parsedCommentId))
                    throw new ArgumentException(
                        $"Invalid comment identifier: '{commentId}'. Expected a numeric comment ID.");
                return provider.UpdateCommentAsync(issueId, parsedCommentId, body, ct);
            });
    }
}
