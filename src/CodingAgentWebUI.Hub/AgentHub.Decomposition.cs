using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgentWebUI.Hub;

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

        // TODO: Thread a SignalR connection-lifetime CancellationToken through both async I/O calls
        // below (LoadProviderConfigsAsync and LoadTemplatesForProjectAsync) instead of using
        // CancellationToken.None. If the agent disconnects during the scope-check phase, these awaits
        // will run to completion against a now-dead connection context. This method is the only
        // location in the decomposition file with multiple uncancellable async I/O calls.
        var issueConfigs = await _facade.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == issueProviderConfigId);
        if (issueConfig is null)
            throw new HubException($"Issue provider config '{issueProviderConfigId}' not found for cross-repo routing in job {jobId.Value}");

        // Scope check: ensure the requested provider belongs to the run's project.
        // Fast path: the run's own provider is always in scope — no template lookup needed.
        // Backward compat: when ProjectId is null/empty (legacy runs), any system-wide provider is accepted.
        if (issueProviderConfigId != run.IssueProviderConfigId && !string.IsNullOrEmpty(run.ProjectId))
        {
            var templates = await _facade.LoadTemplatesForProjectAsync(run.ProjectId, CancellationToken.None);
            var allowedProviders = templates.Select(t => t.IssueProviderId).ToHashSet();
            if (!allowedProviders.Contains(issueProviderConfigId))
                throw new HubException($"Provider '{issueProviderConfigId}' is not part of the run's project '{run.ProjectId}'");
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
    /// Scope check: agents may only read the issue they are working on (their own run's issue identifier).
    /// This prevents an agent from enumerating issues it was not assigned to within the configured repository.
    /// </summary>
    [RequiresActiveJob]
    public Task<IssueDetail> RequestGetIssue(JobId jobId, string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        // Security scope check (1G-006): restrict identifier to the run's own issue.
        // Agents should only read their assigned issue, not arbitrary issues in the repo.
        var run = _facade.GetRun(jobId);
        if (run is null)
            throw new HubException($"No active run found for job {jobId.Value}");

        if (!string.Equals(identifier, run.IssueIdentifier, StringComparison.Ordinal))
        {
            _logger.Warning(
                "RequestGetIssue: agent {AgentId} attempted to read issue '{RequestedIdentifier}' " +
                "but is only authorized for '{OwnIdentifier}' (job {JobId}). Ignoring.",
                run.AgentId, SanitizeForLog(identifier), SanitizeForLog(run.IssueIdentifier), jobId.Value);
            throw new HubException($"Not authorized to read issue '{identifier}': agent may only read its own assigned issue.");
        }

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
}
