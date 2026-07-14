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
    /// <param name="analysisComment">The newest analysis comment on the issue.</param>
    /// <param name="issueComments">All fetched issue comments (up to 50).</param>
    /// <param name="issueBody">Current issue body text for hash comparison.</param>
    /// <param name="issueIdentifier">Issue identifier for DB queries.</param>
    /// <param name="issueProviderConfigId">Issue provider config ID for DB queries.</param>
    /// <param name="commitThreshold">Commit count threshold (0 = disabled).</param>
    /// <param name="getCommitCount">Delegate to fetch commit count (null = skip signal 3).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Staleness evaluation result including which signal fired (if any).</returns>
    public async Task<StalenessResult> EvaluateAsync(
        IssueComment analysisComment,
        IReadOnlyList<IssueComment> issueComments,
        string issueBody,
        string issueIdentifier,
        string issueProviderConfigId,
        int commitThreshold,
        Func<DateTimeOffset, CancellationToken, Task<int>>? getCommitCount,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(analysisComment);
        ArgumentNullException.ThrowIfNull(issueComments);
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(issueProviderConfigId);

        // TODO: Validate issueBody with ArgumentNullException.ThrowIfNull for consistency
        // with other parameter validation in this method (AnalysisBodyHash.Compute handles null,
        // but null here likely indicates a caller bug).

        // TODO: new DateTimeOffset(analysisComment.CreatedAt, TimeSpan.Zero) will throw ArgumentException
        // if CreatedAt.Kind == DateTimeKind.Local with non-zero system timezone offset. Consider using
        // DateTime.SpecifyKind(analysisComment.CreatedAt, DateTimeKind.Utc) for safety.
        var analysisSince = new DateTimeOffset(analysisComment.CreatedAt, TimeSpan.Zero);

        // Max refresh cap: count hash-marker analyses since last success
        var lastSuccess = await _workItemQuery.GetLastSuccessfulCompletionAsync(
            issueIdentifier, issueProviderConfigId, ct);
        // TODO: Use lastSuccess?.UtcDateTime instead of lastSuccess?.DateTime to avoid
        // Kind=Unspecified DateTime comparison issues if DateTimeOffset ever has non-zero offset.
        // TODO: Potential off-by-one in max refresh cap — the count includes the current
        // (un-refreshed) analysisComment because it also has a hash marker. The first analysis
        // posted for any issue gets a hash marker, so refreshCount starts at 1 before any
        // forced refresh has occurred. With >= 3, only 2 actual forced refreshes happen before
        // suppression. Consider excluding analysisComment.Id from the count (add && c.Id != analysisComment.Id)
        // or raising the threshold to >= 4.
        var refreshCount = issueComments.Count(c =>
            c.Body.Contains(CommentMarkers.AnalysisHeader)
            && AnalysisBodyHash.Extract(c.Body) is not null
            && c.CreatedAt > (lastSuccess?.DateTime ?? DateTime.MinValue));

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
