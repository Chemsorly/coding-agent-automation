using CodingAgent.Api.Dispatch;
using CodingAgent.AgentGateway;
using CodingAgent.Contracts;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CodingAgent.Api;

/// <summary>
/// Minimal API endpoints exposing the agent registry and the Kiro credential pool.
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
/// and an agent pod holding a derived per-pod key has no business enumerating its peers. Chat
/// prompts go through <c>/api/ui/chat</c> since spec 051 2.5b, where only the chat's owner may send them.
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
        group.MapGet("/credential-pool", GetCredentialPool);
    }

    // ── GET /api/agents/credential-pool ──────────────────────────────────────

    /// <summary>
    /// GET /api/agents/credential-pool
    /// Returns the Kiro credential (PVC) pool snapshot for the Fleet screen: configured slots,
    /// how many are free, and how many are claimed by active work. Total 0 = pooling not configured.
    /// </summary>
    internal static async Task<Ok<CredentialPoolStatus>> GetCredentialPool(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var pool = DispatchServiceOptionsFactory.Create(configuration).KiroPvcPool;
        if (pool.Count == 0)
            return TypedResults.Ok(new CredentialPoolStatus(0, 0, 0));   // pooling not configured

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var availability = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pool, ct);
        return TypedResults.Ok(new CredentialPoolStatus(pool.Count, availability.AvailablePvcs.Count, availability.ClaimedCount));
    }

    // ── GET /api/agents ────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/agents
    /// Returns every agent currently in the registry, regardless of status
    /// (Idle, Busy and Disconnected entries are all included — the UI renders all three),
    /// enriched with issue/run/PR context from the active run service.
    /// Always 200; an empty registry returns an empty array.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <see cref="AgentEntryDto"/> rather than <see cref="AgentEntry"/> directly.
    /// The original rationale for returning <see cref="AgentEntry"/> as-is (no projection DTO)
    /// no longer holds: the new enrichment fields (<c>ActiveIssueUrl</c>, <c>ActiveRunId</c>,
    /// <c>ActivePullRequestUrl</c>, etc.) are not properties of <see cref="AgentEntry"/> and
    /// require a join with <see cref="IOrchestratorRunService"/>, making a DTO projection the
    /// only correct approach.
    /// </para>
    /// <para>
    /// Active runs are fetched in a single <see cref="IOrchestratorRunService.GetActiveRuns"/>
    /// call and indexed by <c>RunId</c> to avoid N+1 lookups in Redis-backed deployments.
    /// When an agent's <c>ActiveJobId</c> maps to a run not currently in the service (e.g. run
    /// completed between registry read and enrichment), all enrichment fields for that agent
    /// are null — consistent with the requirement "Links must only render when data is present".
    /// </para>
    /// </remarks>
    internal static Ok<IReadOnlyList<AgentEntryDto>> GetAllAgents(
        IAgentRegistryService registry,
        IOrchestratorRunService runService)
    {
        var dtos = ListAgents(registry, runService).Select(a => a.Agent).ToArray();
        return TypedResults.Ok<IReadOnlyList<AgentEntryDto>>(dtos);
    }

    /// <summary>
    /// Every registered agent, enriched from its active run, with the project of that run (null for an
    /// agent without one). <c>GET /api/ui/agents</c> filters on the project.
    /// </summary>
    internal static IEnumerable<(AgentEntryDto Agent, string? ProjectId)> ListAgents(
        IAgentRegistryService registry,
        IOrchestratorRunService runService)
    {
        // Single round-trip to get all active runs, then O(1) per-agent lookup.
        var activeRuns = runService.GetActiveRuns()
            .ToDictionary(r => r.RunId, r => r, StringComparer.OrdinalIgnoreCase);

        return registry.GetAllAgents()
            .Select(entry =>
            {
                PipelineRun? run = null;
                if (!string.IsNullOrEmpty(entry.ActiveJobId))
                    activeRuns.TryGetValue(entry.ActiveJobId, out run);
                return (AgentEntryDtoFactory.From(entry, run), run?.ProjectId);
            })
            .ToArray();
    }
}
