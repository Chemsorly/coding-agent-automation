using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Read-only minimal API endpoints exposing the agent registry.
///
/// <para>
/// Spec 044 moved <c>MapHub&lt;AgentHub&gt;</c> into this process, and <c>AgentHub.RegisterAgent</c>
/// is the only writer of <see cref="IAgentRegistryService"/>. That leaves the API as the sole
/// owner of agent presence, and every other process — the Blazor monolith above all — with no way
/// to see which agents are connected. This group is that window.
/// </para>
///
/// <para>
/// Guarded by <see cref="ApiAuthPolicies.Operator"/>, not <see cref="ApiAuthPolicies.Agent"/>:
/// the response is cluster-wide agent state (hostnames, labels, connection IDs, active job IDs)
/// and an agent pod holding a derived per-pod key has no business enumerating its peers.
/// </para>
/// </summary>
public static class AgentEndpoints
{
    /// <summary>
    /// Maps the agent registry endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agents")
            .RequireAuthorization(ApiAuthPolicies.Operator);

        group.MapGet("/", GetAllAgents);
    }

    // ── GET /api/agents ────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/agents
    /// Returns every agent currently in the registry, regardless of status
    /// (Idle, Busy and Disconnected entries are all included — the UI renders all three).
    /// Always 200; an empty registry returns an empty array.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentEntry"/> is returned as-is rather than through a projection DTO.
    /// It is the exact type <see cref="IAgentRegistryService.GetAllAgents"/> hands back, so a
    /// consumer implementing that interface over HTTP can rebuild its snapshot without a lossy
    /// mapping layer. Its one non-serializable member, <c>SyncRoot</c>, is already
    /// <c>[JsonIgnore]</c>d on the model.
    /// </remarks>
    internal static Ok<IReadOnlyList<AgentEntry>> GetAllAgents(IAgentRegistryService registry)
    {
        return TypedResults.Ok(registry.GetAllAgents());
    }
}
