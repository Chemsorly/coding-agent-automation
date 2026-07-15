using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Shared issue operations extracted from AgentHub private helpers.
/// Used by both the hub (for agent-initiated requests) and the lifecycle service
/// (for post-completion label swaps and feedback comments).
/// </summary>
public sealed class AgentIssueOperations : IHubIssueOperations
{
    private readonly IAgentHubFacade _facade;
    private readonly ILabelSwapper _labelSwapper;
    private readonly ILogger _logger;

    public AgentIssueOperations(
        IAgentHubFacade facade,
        ILabelSwapper labelSwapper,
        ILogger logger)
    {
        _facade = facade;
        _labelSwapper = labelSwapper;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task SwapLabelAsync(PipelineRun run, string newLabel, LabelTargetKind targetKind)
    {
        var providerConfigId = targetKind == LabelTargetKind.PullRequest
            ? run.RepoProviderConfigId
            : run.IssueProviderConfigId;

        return _labelSwapper.SwapLabelAsync(providerConfigId, run.IssueIdentifier, newLabel, targetKind, CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task<string?> PostCommentViaIssueProviderAsync(PipelineRun run, string body)
    {
        try
        {
            var issueConfig = await _facade.GetProviderConfigByIdAsync(run.IssueProviderConfigId, ProviderKind.Issue, CancellationToken.None);
            if (issueConfig is null)
            {
                _logger.Warning("Issue provider config '{ConfigId}' not found for run {RunId}", run.IssueProviderConfigId, run.RunId);
                return null;
            }

            await using var issueProvider = _facade.CreateIssueProvider(issueConfig);
            // Validate initializes provider state (e.g., GitLab PathWithNamespace) needed for URL construction
            await issueProvider.ValidateAsync(CancellationToken.None);
            return await issueProvider.PostCommentAsync(run.IssueIdentifier, body, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to post comment on issue {IssueIdentifier} for run {RunId}", run.IssueIdentifier, run.RunId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task PostIssueFeedbackCommentAsync(PipelineRun run)
    {
        try
        {
            var comment = FeedbackCommentFormatter.FormatComment(run.Feedback?.Issue);
            if (comment is null)
                return;

            var commentUrl = await PostCommentViaIssueProviderAsync(run, comment);
            _logger.Information("Posted issue feedback comment for run {RunId} on issue {IssueIdentifier}",
                run.RunId, run.IssueIdentifier);

            // Append feedback link to PR body if we have both a URL and a PR
            if (commentUrl is not null && !string.IsNullOrEmpty(run.PullRequestNumber))
            {
                await AppendFeedbackLinkToPrBodyAsync(run, commentUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to post issue feedback comment for run {RunId} on issue {IssueIdentifier}",
                run.RunId, run.IssueIdentifier);
        }
    }

    /// <summary>
    /// Appends a feedback comment link section to the existing PR body.
    /// Fetches current body from provider to avoid stale-state overwrites.
    /// Idempotent: skips if feedback section already present.
    /// Non-fatal: logs warning on failure.
    /// </summary>
    private async Task AppendFeedbackLinkToPrBodyAsync(PipelineRun run, string commentUrl)
    {
        try
        {
            // Idempotency guard: don't append twice if retried
            if (run.PullRequestBody?.Contains("## Agent Feedback") == true)
            {
                _logger.Debug("Feedback link already present in PR body for run {RunId}, skipping", run.RunId);
                return;
            }

            var repoConfig = await _facade.GetProviderConfigByIdAsync(run.RepoProviderConfigId, ProviderKind.Repository, CancellationToken.None);
            if (repoConfig is null)
            {
                _logger.Warning("Repo provider config '{ConfigId}' not found for run {RunId}, skipping feedback link", run.RepoProviderConfigId, run.RunId);
                return;
            }

            if (!int.TryParse(run.PullRequestNumber, out var prNumber))
                return;

            await using var repoProvider = _facade.CreateRepositoryProvider(repoConfig);

            // Fetch current body from provider to avoid overwriting external edits
            var currentBody = await repoProvider.GetPullRequestBodyAsync(prNumber, CancellationToken.None)
                              ?? run.PullRequestBody
                              ?? "";

            // Double-check idempotency against remote body (may have been appended by a prior attempt)
            if (currentBody.Contains("## Agent Feedback"))
                return;

            var feedbackSection = $"\n\n## Agent Feedback\n⚠️ Agent posted feedback on the issue [here]({commentUrl}). Read before merging.";
            var newBody = currentBody + feedbackSection;

            await repoProvider.UpdatePullRequestAsync(prNumber, newBody, false, CancellationToken.None);
            run.PullRequestBody = newBody;

            _logger.Information("Appended feedback link to PR #{PrNumber} for run {RunId}", run.PullRequestNumber, run.RunId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to append feedback link to PR #{PrNumber} for run {RunId}", run.PullRequestNumber, run.RunId);
        }
    }
}
