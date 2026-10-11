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
/// Minimal API endpoints exposing the agent registry and agent-directed commands.
///
/// <para>
/// Spec 044 moved <c>MapHub&lt;AgentHub&gt;</c> into this process, and <c>AgentHub.RegisterAgent</c>
/// is the only writer of <see cref="IAgentRegistryService"/>. That leaves the API as the sole
/// owner of agent presence, and every other process — the Blazor monolith above all — with no way
/// to see which agents are connected or send them messages. This group is that window.
/// </para>
///
/// <para>
/// Guarded by <see cref="ApiAuthPolicies.Operator"/>, not <see cref="ApiAuthPolicies.Agent"/>:
/// the response is cluster-wide agent state (hostnames, labels, connection IDs, active job IDs)
/// and an agent pod holding a derived per-pod key has no business enumerating its peers or
/// sending chat prompts to other agents.
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
        group.MapPost("/{agentId}/chat-prompt", SendChatPrompt);
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
        // Single round-trip to get all active runs, then O(1) per-agent lookup.
        var activeRuns = runService.GetActiveRuns()
            .ToDictionary(r => r.RunId, r => r, StringComparer.OrdinalIgnoreCase);

        var dtos = registry.GetAllAgents()
            .Select(entry =>
            {
                PipelineRun? run = null;
                if (!string.IsNullOrEmpty(entry.ActiveJobId))
                    activeRuns.TryGetValue(entry.ActiveJobId, out run);
                return AgentEntryDtoFactory.From(entry, run);
            })
            .ToArray();

        return TypedResults.Ok<IReadOnlyList<AgentEntryDto>>(dtos);
    }

    // ── POST /api/agents/{agentId}/chat-prompt ─────────────────────────────

    /// <summary>
    /// POST /api/agents/{agentId}/chat-prompt
    ///
    /// Delivers a <see cref="ChatPromptMessage"/> to the agent identified by <paramref name="agentId"/>
    /// over the SignalR hub hosted in this process.
    ///
    /// <para>
    /// The monolith's <c>IHubContext&lt;AgentHub&gt;</c> has no connected clients after Spec 044
    /// moved <c>MapHub</c> here — all agent connections live in this process. This endpoint is the
    /// bridge that lets <c>AgentChat.razor</c> send prompts without holding a hub context.
    /// </para>
    ///
    /// <para>
    /// <b>Session ownership:</b> this endpoint always stamps <c>ActiveChatSessionId</c> on the
    /// agent entry in the authoritative registry store (Redis in distributed mode, in-memory
    /// otherwise) and <em>awaits</em> the write before delivering <c>AssignChatPrompt</c> to the
    /// agent. This guarantees that when the agent calls back with <c>ReportChatResponse</c> or
    /// <c>ReportChatCompleted</c> — which may arrive on a different replica — the ownership check
    /// reads the correct session ID from Redis rather than a replica-local snapshot that was never
    /// updated.
    /// </para>
    /// </summary>
    /// <returns>
    /// 200 OK on success.
    /// 404 Not Found when no agent with <paramref name="agentId"/> is registered.
    /// 409 Conflict when the agent is <see cref="AgentStatus.Disconnected"/>.
    /// </returns>
    internal static async Task<Results<Ok, NotFound<string>, Conflict<string>>> SendChatPrompt(
        string agentId,
        ChatPromptMessage message,
        IAgentRegistryService registry,
        IHubContext<AgentHub, IAgentHubClient> hub)
    {
        // TODO: CancellationToken.None is used here instead of HttpContext.RequestAborted.
        // If the HTTP client disconnects before the registry read completes, the in-flight
        // Redis call is not cancelled. Pass HttpContext.RequestAborted (via a ct parameter on
        // the minimal-API delegate) for consistency with the rest of the file.
        var entry = await registry.GetByAgentIdAsync(agentId, CancellationToken.None);
        if (entry is null)
            return TypedResults.NotFound($"Agent '{agentId}' not found.");

        if (entry.Status == AgentStatus.Disconnected)
            return TypedResults.Conflict($"Agent '{agentId}' is disconnected.");

        // Stamp session ownership in the authoritative store and await completion before
        // delivering AssignChatPrompt. This ensures the hub's ReportChatResponse /
        // ReportChatCompleted ownership check — which reads from the same authoritative store —
        // sees the correct session ID even when those callbacks arrive on a different replica.
        await registry.UpdateAgentFieldAsync(entry.AgentId, AgentFieldNames.ActiveChatSessionId, message.SessionId);

        // TODO: No unit/integration test verifies the ordering guarantee here — that
        // UpdateAgentFieldAsync (the stamp) completes before AssignChatPrompt is delivered.
        // Add a test that blocks UpdateAgentFieldAsync via a TaskCompletionSource and asserts
        // AssignChatPrompt is not invoked until the TCS is released (mirror of
        // ReportChatCompleted_AwaitsUpdateBeforeBroadcast in AgentHubChatTests.cs).
        await hub.Clients.Client(entry.ConnectionId).AssignChatPrompt(message);
        return TypedResults.Ok();
    }
}
