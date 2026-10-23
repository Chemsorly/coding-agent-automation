using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgent.Api;

/// <summary>
/// Minimal API endpoints for control-plane Work Item dispatch and lifecycle routes:
/// create, dispatch a Pending item, priority, is-distributed, and active-identifiers.
/// </summary>
public static class WorkItemDispatchEndpoints
{
    /// <summary>
    /// Maps control-plane dispatch and lifecycle work item endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapWorkItemDispatchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work-items")
            .RequireAuthorization(ApiAuthPolicies.Agent);

        // ── Control-plane endpoints ────────────────────────────────────────
        // Called by the Job Controller and the monolith, which authenticate with the master
        // key (operator tier). No agent pod calls these, so agent-derived keys are refused
        // outright — otherwise a single compromised pod could create, dispatch or enumerate
        // every work item in the cluster.
        group.MapPost("/", CreateWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/dispatch", DispatchPendingWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/priority", PostPriorityWeight).RequireAuthorization(ApiAuthPolicies.Operator);

        group.MapGet("/is-distributed", GetIsDistributed).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/active-identifiers", GetActiveIdentifiers).RequireAuthorization(ApiAuthPolicies.Operator);
    }

    // ── POST / — create WorkItem ───────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items
    /// Creates a new WorkItem with Status=Pending from a JobDistributionRequest.
    /// The work is done by <see cref="WorkItemStore.CreateAsync"/>, which the in-process
    /// <see cref="ApiWorkDistributor"/> shares.
    /// Also materialises an in-memory <see cref="PipelineRun"/> in <see cref="IOrchestratorRunService"/>
    /// so the UI can show the run immediately (Option A, Req 1a.1).
    /// Returns 201 + new GUID. Maps Postgres 23505 unique violation to 409 Conflict.
    /// <para>
    /// The API routes the request here (spec 053 DD1): the caller sends the task type and the repository, and
    /// <see cref="WorkItemRouter"/> pins the profile, model and effort. When no route resolves, or the route cannot
    /// run (for consolidation: its job template is missing, #3642), the request is refused with 422 and a
    /// ProblemDetails <c>detail</c> that names the reason, before any row or in-memory run exists (DD2).
    /// </para>
    /// <para>
    /// Only identity fields are serialized to <c>WorkItems.Payload</c> (issue #2171).
    /// Mutable config (ProviderConfigs, QualityGateConfigs, RepoSteeringContent, etc.) is stripped
    /// from the stored payload; <c>GET /api/work-items/{id}/assignment</c> fetches it fresh at
    /// dispatch time via <see cref="AssignmentEnricher"/>.
    /// </para>
    /// </summary>
    internal static async Task<IResult> CreateWorkItem(
        [FromBody] JobDistributionRequest request,
        WorkItemStore store,
        CancellationToken ct = default)
    {
        var (outcome, workItemId, reason) = await store.CreateAsync(request, ct);
        return outcome switch
        {
            // 422 Unprocessable Entity — a permanent configuration error, distinct from 409 (a live run already exists).
            WorkItemCreateOutcome.Refused => TypedResults.Problem(
                detail: reason, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Work item refused"),
            WorkItemCreateOutcome.LiveItemExists => DispatchWorkItemService.HandleUniqueViolationFallback(),
            _ => TypedResults.Created($"/api/work-items/{workItemId}", workItemId),
        };
    }

    // ── POST /{id}/dispatch — claim-by-CAS dispatch for a single-owner Scheduler ──

    /// <summary>
    /// POST /api/work-items/{id}/dispatch
    /// Claims an existing <c>Pending</c> WorkItem by CAS and creates the K8s Job atomically.
    /// Designed for the leader-elected Scheduler poller (issue #2541) which enqueues items as
    /// <c>Pending</c> via <c>POST /api/work-items</c> and then dispatches them via this endpoint.
    ///
    /// <para>
    /// Wraps the snapshot → gate → CAS in a Postgres advisory lock keyed on the normalized
    /// agent selector to prevent the over-dispatch window where two concurrent calls for
    /// different Pending items of the same selector both observe capacity available.
    /// </para>
    ///
    /// Returns:
    /// <list type="bullet">
    ///   <item>200 <c>{ "dispatched": true }</c> — dispatch succeeded (K8s Job running, WorkItem=Dispatched)</item>
    ///   <item>200 <c>{ "dispatched": false, "reason": "not_pending" | "no_template" | "concurrency_limit" }</c>
    ///     — expected backpressure outcome; Scheduler stops this selector for the cycle</item>
    ///   <item>200 <c>{ "dispatched": false, "reason": "no_route" }</c> — an item stored without a selector (spec 053
    ///     rollout) whose route is refused; it is failed with the reason</item>
    ///   <item>404 Not Found — no WorkItem with this ID</item>
    ///   <item>503 Service Unavailable — no PVC available, advisory lock timeout, or K8s Job creation failed</item>
    /// </list>
    /// </summary>
    internal static async Task<IResult> DispatchPendingWorkItem(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        DispatchLifecycleService lifecycle,
        IDistributedLockProvider lockProvider,
        DispatchWorkItemService dispatchService,
        RolloutRouting rollout,
        CancellationToken ct = default)
    {
        // Delegate entirely to the service method which owns the full dispatch lifecycle
        // (fast-path check, advisory lock, concurrency gate, PVC gate, K8s Job creation,
        // result interpretation, and telemetry). The static endpoint exists as the ASP.NET
        // Core minimal-API handler that the DI container resolves parameters for.
        return await dispatchService.DispatchPendingWorkItemAsync(id, dbFactory, lifecycle, lockProvider, rollout, ct);
    }

    /// <summary>
    /// Loads project secrets for a work item if a project is configured.
    /// The prepareVariant body of <see cref="DispatchPendingWorkItem"/>.
    /// Called by <see cref="CodingAgent.Api.Dispatch.DispatchWorkItemService.RunDispatchLifecycleAsync"/>
    /// to invoke the shared variant preparation logic without a delegate parameter.
    /// </summary>
    internal static async Task<(bool shouldContinue, Dictionary<string, string>? projectSecrets)> PrepareDispatchVariantAsync(
        PipelineDbContext db,
        WorkItemEntity workItem,
        CancellationToken ct)
    {
        Dictionary<string, string>? projectSecrets = null;
        if (workItem.ProjectId.HasValue)
            projectSecrets = await DispatchLifecycleService.LoadProjectSecretsAsync(
                db, workItem.ProjectId.Value.ToString(), ct);
        return (shouldContinue: true, projectSecrets);
    }


    // ── GET /is-distributed ───────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/is-distributed?issueIdentifier=...&amp;issueProviderConfigId=...
    /// Returns true when a non-terminal WorkItem exists for this issue, OR when a WorkItem
    /// was recently terminated (within <see cref="PipelineConstants.DefaultRestartDedupCooldown"/>).
    /// Used by KubernetesWorkDistributor.IsIssueDistributedAsync for dispatch deduplication.
    /// 200 with { isDistributed: bool }.
    /// </summary>
    internal static async Task<IResult> GetIsDistributed(
        string issueIdentifier,
        string issueProviderConfigId,
        WorkItemStore store,
        CancellationToken ct) =>
        TypedResults.Ok(new { isDistributed = await store.IsDistributedAsync(issueIdentifier, issueProviderConfigId, ct) });

    // ── GET /active-identifiers ───────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/active-identifiers
    /// Returns the set of (IssueIdentifier, IssueProviderConfigId) pairs that have active
    /// (non-terminal) WorkItems OR were recently terminated.
    /// Used by KubernetesWorkDistributor.GetActiveIssueIdentifiersAsync for dispatch deduplication.
    /// 200 with array of { issueIdentifier, issueProviderConfigId }.
    /// </summary>
    internal static async Task<IResult> GetActiveIdentifiers(
        WorkItemStore store,
        CancellationToken ct) =>
        TypedResults.Ok((IReadOnlyList<ActiveIdentifierDto>)(await store.GetActiveIdentifiersAsync(ct))
            .Select(p => new ActiveIdentifierDto(p.IssueIdentifier, p.IssueProviderConfigId))
            .ToList());

    /// <summary>
    /// DTO returned by <see cref="GetActiveIdentifiers"/>. Named type eliminates the
    /// S1944 suspicious-cast warning and preserves field-name stability on the wire.
    /// </summary>
    internal sealed record ActiveIdentifierDto(string IssueIdentifier, string IssueProviderConfigId);

    // ── POST /{id}/priority ───────────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/priority
    /// Body: { "priorityWeight": int }
    /// Sets <see cref="WorkItemEntity.PriorityWeight"/> on a Pending work item.
    /// Returns 200 on success.
    /// Returns 400 if <c>priorityWeight</c> is outside [0, 1000].
    /// Returns 409 if the item is not in Pending status.
    /// Returns 404 if the item does not exist.
    /// </summary>
    internal static async Task<IResult> PostPriorityWeight(
        Guid id,
        [FromBody] PriorityWeightRequest request,
        WorkItemTransitionService transitionService,
        CancellationToken ct)
    {
        if (request.PriorityWeight is null)
            return TypedResults.BadRequest("priorityWeight is required.");

        if (request.PriorityWeight < 0 || request.PriorityWeight > 1000)
            return TypedResults.BadRequest("priorityWeight must be between 0 and 1000 (inclusive).");

        var result = await transitionService.UpdatePriorityWeightAsync(id, request.PriorityWeight.Value, ct);

        return result switch
        {
            Infrastructure.Persistence.Services.UpdatePriorityWeightResult.Success => TypedResults.Ok(),
            Infrastructure.Persistence.Services.UpdatePriorityWeightResult.NotPending =>
                TypedResults.Conflict("Cannot update PriorityWeight: work item is not in Pending status."),
            Infrastructure.Persistence.Services.UpdatePriorityWeightResult.ConcurrencyConflict =>
                TypedResults.Conflict("Cannot update PriorityWeight: concurrent update conflict, please retry."),
            _ => TypedResults.NotFound()
        };
    }

}

/// <summary>
/// Request body for POST /api/work-items/{id}/priority.
/// </summary>
public sealed class PriorityWeightRequest
{
    /// <summary>
    /// Dispatch priority weight. Must be between 0 and 1000 (inclusive). Required.
    /// </summary>
    public int? PriorityWeight { get; init; }
}
