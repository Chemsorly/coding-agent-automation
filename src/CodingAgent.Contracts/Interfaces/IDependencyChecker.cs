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
    /// use <see cref="CheckAsync(IssueIdentifier,string?,IIssueProvider,string,IReadOnlyDictionary{string,IIssueProvider},IReadOnlyDictionary{string,string},Dictionary{string,Dictionary{int,bool}},CancellationToken)"/>
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
    /// Numeric references (<c>#N</c>) are checked against <paramref name="defaultProvider"/>.
    /// URL references are matched against <paramref name="providerUrlPrefixes"/> and checked
    /// against the corresponding provider in <paramref name="allProviders"/>.
    /// A URL that matches no prefix is treated as unresolved (blocks dispatch).
    /// </summary>
    /// <param name="issueIdentifier">The issue's own identifier (for self-reference filtering).</param>
    /// <param name="issueBody">The issue body text containing dependency references.</param>
    /// <param name="defaultProvider">Provider for numeric (<c>#N</c>) dependency references.</param>
    /// <param name="defaultProviderId">Provider config ID for <paramref name="defaultProvider"/> (used for cache keying).</param>
    /// <param name="allProviders">Map of provider config ID → provider, for cross-tracker URL resolution.</param>
    /// <param name="providerUrlPrefixes">Map of provider config ID → URL prefix (e.g. <c>"https://github.com/acme/api"</c>), used to match URL references to providers.</param>
    /// <param name="stateCaches">Per-provider state caches keyed by provider config ID. Avoids cross-tracker number collisions.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DependencyCheckResult> CheckAsync(
        IssueIdentifier issueIdentifier,
        string? issueBody,
        IIssueProvider defaultProvider,
        string defaultProviderId,
        IReadOnlyDictionary<string, IIssueProvider> allProviders,
        IReadOnlyDictionary<string, string> providerUrlPrefixes,
        Dictionary<string, Dictionary<int, bool>> stateCaches,
        CancellationToken ct);
}
