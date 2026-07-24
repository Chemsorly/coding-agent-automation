using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Provides query-only access to WorkItem data for staleness detection.
/// Extracted from <see cref="CodingAgentWebUI.Infrastructure.Persistence.Services.WorkItemTransitionService"/>
/// to enable mocking in orchestration-layer unit tests.
/// </summary>
public interface IWorkItemQueryService
{
    /// <summary>
    /// Returns true if any WorkItem for the given issue failed with
    /// <see cref="Models.FailureReason.AgentError"/> after the specified timestamp.
    /// Only considers <c>AgentError</c>; <c>Timeout</c>, <c>InfrastructureFailure</c>,
    /// and <c>TokenRefreshFailure</c> are excluded.
    /// </summary>
    /// <param name="issueIdentifier">The issue identifier (e.g., "owner/repo#42").</param>
    /// <param name="issueProviderConfigId">Issue provider config ID for disambiguation.</param>
    /// <param name="since">Only consider WorkItems completed after this timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> HasAgentErrorSinceAsync(
        string issueIdentifier, ProviderConfigId issueProviderConfigId,
        DateTimeOffset since, CancellationToken ct);

    /// <summary>
    /// Returns the <see cref="DateTimeOffset"/> of the most recent successful WorkItem
    /// completion for the given issue, or null if no successes exist.
    /// Used by the max-refresh cap to determine the reset point.
    /// </summary>
    /// <param name="issueIdentifier">The issue identifier.</param>
    /// <param name="issueProviderConfigId">Issue provider config ID for disambiguation.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DateTimeOffset?> GetLastSuccessfulCompletionAsync(
        string issueIdentifier, ProviderConfigId issueProviderConfigId,
        CancellationToken ct);
}
