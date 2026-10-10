using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Shared issue operations extracted from AgentHub private helpers.
/// Used by both the hub (for agent-initiated requests) and the lifecycle service
/// (for post-completion label swaps and feedback comments).
/// </summary>
public sealed class AgentIssueOperations : IHubIssueOperations
{
    private readonly IAgentHubFacade _facade;
    private readonly ILabelService _labelService;
    private readonly ILogger _logger;

    public AgentIssueOperations(
        IAgentHubFacade facade,
        ILabelService labelService,
        ILogger logger)
    {
        _facade = facade;
        _labelService = labelService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task SwapLabelAsync(PipelineRun run, string newLabel, CancellationToken ct = default)
    {
        return _labelService.SwapLabelAsync(run.ProviderConfigIdForLabel, run.IssueIdentifier, newLabel, run.LabelTargetKind, ct);
    }

    /// <inheritdoc />
    public async Task<string?> PostCommentViaIssueProviderAsync(PipelineRun run, string body, CancellationToken ct = default)
    {
        try
        {
            var (_, commentUrl) = await PostCommentOnIssueAsync(run, body, ct);
            return commentUrl;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to post comment on issue {IssueIdentifier} for run {RunId}", run.IssueIdentifier, run.RunId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> PostIssueFeedbackCommentAsync(PipelineRun run, CancellationToken ct = default)
    {
        string? commentUrl;
        try
        {
            var comment = FeedbackCommentFormatter.FormatComment(run.Feedback?.Issue);
            if (comment is null)
                return true;

            var (posted, url) = await PostCommentOnIssueAsync(run, comment, ct);
            if (!posted)
                return false;
            commentUrl = url;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: the caller must see the cancellation so it leaves the outbox row for the relay.
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to post issue feedback comment for run {RunId} on issue {IssueIdentifier}",
                run.RunId, run.IssueIdentifier);
            return false;
        }

        _logger.Information("Posted issue feedback comment for run {RunId} on issue {IssueIdentifier}",
            run.RunId, run.IssueIdentifier);

        // Append feedback link to PR body if we have both a URL and a PR.
        // Non-fatal: the comment is already posted, so the result stays true whatever happens here.
        if (commentUrl is not null && !string.IsNullOrEmpty(run.PullRequestNumber))
        {
            await AppendFeedbackLinkToPrBodyAsync(run, commentUrl, ct);
        }

        return true;
    }

    /// <summary>
    /// Posts <paramref name="body"/> on the run's issue. Returns <c>Posted = false</c> when the issue
    /// provider config cannot be resolved; provider failures propagate. <c>Url</c> may be null even
    /// when the comment was posted (the provider could not build one).
    /// </summary>
    private async Task<(bool Posted, string? Url)> PostCommentOnIssueAsync(PipelineRun run, string body, CancellationToken ct)
    {
        var issueConfig = await ProviderConfigResolver.TryResolveAsync(
            () => _facade.GetProviderConfigByIdAsync(run.IssueProviderConfigId, ProviderKind.Issue, ct),
            run.IssueProviderConfigId, ProviderKind.Issue, _logger);
        if (issueConfig is null)
            return (false, null);

        await using var issueProvider = _facade.CreateIssueProvider(issueConfig);
        // Validate initializes provider state (e.g., GitLab PathWithNamespace) needed for URL construction
        await issueProvider.ValidateAsync(ct);
        return (true, await issueProvider.PostCommentAsync(run.IssueIdentifier, body, ct));
    }

    /// <summary>
    /// Appends a feedback comment link section to the existing PR body.
    /// Fetches current body from provider to avoid stale-state overwrites.
    /// Idempotent: skips if feedback section already present.
    /// Non-fatal: logs warning on failure.
    /// </summary>
    private async Task AppendFeedbackLinkToPrBodyAsync(PipelineRun run, string commentUrl, CancellationToken ct)
    {
        try
        {
            // Idempotency guard: don't append twice if retried
            if (run.PullRequestBody?.Contains("## Agent Feedback") == true)
            {
                _logger.Debug("Feedback link already present in PR body for run {RunId}, skipping", run.RunId);
                return;
            }

            var repoConfig = await ProviderConfigResolver.TryResolveAsync(
                () => _facade.GetProviderConfigByIdAsync(run.RepoProviderConfigId, ProviderKind.Repository, ct),
                run.RepoProviderConfigId, ProviderKind.Repository, _logger);
            if (repoConfig is null)
                return;

            if (!int.TryParse(run.PullRequestNumber, out var prNumber))
                return;

            await using var repoProvider = _facade.CreateRepositoryProvider(repoConfig);

            // Fetch current body from provider to avoid overwriting external edits
            var currentBody = await repoProvider.GetPullRequestBodyAsync(prNumber, ct)
                              ?? run.PullRequestBody
                              ?? "";

            // Double-check idempotency against remote body (may have been appended by a prior attempt)
            if (currentBody.Contains("## Agent Feedback"))
                return;

            var feedbackSection = $"\n\n## Agent Feedback\n⚠️ Agent posted feedback on the issue [here]({commentUrl}). Read before merging.";
            var newBody = currentBody + feedbackSection;

            await repoProvider.UpdatePullRequestAsync(prNumber, newBody, null, ct);
            run.PullRequestBody = newBody;

            _logger.Information("Appended feedback link to PR #{PrNumber} for run {RunId}", run.PullRequestNumber, run.RunId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to append feedback link to PR #{PrNumber} for run {RunId}", run.PullRequestNumber, run.RunId);
        }
    }
}
