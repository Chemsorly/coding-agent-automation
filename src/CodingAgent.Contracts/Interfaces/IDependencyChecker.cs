using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Checks whether all dependency references in an issue body are satisfied (closed).
/// </summary>
public interface IDependencyChecker
{
    /// <summary>
    /// Checks dependencies for a single issue using a single issue provider.
    /// URL-based dependency references (cross-tracker) are not resolved by this overload —
    /// use <see cref="CheckAsync(IssueIdentifier,string?,DependencyRoutingContext,CancellationToken)"/>
    /// for full cross-tracker support.
    /// </summary>
    /// <param name="issueIdentifier">The issue's own identifier (for self-reference filtering).</param>
    /// <param name="issueBody">The issue body text containing dependency references.</param>
    /// <param name="issueProvider">Provider to check referenced issue states.</param>
    /// <param name="stateCache">Shared cache for issue state lookups within a poll cycle.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DependencyCheckResult> CheckAsync(
        IssueIdentifier issueIdentifier,
        string? issueBody,
        IIssueProvider issueProvider,
        Dictionary<int, bool> stateCache,
        CancellationToken ct);

    /// <summary>
    /// Checks dependencies for a single issue with full cross-tracker URL routing support.
    /// Numeric references (<c>#N</c>) are checked against <see cref="DependencyRoutingContext.DefaultProvider"/>.
    /// URL references are matched against <see cref="DependencyRoutingContext.ProviderUrlPrefixes"/> and
    /// checked against the corresponding provider in <see cref="DependencyRoutingContext.AllProviders"/>.
    /// A URL that matches no prefix is treated as unresolved (blocks dispatch).
    /// </summary>
    /// <param name="issueIdentifier">The issue's own identifier (for self-reference filtering).</param>
    /// <param name="issueBody">The issue body text containing dependency references.</param>
    /// <param name="routing">
    /// Default provider and ID, all providers with their URL prefixes, and the per-provider state
    /// caches keyed by provider config ID (shared across calls within a poll cycle).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<DependencyCheckResult> CheckAsync(
        IssueIdentifier issueIdentifier,
        string? issueBody,
        DependencyRoutingContext routing,
        CancellationToken ct);
}
