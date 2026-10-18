using System.Diagnostics;
using System.Linq.Expressions;
using System.Text.Json;
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
    /// A Consolidation request whose agent selector has no job template — directly or through the profile
    /// fallback, resolved exactly as <c>DispatchPendingWorkItem</c> does — is refused with 422 before any row
    /// or in-memory run exists (issue #3642). The dispatcher could never start it: it would stay Pending for
    /// ever and be deferred as <c>no_template</c> every cycle. Templates are read from a ConfigMap at start-up,
    /// so unlike a transient dispatch failure the next cycle cannot fix it. Other task types are not checked
    /// here; their missing-template case is still deferred by the pending-dispatch path.
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
        DispatchWorkItemService dispatchService,
        DispatchTemplateResolver templateResolver,
        CancellationToken ct = default)
    {
        // Refuse a consolidation run the dispatcher could never start, before anything is written. The lookup is
        // the dispatcher's own (direct, then the profile fallback). It runs without the expansion warning: the
        // dispatcher logs that when it dispatches, and consolidation selectors are routinely partial.
        if (request.TaskType == WorkItemTaskType.Consolidation)
        {
            var (template, _) = await dispatchService.ResolveTemplateAsync(
                request.AgentSelector ?? "", templateResolver, "CreateWorkItem", warnOnExpansion: false, ct);
            if (template is null)
            {
                Log.Warning("CreateWorkItem: no job template for consolidation selector {Selector} — returning 422",
                    LogSanitizer.SanitizeForLog(request.AgentSelector));
                // 422 Unprocessable Entity — permanent config error, distinct from 409 (a live run already exists).
                return TypedResults.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }
        }

        var (outcome, workItemId) = await store.CreateAsync(request, ct);
        return outcome == WorkItemCreateOutcome.LiveItemExists
            ? DispatchWorkItemService.HandleUniqueViolationFallback()
            : TypedResults.Created($"/api/work-items/{workItemId}", workItemId);
    }

    /// <summary>
    /// Builds the WorkItem row for a request: identity-only payload, agent selector, timeout and priority.
    /// Shared with the triage service, which inserts a triage and its first WorkItem in one transaction.
    /// </summary>
    internal static WorkItemEntity BuildWorkItemEntity(JobDistributionRequest request, Guid workItemId)
    {
        // Serialize only identity fields to the payload (issue #2171).
        // Mutable config (ProviderConfigs, QGs, steering, MCP servers, issue context) is fetched
        // fresh at GetAssignment time. This prevents stale config being served to agents that
        // were queued for extended periods.
        var minimalPayload = BuildMinimalPayload(request);
        var payloadJson = JsonSerializer.Serialize(minimalPayload, PipelineJsonOptions.Default);

        return new WorkItemEntity
        {
            Id = workItemId,
            TaskType = request.TaskType,
            IssueIdentifier = request.IssueIdentifier.Value,
            IssueProviderConfigId = request.IssueProviderConfigId,
            Status = WorkItemStatus.Pending,
            Payload = payloadJson,
            AgentSelector = request.AgentSelector ?? "",
            // Clamp zero/negative TimeoutSeconds to DefaultAgentTimeout (issue #2745).
            // A zero stored value causes ReconciliationLoop to immediately force-fail Running items
            // because the elapsed time always exceeds the zero timeout.
            TimeoutSeconds = request.TimeoutSeconds > 0
                ? request.TimeoutSeconds
                : (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds,
            ProjectId = request.ProjectId,
            CreatedAt = DateTimeOffset.UtcNow,
            PriorityWeight = InitiatedByConstants.IsManual(request.InitiatedBy) ? 100 : 0,
            // Capture the W3C traceparent from the current API span so the worker K8s Job
            // can restore it and attach its spans to this trace rather than starting a new root.
            // Activity.Current here is the ASP.NET Core request span — the API span that the
            // caller's trace is already a child of — which is exactly the correct parent.
            // Exception: when the request carries a pre-stored TraceContext (e.g., consolidation
            // rehydration at startup where Activity.Current is null), prefer that instead.
            TraceParent = request.TraceContext?.GetValueOrDefault("traceparent")
                ?? PipelineTelemetry.FormatTraceParent(Activity.Current)
        };
    }

    /// <summary>
    /// Builds a minimal <see cref="JobDistributionRequest"/> containing only identity fields
    /// that are stored in <c>WorkItems.Payload</c>. Strips all mutable config that will be
    /// re-fetched at assignment time.
    /// </summary>
    internal static JobDistributionRequest BuildMinimalPayload(JobDistributionRequest request)
    {
        return new JobDistributionRequest
        {
            // Identity / non-reconstructable fields
            IssueIdentifier = request.IssueIdentifier,
            IssueProviderConfigId = request.IssueProviderConfigId,
            RepoProviderConfigId = request.RepoProviderConfigId,
            BrainProviderConfigId = request.BrainProviderConfigId,
            PipelineProviderConfigId = request.PipelineProviderConfigId,
            InitiatedBy = request.InitiatedBy,
            TaskType = request.TaskType,
            AgentSelector = request.AgentSelector,
            TimeoutSeconds = request.TimeoutSeconds,
            ProjectId = request.ProjectId,
            ProjectName = request.ProjectName,
            RunType = request.RunType,
            RunId = request.RunId,
            TraceContext = request.TraceContext,

            // Audit / routing identity
            // (ProjectName and InitiatedBy also kept above for GetPendingWorkItems display)

            // Review-specific identity (not trivially re-fetchable at assignment time)
            LinkedPullRequest = request.LinkedPullRequest,
            ReviewPrTargetBranch = request.ReviewPrTargetBranch,
            ReviewPrDescription = request.ReviewPrDescription,
            ReviewPrAuthor = request.ReviewPrAuthor,

            // Decomposition identity
            // ProjectContext is pre-built by DispatchOrchestrationService.BuildDecompositionProjectContextAsync
            // and consumed by WriteProjectContextStep, CloneProjectRepositoriesStep, DecompositionAnalysisStep,
            // CreateSubIssuesStep, and AgentProviderResolver. It cannot be reconstructed at assignment time.
            ProjectContext = request.ProjectContext,
            DecompositionSource = request.DecompositionSource,

            // Review-specific pre-fetched context
            // LinkedIssueContexts is pre-fetched by DispatchOrchestrationService and consumed by
            // ExtractLinkedIssuesStep and JobAssignmentMessageFactory. It is not re-fetched by
            // AssignmentEnricher.EnrichCoreAsync, so it must be preserved here.
            LinkedIssueContexts = request.LinkedIssueContexts,

            // Consolidation identity
            ConsolidationRunType = request.ConsolidationRunType,
            ConsolidationTemplateId = request.ConsolidationTemplateId,
            AutoDispatch = request.AutoDispatch,
            RefactoringFocus = request.RefactoringFocus,

            // Issue title kept for GetPendingWorkItems display (not mutable config)
            IssueDetail = request.IssueDetail is not null
                ? new IssueDetail
                {
                    Identifier = request.IssueDetail.Identifier,
                    Title = request.IssueDetail.Title,
                    Description = string.Empty, // Strip body; re-fetched at assignment time
                    Labels = []
                }
                : null,

            // Dispatch-time profile identity — kept so AssignmentEnricher can restore the exact
            // profile at claim time without re-resolving from labels.
            ResolvedProfileId = request.ResolvedProfileId,

            // Schema version discriminator — marks this as new-schema (minimal identity payload).
            // GetAssignment uses PayloadSchemaVersion == 1 to detect new-schema rows and trigger
            // AssignmentEnricher. All mutable config intentionally omitted (null). Fields not listed
            // above (ProviderConfigs, PipelineConfiguration, QualityGateConfigs, ReviewerConfigs,
            // McpServers, RepoSteeringContent, ProjectSteeringContent, IssueComments,
            // ParsedIssue, ExistingAnalysis, AgentProviderConfigId)
            // are left at their default null values and re-fetched at assignment time by AssignmentEnricher.
            PayloadSchemaVersion = 1,
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
    ///   <item>404 Not Found — no WorkItem with this ID</item>
    ///   <item>503 Service Unavailable — no PVC available, advisory lock timeout, or K8s Job creation failed</item>
    /// </list>
    /// </summary>
    internal static async Task<IResult> DispatchPendingWorkItem(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        DispatchLifecycleService lifecycle,
        DispatchTemplateResolver templateResolver,
        IDistributedLockProvider lockProvider,
        DispatchWorkItemService dispatchService,
        CancellationToken ct = default)
    {
        // Delegate entirely to the service method which owns the full dispatch lifecycle
        // (fast-path check, advisory lock, concurrency gate, PVC gate, K8s Job creation,
        // result interpretation, and telemetry). The static endpoint exists as the ASP.NET
        // Core minimal-API handler that the DI container resolves parameters for.
        return await dispatchService.DispatchPendingWorkItemAsync(id, dbFactory, lifecycle, templateResolver, lockProvider, ct);
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

    // ── Private helpers ───────────────────────────────────────────────────

    internal static bool IsUniqueViolation(Exception ex)
        => PostgresErrorClassifier.IsUniqueViolation(ex);
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
