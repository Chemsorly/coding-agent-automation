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
/// create, claim, dispatch a Pending item, requeue, label-swap, last-progress, priority,
/// k8s-job-name, is-distributed, and active-identifiers.
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
        // outright — otherwise a single compromised pod could claim, requeue or enumerate
        // every work item in the cluster.
        group.MapPost("/", CreateWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/claim", ClaimWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/dispatch", DispatchPendingWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/requeue", RequeueWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/label-swap", PostLabelSwap).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/last-progress", PostLastProgress).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapPost("/{id:guid}/priority", PostPriorityWeight).RequireAuthorization(ApiAuthPolicies.Operator);

        group.MapGet("/{id:guid}/k8s-job-name", GetK8sJobName).RequireAuthorization(ApiAuthPolicies.Operator);
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

            // Schema version discriminator — marks this as new-schema (minimal identity payload).
            // GetAssignment uses PayloadSchemaVersion == 1 to detect new-schema rows and trigger
            // AssignmentEnricher. All mutable config intentionally omitted (null). Fields not listed
            // above (ProviderConfigs, PipelineConfiguration, QualityGateConfigs, ReviewerConfigs,
            // McpServers, RepoSteeringContent, ProjectSteeringContent, IssueComments,
            // ParsedIssue, ExistingAnalysis, ResolvedProfileId, AgentProviderConfigId)
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


    // ── POST /{id}/claim ──────────────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/claim
    /// Atomic Pending → Dispatched compare-and-swap. Uses TransitionIfAsync (NOT TransitionAsync).
    /// 200 WorkItemClaimResponse, 409 Conflict (already claimed), 404 not found.
    /// <para>
    /// Also updates the in-memory <see cref="PipelineRun.StartedAtOffset"/> to
    /// <see cref="ClaimWorkItemRequest.DispatchedAt"/> so the UI ELAPSED column reflects actual
    /// run time rather than queue-wait + run time (issue #2106 / BUG-14 K8s path).
    /// Uses ResetStartedAt + ReplaceRun so the mutation is persisted back to Redis in
    /// distributed deployments — ResetStartedAt alone is insufficient because
    /// DistributedRunService.GetRun deserialises a fresh copy on every call.
    /// </para>
    /// </summary>
    internal static async Task<IResult> ClaimWorkItem(
        Guid id,
        [FromBody] ClaimWorkItemRequest request,
        WorkItemTransitionService transitionService,
        IDbContextFactory<PipelineDbContext> dbFactory,
        IOrchestratorRunService runService,
        IConfiguration configuration,
        CancellationToken ct)
    {
        string? payloadJson = null;

        var success = await transitionService.TransitionIfAsync(
            id,
            expectedCurrent: WorkItemStatus.Pending,
            target: WorkItemStatus.Dispatched,
            mutate: entity =>
            {
                entity.AssignedAgentId = request.AssignedAgentId;
                entity.DispatchedAt = request.DispatchedAt;
                if (request.K8sJobName is not null)
                    entity.K8sJobName = request.K8sJobName;
                // KiroPvcName is set by the Job Controller for kiro agent dispatches.
                // Written here so QueryAvailablePvcsAsync can compute accurate PVC availability
                // from the DB without querying live K8s Jobs (issue #2338).
                if (request.KiroPvcName is not null)
                    entity.ClaimedPvcName = request.KiroPvcName;
                payloadJson = entity.Payload;
            },
            ct: ct);

        if (!success)
        {
            // Distinguish 404 from 409
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var exists = await db.WorkItems.AnyAsync(w => w.Id == id, ct);
            if (!exists)
                return TypedResults.NotFound();

            return TypedResults.Conflict("Work item is not in Pending state or was already claimed.");
        }

        // Update the in-memory PipelineRun's StartedAtOffset to the actual dispatch time.
        // This fixes the ELAPSED column in the UI, which was showing queue-wait + run time
        // instead of just run time (issue #2106 / BUG-14 recurrence in the K8s API path).
        //
        // ReplaceRun is required (not just ResetStartedAt) because DistributedRunService.GetRun
        // returns a freshly deserialised copy — without ReplaceRun the mutation is discarded and
        // the Redis hash retains the original enqueue-time StartedAtOffset.
        //
        // Null-safe: run is null when the API pod restarted between CreateWorkItem and ClaimWorkItem
        // (no in-memory run exists). The DB-backed fallback (PostgresActiveRunQueryService) already
        // uses DispatchedAt ?? CreatedAt for elapsed calculation, so that path is unaffected.
        //
        // Default guard: DispatchedAt is 'required DateTimeOffset' (non-nullable); a zero-epoch
        // default value would produce a nonsensical StartedAtOffset, so skip the update in that case.
        var run = runService.GetRun(new RunId(id.ToString()));
        if (run is not null && request.DispatchedAt != default)
        {
            run.ResetStartedAt(request.DispatchedAt);
            runService.ReplaceRun(run);
        }

        // Derive RunId from workItemId if not set in payload
        // (deterministic: reuses WorkItemId bytes to form a stable GUID)
        var runId = DeriveRunId(id);

        // Read OrchestratorUrl from configuration
        var orchestratorUrl =
            configuration.GetValue<string>("WorkDistribution:OrchestratorUrl")
            ?? configuration.GetValue<string>("OrchestratorUrl")
            ?? "";

        var response = new WorkItemClaimResponse
        {
            WorkItemId = id,
            RunId = runId,
            PayloadJson = payloadJson ?? "",
            OrchestratorUrl = orchestratorUrl
        };

        return TypedResults.Ok(response);
    }

    // ── POST /{id}/requeue ─────────────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/requeue
    /// Transitions Failed/Cancelled/Dispatched → Pending, incrementing RetryCount.
    /// Dispatched→Pending covers the case where Job creation fails after a successful claim
    /// (ProcessItemAsync calls SafeRequeueAsync while the item is still in Dispatched state).
    /// 200, 409 Conflict (wrong state), 404.
    /// </summary>
    internal static async Task<IResult> RequeueWorkItem(
        Guid id,
        WorkItemTransitionService transitionService,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        // Collapse the three sequential TransitionIfAsync blocks into a loop (issue #2859).
        // Failed→Pending and Cancelled→Pending share the same mutate lambda.
        // Dispatched→Pending additionally clears K8sJobName (the item had a K8s Job assigned).
        var sourceStates = new[] { WorkItemStatus.Failed, WorkItemStatus.Cancelled, WorkItemStatus.Dispatched };
        foreach (var sourceState in sourceStates)
        {
            Action<WorkItemEntity> mutate = sourceState == WorkItemStatus.Dispatched
                ? (e => { e.RetryCount++; e.DispatchedAt = null; e.AssignedAgentId = null; e.K8sJobName = null; })
                : (e => { e.RetryCount++; e.DispatchedAt = null; e.AssignedAgentId = null; });
            var succeeded = await transitionService.TransitionIfAsync(id, sourceState, WorkItemStatus.Pending, mutate, ct);
            if (succeeded)
                return TypedResults.Ok();
        }

        // Check existence to differentiate 404 from 409
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (item, notFound) = await LoadProjectionOrNotFoundAsync(
            db, id, w => new { w.Status }, ct);

        if (notFound is not null)
            return notFound;

        // Item exists but is in wrong state (e.g. Pending/Running/Succeeded)
        return TypedResults.Conflict($"Cannot requeue work item in status '{item!.Status}'.");
    }

    // ── POST /{id}/label-swap ─────────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/label-swap
    /// Body: { "label": string } — the label field is kept for wire compatibility but is NOT
    /// used to drive behavior. The handler always swaps to agent:in-progress.
    /// Returns 200, 404 if WorkItem not found.
    ///
    /// <para>
    /// The target follows the work item's task type. A review's identifier is a pull request
    /// number, not an issue number, so labelling it as an issue puts the in-progress marker on
    /// whatever issue happens to share that number. This handler used to pass
    /// <c>LabelTargetKind.Issue</c> unconditionally — "work items are always issue-origin at
    /// dispatch time", which reviews are not — and it disagreed with the completion path, which
    /// labels the pull request. GitHub hides the disagreement, since its issues API accepts a PR
    /// number; a provider that keeps the two apart does not.
    /// </para>
    /// </summary>
    internal static async Task<IResult> PostLabelSwap(
        Guid id,
        [FromBody] LabelSwapRequest request,
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILabelSwapService? labelSwapService,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (item, notFound) = await LoadProjectionOrNotFoundAsync(
            db, id, w => new { w.IssueProviderConfigId, w.IssueIdentifier, w.TaskType, w.Payload }, ct);

        if (notFound is not null)
            return notFound;

        // item is guaranteed non-null: LoadProjectionOrNotFoundAsync returns notFound=null iff Value != null.
        var workItem = item!;

        if (labelSwapService is not null)
        {
            var isReview = workItem.TaskType == WorkItemTaskType.Review;
            var targetKind = isReview ? LabelTargetKind.PullRequest : LabelTargetKind.Issue;

            // A PR label swap must go through the *repository* provider — SwapPrLabelAsync resolves
            // the config as ProviderKind.Repository — but the work item only stores its issue
            // provider config as a column. For a review the repo provider lives in the serialized
            // JobDistributionRequest payload (same source the assignment endpoint reads), so pull
            // it from there. Passing the issue config id would make the repo lookup miss and the
            // swap silently no-op, which is why review PRs never got the in-progress marker.
            var providerConfigIdValue = workItem.IssueProviderConfigId;
            if (isReview && workItem.Payload is not null
                && WorkItemPayload.TryDeserialize(workItem.Payload, out var payloadReq)
                && !string.IsNullOrEmpty(payloadReq?.RepoProviderConfigId))
            {
                providerConfigIdValue = payloadReq.RepoProviderConfigId;
            }

            await labelSwapService.SwapLabelWithRetryAsync(
                id,
                new ProviderConfigId(providerConfigIdValue),
                new IssueIdentifier(workItem.IssueIdentifier),
                targetKind,
                ct);
        }

        return TypedResults.Ok();
    }

    // ── POST /{id}/last-progress ──────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items/{id}/last-progress
    /// Body: { "timestamp": DateTimeOffset }
    /// Updates the LastProgressAt field. Returns 200 or 404.
    /// NOTE: LastProgressAt column already exists via migration AddLastProgressAtToWorkItems.
    /// No EF migration is needed.
    /// </summary>
    internal static async Task<IResult> PostLastProgress(
        Guid id,
        [FromBody] LastProgressRequest request,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (item, notFound) = await LoadProjectionOrNotFoundAsync(
            db, id, w => w, ct, noTracking: false);

        if (notFound is not null)
            return notFound;

        item!.LastProgressAt = request.Timestamp;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok();
    }

    // ── GET /{id}/k8s-job-name ────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/{id}/k8s-job-name
    /// Returns the K8s Job name associated with a WorkItem.
    /// Used by KubernetesJobCleanup to cancel the K8s Job when an issue is cancelled.
    /// 200 with { jobName: string } or 404 if not found / no job name set.
    /// </summary>
    internal static async Task<IResult> GetK8sJobName(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (jobName, notFound) = await LoadProjectionOrNotFoundAsync(
            db, id, w => w.K8sJobName, ct);

        // notFound covers two cases: (a) no row exists, (b) row exists but K8sJobName is null.
        // string.IsNullOrEmpty additionally catches an empty-string K8sJobName on an existing row.
        if (notFound is not null || string.IsNullOrEmpty(jobName))
            return TypedResults.NotFound();

        return TypedResults.Ok(new { jobName });
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

    /// <summary>
    /// Projects a single <see cref="WorkItemEntity"/> row by its <paramref name="id"/> using the
    /// supplied <paramref name="selector"/>, returning <c>(Value, null)</c> when the row exists or
    /// <c>(null, TypedResults.NotFound())</c> when it does not.
    /// </summary>
    /// <typeparam name="T">The projected type. Must be a reference type (class, record, or string) so
    /// that <c>null</c> reliably signals absence. Anonymous types and named record types satisfy
    /// this constraint. Nullable variants (e.g. <c>string?</c>) are also accepted.</typeparam>
    /// <param name="db">An open <see cref="PipelineDbContext"/> scoped to the current request.</param>
    /// <param name="id">The work item GUID to look up.</param>
    /// <param name="selector">EF Core-translatable projection expression.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="noTracking">
    /// When <c>true</c> (default), applies <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{T}(IQueryable{T})"/>.
    /// Pass <c>false</c> when the caller needs a tracked entity (e.g. for subsequent mutation).
    /// </param>
    /// <returns>
    /// A tuple where either <c>Value</c> is non-null (row found) or <c>NotFound</c> is non-null
    /// (row absent). Exactly one of the two fields is non-null.
    /// </returns>
    private static async Task<(T? Value, IResult? NotFound)> LoadProjectionOrNotFoundAsync<T>(
        PipelineDbContext db,
        Guid id,
        Expression<Func<WorkItemEntity, T>> selector,
        CancellationToken ct,
        bool noTracking = true)
        where T : class?
    {
        // TODO: The null sentinel used to detect row-absence is unreliable when T is a nullable
        // reference type (e.g. string?) and the projected column value is null on an existing row.
        // In that case FirstOrDefaultAsync returns null even though the row exists, causing the
        // helper to return (null, NotFound()) — both tuple fields are null — which violates the
        // documented contract ("Exactly one of the two fields is non-null"). Callers that rely on
        // only the notFound guard (rather than also checking Value) will incorrectly treat the
        // populated-but-null row as absent. The current GetK8sJobName call site handles this with
        // a secondary string.IsNullOrEmpty check, but future callers of this helper are at risk.
        // Consider restricting the constraint to `where T : class` (non-nullable) or adding an
        // explicit caveat to the XML doc and the return value.
        var query = noTracking
            ? db.WorkItems.AsNoTracking().Where(w => w.Id == id)
            : db.WorkItems.Where(w => w.Id == id);

        var value = await query
            .Select(selector)
            .FirstOrDefaultAsync(ct);

        return value is null
            ? (default, TypedResults.NotFound())
            : (value, null);
    }

    /// <summary>
    /// Derives a deterministic RunId string from the work item GUID.
    /// Checks the payload for a pre-assigned RunId first, then falls back to workItemId-based derivation.
    /// </summary>
    private static string DeriveRunId(Guid workItemId)
    {
        // Use workItemId as the run ID (bytes rearranged to produce a v4-like GUID)
        // This is deterministic and stable across retries.
        return workItemId.ToString();
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

/// <summary>
/// Request body for POST /api/work-items/{id}/label-swap.
/// The <see cref="Label"/> field is kept for wire compatibility but is NOT used to drive behavior.
/// The handler always calls <see cref="ILabelSwapService.SwapLabelWithRetryAsync"/> with
/// <see cref="LabelTargetKind.Issue"/> — the string value is ignored.
/// </summary>
public sealed class LabelSwapRequest
{
    public string Label { get; init; } = "";
}

/// <summary>
/// Request body for POST /api/work-items/{id}/last-progress.
/// </summary>
public sealed class LastProgressRequest
{
    public required DateTimeOffset Timestamp { get; init; }
}
