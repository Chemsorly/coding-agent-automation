using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Evaluates analysis staleness signals to determine if an existing analysis
/// comment should be regenerated. Shared between <see cref="DispatchOrchestrationService"/>
/// (DB mode) and <see cref="AgentJobDispatcher"/> (Legacy/SignalR mode).
///
/// Signal evaluation order (cheapest first, short-circuits on first trigger):
/// 1. body_changed — in-memory hash comparison (negligible cost)
/// 2. agent_error — single DB query (fast, indexed)
/// 3. commit_threshold — external API call (most expensive)
///
/// Max refresh cap: After 3 forced refreshes without a successful run completing,
/// automatic staleness detection is suppressed (requires manual gate-rejection).
/// </summary>
public sealed class AnalysisStalenessDetector
{
    private readonly IWorkItemQueryService _workItemQuery;
    private readonly ILogger _logger;

    public AnalysisStalenessDetector(IWorkItemQueryService workItemQuery, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workItemQuery);
        ArgumentNullException.ThrowIfNull(logger);

        _workItemQuery = workItemQuery;
        _logger = logger;
    }

    /// <summary>Result of staleness evaluation.</summary>
    public sealed record StalenessResult(bool ForceRefresh, string? Signal, int RefreshCount);

    /// <summary>
    /// Evaluates staleness signals for an existing analysis comment.
    /// </summary>
    /// <param name="request">The evaluation request containing all required inputs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Staleness evaluation result including which signal fired (if any).</returns>
    public async Task<StalenessResult> EvaluateAsync(
        StalenessEvaluationRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var analysisComment = request.AnalysisComment;
        var issueComments = request.IssueComments;
        var issueBody = request.IssueBody;
        var issueIdentifier = request.IssueIdentifier;
        var issueProviderConfigId = request.IssueProviderConfigId;
        var commitThreshold = request.CommitThreshold;
        var getCommitCount = request.GetCommitCount;

        ArgumentNullException.ThrowIfNull(analysisComment);
        ArgumentNullException.ThrowIfNull(issueComments);
        ArgumentNullException.ThrowIfNull(issueIdentifier);

        // TODO: Validate issueBody with ArgumentNullException.ThrowIfNull for consistency
        // with other parameter validation in this method (AnalysisBodyHash.Compute handles null,
        // but null here likely indicates a caller bug).

        var analysisSince = new DateTimeOffset(DateTime.SpecifyKind(analysisComment.CreatedAt, DateTimeKind.Utc), TimeSpan.Zero);

        // Max refresh cap: count hash-marker analyses since last success, excluding the
        // current comment being evaluated (it is the "current" analysis, not a prior refresh).
        var lastSuccess = await _workItemQuery.GetLastSuccessfulCompletionAsync(
            issueIdentifier, issueProviderConfigId, ct);
        var refreshCount = issueComments.Count(c =>
            c.Id != analysisComment.Id
            && c.Body.Contains(CommentMarkers.AnalysisHeader)
            && AnalysisBodyHash.Extract(c.Body) is not null
            && c.CreatedAt > (lastSuccess?.UtcDateTime ?? DateTime.MinValue));

        if (refreshCount >= 3)
        {
            _logger.Information(
                "Analysis staleness suppressed for issue {IssueId}: {Count} refreshes without successful run",
                issueIdentifier, refreshCount);
            return new StalenessResult(false, null, refreshCount);
        }

        // Signal 1 (cheapest): Body hash changed — in-memory comparison
        var storedHash = AnalysisBodyHash.Extract(analysisComment.Body);
        if (storedHash is not null)
        {
            var currentHash = AnalysisBodyHash.Compute(issueBody);
            if (storedHash != currentHash)
            {
                _logger.Information(
                    "Analysis force-refresh triggered for issue {IssueId} by signal: {Signal}",
                    issueIdentifier, "body_changed");
                return new StalenessResult(true, "body_changed", refreshCount);
            }
        }

        // Signal 2: Prior AgentError — single DB query
        if (await _workItemQuery.HasAgentErrorSinceAsync(
            issueIdentifier, issueProviderConfigId, analysisSince, ct))
        {
            _logger.Information(
                "Analysis force-refresh triggered for issue {IssueId} by signal: {Signal}",
                issueIdentifier, "agent_error");
            return new StalenessResult(true, "agent_error", refreshCount);
        }

        // Signal 3 (most expensive): Commit threshold — external API call
        if (commitThreshold > 0 && getCommitCount is not null)
        {
            var count = await getCommitCount(analysisSince, ct);
            if (count >= commitThreshold)
            {
                _logger.Information(
                    "Analysis force-refresh triggered for issue {IssueId} by signal: {Signal}",
                    issueIdentifier, "commit_threshold");
                return new StalenessResult(true, "commit_threshold", refreshCount);
            }
        }

        return new StalenessResult(false, null, refreshCount);
    }
}
