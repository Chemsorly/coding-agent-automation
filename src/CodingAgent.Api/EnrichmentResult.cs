using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api;

/// <summary>
/// What <see cref="AssignmentEnricher.EnrichAsync"/> made of a claim (spec 053 DD6). <see cref="Refused"/> is a routing
/// reason the next claim cannot change: <c>GET /assignment</c> fails the work item and answers 410.
/// <see cref="Unavailable"/> is every other reason to serve no assignment, transient ones included: the endpoint
/// answers 503 and the agent retries.
/// </summary>
public abstract record EnrichmentResult
{
    private EnrichmentResult()
    {
    }

    /// <summary>The assignment, with fresh configuration.</summary>
    public sealed record Enriched(JobDistributionRequest Request) : EnrichmentResult;

    /// <summary>A routing refusal: <paramref name="Reason"/> is an <c>AgentRouteRefusalReason</c>, <paramref name="Text"/> the work item's error.</summary>
    public sealed record Refused(string Reason, string Text) : EnrichmentResult;

    /// <summary>No assignment for now (no repository list, a tracker gone, a transient failure).</summary>
    public sealed record Unavailable : EnrichmentResult
    {
        public static Unavailable Instance { get; } = new();
    }
}
