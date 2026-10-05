using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Provider routing and caching inputs for the cross-tracker
/// <see cref="IDependencyChecker.CheckAsync(IssueIdentifier, string?, DependencyRoutingContext, CancellationToken)"/>
/// overload. Typically built once per poll cycle and reused for every issue checked in that cycle.
/// </summary>
public sealed record DependencyRoutingContext
{
    /// <summary>Provider for numeric (<c>#N</c>) dependency references.</summary>
    public required IIssueProvider DefaultProvider { get; init; }

    /// <summary>Provider config ID for <see cref="DefaultProvider"/> (used for cache keying).</summary>
    public required string DefaultProviderId { get; init; }

    /// <summary>Map of provider config ID → provider, for cross-tracker URL resolution.</summary>
    public required IReadOnlyDictionary<string, IIssueProvider> AllProviders { get; init; }

    /// <summary>
    /// Map of provider config ID → URL prefix (e.g. <c>"https://github.com/acme/api"</c>),
    /// used to match URL references to providers.
    /// </summary>
    public required IReadOnlyDictionary<string, string> ProviderUrlPrefixes { get; init; }

    /// <summary>
    /// Per-provider state caches keyed by provider config ID. Avoids cross-tracker number collisions.
    /// Mutated by the checker (shared across calls within a poll cycle).
    /// </summary>
    public required Dictionary<string, Dictionary<int, bool>> StateCaches { get; init; }
}
