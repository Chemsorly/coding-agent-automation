using System.Diagnostics;
using System.Text.Json;
using CodingAgent.Api.Dispatch;
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
using Npgsql;
using Serilog;

namespace CodingAgent.Api;

/// <summary>
/// Minimal API endpoints for control-plane Work Item dispatch and lifecycle routes:
/// create, claim, dispatch (both paths), requeue, label-swap, last-progress, priority,
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

        // Synchronous dispatch endpoint — called by KubernetesWorkDistributor instead of POST /.
        // Atomically performs PVC selection, K8s Job creation, and Dispatched state write in a
        // single request. Returns 200+WorkItemId on success, 409 if at concurrency limit,
        // 503 if no PVC available or K8s failure.
        group.MapPost("/dispatch", DispatchWorkItem).RequireAuthorization(ApiAuthPolicies.Operator);

        group.MapGet("/{id:guid}/k8s-job-name", GetK8sJobName).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/is-distributed", GetIsDistributed).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/active-identifiers", GetActiveIdentifiers).RequireAuthorization(ApiAuthPolicies.Operator);
    }

    // ── POST / — create WorkItem ───────────────────────────────────────────

    /// <summary>
    /// POST /api/work-items
    /// Creates a new WorkItem with Status=Pending from a JobDistributionRequest.
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
        IDbContextFactory<PipelineDbContext> dbFactory,
        IOrchestratorRunService runService,
        CancellationToken ct = default)
    {
        // Use RunId from request if provided (ensures WorkItem.Id == PipelineRun.RunId for hub routing).
        // Fall back to a new GUID when no RunId is set (e.g., direct API calls without orchestration).
        var workItemId = !string.IsNullOrEmpty(request.RunId) && Guid.TryParse(request.RunId, out var parsedRunId)
            ? parsedRunId
            : Guid.NewGuid();

        // Serialize only identity fields to the payload (issue #2171).
        // Mutable config (ProviderConfigs, QGs, steering, MCP servers, issue context) is fetched
        // fresh at GetAssignment time. This prevents stale config being served to agents that
        // were queued for extended periods.
        var minimalPayload = BuildMinimalPayload(request);
        var payloadJson = JsonSerializer.Serialize(minimalPayload, PipelineJsonOptions.Default);

        var entity = new WorkItemEntity
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

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.WorkItems.Add(entity);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // Distinguish idempotent PK retry from business-rule unique index conflict.
            // Delegates to DispatchWorkItemService.HandleUniqueViolationAsync which opens
            // a fresh DbContext (this context is faulted after the exception) to re-query:
            // - If a row with workItemId already exists → 201 (idempotent PK retry).
            // - Otherwise → 409 via HandleUniqueViolationFallback (partial unique index on
            //   (IssueIdentifier, IssueProviderConfigId) — a different run is already live).
            return await DispatchWorkItemService.HandleUniqueViolationAsync(dbFactory, workItemId, isNewlyCreated: true, ct);
        }

        // Materialise in-memory PipelineRun in the API's IOrchestratorRunService so the UI
        // can subscribe to hub events and display the run immediately (Req 1a.1 Option A).
        // WorkItem.Id == PipelineRun.RunId for deterministic hub-group routing.
        // Consolidation WorkItems return null — they are tracked via ConsolidationRun, not PipelineRun.
        var run = PipelineRunFactory.CreateFromWorkItem(workItemId, request);
        if (run is not null)
            runService.AddRun(run);

        return TypedResults.Created($"/api/work-items/{workItemId}", workItemId);
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
            ConsolidationWorkspacePath = request.ConsolidationWorkspacePath,
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
    /// <para>
    /// Unlike <c>POST /api/work-items/dispatch</c> (<see cref="DispatchWorkItem"/>), this endpoint
    /// does NOT create a new WorkItem — it claims an already-Pending row.
    /// </para>
    ///
    /// Returns:
    /// <list type="bullet">
    ///   <item>200 — dispatch succeeded (K8s Job running, WorkItem=Dispatched)</item>
    ///   <item>404 Not Found — no WorkItem with this ID</item>
    ///   <item>409 Conflict — item not Pending, concurrency limit reached, or no template for selector</item>
    ///   <item>503 Service Unavailable — no PVC available, advisory lock timeout, or K8s Job creation failed</item>
    /// </list>
    /// </summary>
    internal static async Task<IResult> DispatchPendingWorkItem(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        DispatchLifecycleService lifecycle,
        JobTemplateStore templateStore,
        DispatchTemplateResolver templateResolver,
        IDistributedLockProvider lockProvider,
        DispatchWorkItemService dispatchService,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Fast path: check status before acquiring the lock.
        // Not an atomic guarantee — the CAS inside ExecuteDispatchLifecycleAsync is the
        // correctness guard. This check avoids unnecessary lock contention for items that
        // are already Dispatched or in a terminal state.
        var quickCheck = await db.WorkItems.AsNoTracking()
            .Select(w => new { w.Id, w.Status, w.AgentSelector, w.TimeoutSeconds, w.TaskType, w.ProjectId, w.IssueIdentifier, w.IssueProviderConfigId, w.PriorityWeight, w.CreatedAt })
            .FirstOrDefaultAsync(w => w.Id == id, ct);

        if (quickCheck is null)
            return TypedResults.NotFound();

        if (quickCheck.Status != WorkItemStatus.Pending)
        {
            Log.Information("DispatchPendingWorkItem: WorkItem {WorkItemId} is not Pending (status={Status}) — returning 409",
                id, quickCheck.Status);
            return TypedResults.Conflict($"Work item {id} is not in Pending state (current status: {quickCheck.Status}).");
        }

        var agentSelector = quickCheck.AgentSelector;
        var normalizedSelector = JobTemplateStore.NormalizeLabels(agentSelector);
        // Sanitize agentSelector before embedding it in any log message or HTTP response body.
        // agentSelector originates from a database column set by POST /api/work-items callers;
        // a crafted value containing \r\n can inject fake log lines (log forging). All log and
        // Conflict call sites below use sanitizedSelector / sanitized normalizedSelector instead
        // of the raw values. See also: LogSanitizer in CodingAgent.Pipeline.Services.
        var sanitizedSelector = CodingAgent.Pipeline.Services.LogSanitizer.SanitizeForLog(agentSelector);

        // Acquire advisory lock keyed on the normalized selector.
        // This serialises all concurrent calls to this endpoint for the same selector,
        // preventing the over-dispatch window where two callers both see capacity available
        // and each dispatch a different Pending item for the same selector.
        //
        // Lock scope: snapshot → gate → CAS (inside ExecuteDispatchLifecycleAsync).
        // The label swap to agent:in-progress is NOT in scope — it happens in AgentHub.RegisterAgent.
        //
        // Note: this lock only protects concurrent calls to THIS endpoint. DispatchWorkItem
        // (collection-level) and WorkItemDispatchPoller (Scheduler background poller) do NOT acquire
        // this lock. Cross-path correctness is provided by the CAS in ExecuteDispatchLifecycleAsync.
        IAsyncDisposable lockHandle;
        try
        {
            lockHandle = await lockProvider.AcquireAsync($"dispatch-selector:{normalizedSelector}", ct);
        }
        catch (TimeoutException)
        {
            // PostgresDistributedLockProvider retries with pg_try_advisory_lock for up to 60s.
            // A timeout means another call has held the lock for that entire window (e.g. a slow
            // K8s API response). Return 503 — transient, the Scheduler should retry next cycle.
            Log.Warning("DispatchPendingWorkItem: advisory lock acquisition timed out for selector {Selector} — returning 503", CodingAgent.Pipeline.Services.LogSanitizer.SanitizeForLog(normalizedSelector));
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await using var _ = lockHandle;

        // Post-lock status re-read: close the TOCTOU window.
        // Another dispatch path (WorkItemDispatchPoller, DispatchWorkItem, or a concurrent call
        // to this endpoint) may have transitioned the item from Pending → Dispatched between
        // the fast-path check above and lock acquisition. The advisory lock serialises concurrent
        // callers on this endpoint; without this re-read, the loser enters
        // ExecuteDispatchLifecycleAsync, finds the item no longer Pending, exits early
        // (dispatched = false), and the endpoint returns 503. The Scheduler interprets 503 as a
        // transient error and schedules an unnecessary full-cycle retry. 409 is the correct
        // signal: the item is already dispatched — no retry needed.
        //
        // Nullable projection: a missing row returns null (should not happen — WorkItems use
        // status transitions, not DELETE) and is treated as non-Pending to avoid dispatching a
        // ghost. Using a nullable projection avoids the default(WorkItemStatus) == Pending pitfall
        // that a plain .Select(w => w.Status).FirstOrDefaultAsync() would have if the row vanished.
        var postLockCheck = await db.WorkItems.AsNoTracking()
            .Select(w => new { w.Id, w.Status })
            .FirstOrDefaultAsync(w => w.Id == id, ct);

        if (postLockCheck is null || postLockCheck.Status != WorkItemStatus.Pending)
        {
            Log.Information(
                "DispatchPendingWorkItem: WorkItem {WorkItemId} is no longer Pending after lock acquisition (status={Status}) — returning 409",
                id, postLockCheck?.Status);
            return TypedResults.Conflict(
                $"Work item {id} is not in Pending state (current status: {postLockCheck?.Status}).");
        }

        // Build concurrency map inside the lock with normalised keys.
        // NOTE: do NOT use DispatchStateBuilder.BuildStateAsync here — it does NOT normalise
        // keys (uses raw x.Selector). Follow the DispatchWorkItem pattern (NormalizeLabels on
        // each key) so active items stored by any path are counted correctly.
        var concurrencyBySelector = await dispatchService.BuildConcurrencySnapshotAsync(db, ct);

        // Query PVC availability.
        var pvcPool = lifecycle.GetPvcPool();
        var pvcResult = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pvcPool, ct);
        // Emit credential-pool gauge BEFORE the PVC gate so the metric is always updated
        // whenever the PVC query runs (including on PVC-exhaustion 503).
        // Deliberate exception to the BuildStateAsync restriction: this endpoint is the
        // Scheduler-driven dispatch path and is the appropriate emitter for this metric.
        WorkDistributionTelemetry.UpdateCredentialPoolMetrics(pvcResult.AvailablePvcs.Count, pvcResult.ClaimedCount);

        // Template gate — try direct resolve first, then profile-based fallback.
        // When the profile fallback is used, capture the effective (canonical) selector so the
        // concurrency gate checks the correct key. Previously the gate used normalizedSelector
        // ("dotnet") while active items were stored under the canonical key ("dotnet,kiro"),
        // causing the gate to under-count and allow over-dispatch on the fallback path.
        var template = templateStore.Resolve(normalizedSelector);
        string? resolvedSelector = null;
        if (template is null)
        {
            var (fallbackTemplate, fallbackSelector) = await templateResolver.ResolveTemplateViaProfileAsync(
                agentSelector, "DispatchPendingWorkItem", ct);
            template = fallbackTemplate;
            resolvedSelector = fallbackSelector;
        }

        if (template is null)
        {
            Log.Warning("DispatchPendingWorkItem: no job template for selector {Selector} — returning 409", sanitizedSelector);
            return TypedResults.Conflict($"No job template for agent selector: {sanitizedSelector}");
        }

        // Use the canonical selector for the concurrency gate: if the profile fallback resolved the
        // template, the canonical key (e.g. "dotnet,kiro") is what's stored in the concurrency map
        // for items dispatched via the normal path. Using the partial normalizedSelector ("dotnet")
        // would miss those entries and silently allow over-dispatch.
        var effectiveSelector = resolvedSelector is not null
            ? JobTemplateStore.NormalizeLabels(resolvedSelector)
            : normalizedSelector;
        var sanitizedEffectiveSelector = CodingAgent.Pipeline.Services.LogSanitizer.SanitizeForLog(effectiveSelector);

        // Concurrency gate + PVC gate via shared helper.
        // UpdateCredentialPoolMetrics is emitted just above, before this call, as required —
        // it must stay outside ApplyGates (DispatchPendingWorkItem-only metric).
        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);
        var gateResult = dispatchService.ApplyGates(
            effectiveSelector, sanitizedEffectiveSelector, concurrencyBySelector,
            pvcResult, template, isKiroAgent, "DispatchPendingWorkItem");
        if (gateResult is not null)
        {
            // Emit the PVC exhaustion counter only when the gate result IS the 503/PVC-gate path.
            // This counter belongs exclusively to the DispatchPendingWorkItem path and must
            // NOT be emitted by ApplyGates itself, as DispatchWorkItem does not count exhaustions.
            // Checking the result type (StatusCodeHttpResult { StatusCode: 503 }) rather than
            // re-deriving pool state ensures the counter does not fire when the concurrency gate
            // (409 Conflict) short-circuits before the PVC gate is reached — even if both
            // conditions happen to hold simultaneously.
            if (gateResult is Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult { StatusCode: 503 })
                WorkDistributionTelemetry.PvcPoolExhaustions.Add(1);
            return gateResult;
        }

        // Build the projection for ExecuteDispatchLifecycleAsync.
        // TODO [WARNING]: When the profile fallback resolves the template, projection.AgentSelector is
        // set to normalizedSelector (e.g. "dotnet"), not to the template's canonical labels (e.g. "dotnet,kiro").
        // FinalizeDispatchAsync will increment concurrencyBySelector["dotnet"] rather than ["dotnet,kiro"].
        // Active items stored with selector "kiro,dotnet" are counted under a different normalized key
        // ("dotnet,kiro"), so IsAtConcurrencyLimit may under-count on the profile-fallback path and
        // allow over-dispatch when maxConcurrent is tight. The same gap exists in FinalizeDispatchAsync
        // (see its // TODO: Use effectiveSelector comment). Fix both together when the effectiveSelector
        // propagation is resolved.
        var projection = new PendingWorkItemProjection
        {
            Id = id,
            AgentSelector = normalizedSelector,
            CreatedAt = quickCheck.CreatedAt,
            TimeoutSeconds = quickCheck.TimeoutSeconds,
            TaskType = quickCheck.TaskType,
            ProjectId = quickCheck.ProjectId,
            IssueIdentifier = quickCheck.IssueIdentifier,
            IssueProviderConfigId = quickCheck.IssueProviderConfigId,
            PriorityWeight = quickCheck.PriorityWeight
        };

        // ExpectedInitialStatus is left at the default (Pending) — do NOT copy the
        // "ExpectedInitialStatus = WorkItemStatus.Dispatched" override from DispatchWorkItem.
        // That override applies because DispatchWorkItem creates items directly as Dispatched.
        // Here the item already exists as Pending; the lifecycle race-guard must see Pending
        // after K8s Job creation to proceed — setting it to Dispatched would cause the guard
        // to treat the Pending row as a race and delete the K8s Job silently.
        var ctx = new DispatchLifecycleContext(
            db,
            projection,
            template,
            isKiroAgent,
            pvcResult.AvailablePvcs,
            concurrencyBySelector,
            "pending-dispatch ");

        // Shared lifecycle execution: try/catch + !dispatched + 503 path are consolidated in
        // RunDispatchLifecycleAsync (issue #2859). onDispatchFailure is null here — the item
        // started as Pending, not Dispatched, so SafelyCancelOrphanedDispatchedWorkItemAsync
        // must NOT be called (no orphaned Dispatched row exists on this path).
        return await dispatchService.RunDispatchLifecycleAsync(
            ctx,
            lifecycle,
            onDispatchFailure: null,
            onSuccess: _ => TypedResults.Ok(id),
            id,
            "DispatchPendingWorkItem",
            ct);
    }

    // ── POST /dispatch — synchronous dispatch endpoint ────────────────────

    /// <summary>
    /// POST /api/work-items/dispatch
    /// Synchronous dispatch path: atomically performs PVC selection, K8s Job creation, and
    /// <c>Dispatched</c> state write in a single request. Called by <c>KubernetesWorkDistributor</c>
    /// instead of the two-step <c>POST /api/work-items</c> + DispatchLoop path.
    ///
    /// Returns:
    /// <list type="bullet">
    ///   <item>200 + WorkItemId — dispatch succeeded (K8s Job running, WorkItem=Dispatched)</item>
    ///   <item>409 Conflict — concurrency limit reached for this selector</item>
    ///   <item>503 Service Unavailable — no PVC available or K8s Job creation failed</item>
    /// </list>
    /// </summary>
    internal static async Task<IResult> DispatchWorkItem(
        [FromBody] JobDistributionRequest request,
        IDbContextFactory<PipelineDbContext> dbFactory,
        IOrchestratorRunService runService,
        DispatchLifecycleService lifecycle,
        JobTemplateStore templateStore,
        DispatchWorkItemService dispatchService,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Template resolution: selector → JobTemplate
        var template = templateStore.Resolve(request.AgentSelector ?? "");
        if (template is null)
        {
            Log.Warning("DispatchWorkItem: no job template for selector {Selector} — returning 422",
                CodingAgent.Pipeline.Services.LogSanitizer.SanitizeForLog(request.AgentSelector));
            // 422 Unprocessable Entity — permanent config error (no job template for this selector).
            // Distinct from 409 Conflict (transient capacity limit) so callers can differentiate
            // permanent failures (cascade run to Failed) from transient ones (leave Queued, retry later).
            return TypedResults.StatusCode(StatusCodes.Status422UnprocessableEntity);
        }

        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Build concurrency map (dispatched/running items by selector)
        var concurrencyBySelector = await dispatchService.BuildConcurrencySnapshotAsync(db, ct);

        // Query PVC availability.
        // TODO [WARNING]: This PVC availability snapshot is taken OUTSIDE _pvcSelectLock. Two concurrent
        // requests can both observe availablePvcs.Count > 0, pass the gate, create their Dispatched rows,
        // and both enter ExecuteDispatchLifecycleAsync. SelectPvcAsync (inside the lock) dequeues from
        // each caller's in-memory availablePvcs list — it does NOT re-query the database. In a
        // single-process deployment _pvcSelectLock ensures exactly one 200 and one 503, because all
        // callers share the same in-memory list. In a multi-replica deployment each pod holds its own
        // list; _pvcSelectLock cannot serialise across replicas. Both replicas can dequeue the same PVC
        // and create a K8s Job. The race is detected by HandleOrphanedJobIfRaceDetectedAsync (status
        // mismatch after K8s Job creation) or a DbUpdateConcurrencyException in FinalizeDispatchAsync.
        // SafelyCancelOrphanedDispatchedWorkItemAsync transitions the orphaned Dispatched row to Failed.
        // The 503 is self-healing: the issue label never advanced to agent:in-progress on this path,
        // so it stays agent:next and the Scheduler re-picks it on the next poll (default 60s).
        // See docs/architecture/concurrency-model.md — "PVC Dispatch Race in Multi-Replica Deployments".
        // A distributed lock (Postgres advisory lock) would be required to guarantee the one-200/one-503
        // invariant across replicas.
        var pvcPool = lifecycle.GetPvcPool();
        var pvcResult = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pvcPool, ct);
        var availablePvcs = pvcResult.AvailablePvcs;

        // Concurrency gate + PVC gate via shared helper.
        // NOTE: No PvcPoolExhaustions counter here — that metric belongs exclusively to DispatchPendingWorkItem.
        var normalizedReqSelector = JobTemplateStore.NormalizeLabels(request.AgentSelector ?? "");
        var sanitizedReqSelector = CodingAgent.Pipeline.Services.LogSanitizer.SanitizeForLog(request.AgentSelector);
        var gateResult = dispatchService.ApplyGates(
            normalizedReqSelector, sanitizedReqSelector, concurrencyBySelector,
            pvcResult, template, isKiroAgent, "DispatchWorkItem");
        if (gateResult is not null)
            return gateResult;

        // Capacity checks passed — create the WorkItem directly as Dispatched.
        // Issue #2322 requirement: no WorkItem is ever written as Pending on the live dispatch path.
        // The item is created as Dispatched atomically with the lifecycle call that creates the K8s Job.
        var workItemId = !string.IsNullOrEmpty(request.RunId) && Guid.TryParse(request.RunId, out var parsedRunId)
            ? parsedRunId
            : Guid.NewGuid();

        var minimalPayload = BuildMinimalPayload(request);
        var payloadJson = JsonSerializer.Serialize(minimalPayload, PipelineJsonOptions.Default);

        // Use the shared entity factory (parameterised by Status/DispatchedAt) to replace the
        // near-identical construction that previously appeared in CreateWorkItem and here.
        var entity = DispatchWorkItemService.CreateWorkItemEntity(
            workItemId,
            request,
            WorkItemStatus.Dispatched,
            DateTimeOffset.UtcNow,
            payloadJson);

        try
        {
            db.WorkItems.Add(entity);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // Idempotent retry: a work item with this ID already exists.
            // Delegates to DispatchWorkItemService.HandleUniqueViolationAsync which opens
            // a fresh DbContext (this context is faulted after the exception) to re-query
            // the existing item's status:
            // - Active (Dispatched/Running) → 200 OK.
            // - Non-active → 409 Conflict (Scheduler must re-queue).
            // - Not found → 409 via HandleUniqueViolationFallback (IssueIdentifier conflict).
            return await DispatchWorkItemService.HandleUniqueViolationAsync(dbFactory, workItemId, isNewlyCreated: false, ct);
        }

        // Register PipelineRun so the UI can subscribe to hub events immediately.
        var run = PipelineRunFactory.CreateFromWorkItem(workItemId, request);
        // TODO [WARNING]: If PipelineRunFactory.CreateFromWorkItem returns null, the WorkItem will be
        // dispatched (K8s Job running, WorkItem=Dispatched) but no PipelineRun is registered in
        // IOrchestratorRunService. The UI will not receive live run events for this WorkItem. The
        // requirement states registration is mandatory for SignalR hub routing. Add a log warning
        // and investigate why CreateFromWorkItem returns null (missing required fields in the request).
        if (run is not null)
            runService.AddRun(run);

        // Projection for ExecuteDispatchLifecycleAsync
        var projection = new PendingWorkItemProjection
        {
            Id = workItemId,
            AgentSelector = entity.AgentSelector,
            CreatedAt = entity.CreatedAt,
            TimeoutSeconds = entity.TimeoutSeconds,
            TaskType = entity.TaskType,
            ProjectId = entity.ProjectId,
            IssueIdentifier = entity.IssueIdentifier,
            IssueProviderConfigId = entity.IssueProviderConfigId,
            PriorityWeight = entity.PriorityWeight
        };

        var ctx = new DispatchLifecycleContext(
            db,
            projection,
            template,
            isKiroAgent,
            availablePvcs,
            concurrencyBySelector,
            "sync-dispatch ")
        {
            // The WorkItem was created directly as Dispatched (issue #2322: no Pending write on live path).
            // The lifecycle race-guard checks this status when reloading the item after K8s Job creation.
            ExpectedInitialStatus = WorkItemStatus.Dispatched
        };

        // Shared lifecycle execution: try/catch + !dispatched + 503 path are consolidated in
        // RunDispatchLifecycleAsync (issue #2859). SafelyCancelOrphanedDispatchedWorkItemAsync is
        // passed as onDispatchFailure — it must be called on the 503 path for DispatchWorkItem
        // (item was created as Dispatched; without cleanup it stays orphaned with no running Job).
        return await dispatchService.RunDispatchLifecycleAsync(
            ctx,
            lifecycle,
            onDispatchFailure: (id, reason) => SafelyCancelOrphanedDispatchedWorkItemAsync(lifecycle, id, reason),
            onSuccess: id => TypedResults.Ok(id),
            workItemId,
            "DispatchWorkItem",
            ct);
    }

    /// <summary>
    /// Loads project secrets for a work item if a project is configured.
    /// Shared by <see cref="DispatchPendingWorkItem"/> and <see cref="DispatchWorkItem"/> to
    /// eliminate the duplicated prepareVariant lambda body.
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

    /// <summary>
    /// Attempts to transition an orphaned Dispatched work item to Cancelled/Failed.
    /// Called when <c>DispatchWorkItem</c> returns 503 and the item may still be in <c>Dispatched</c>
    /// state with no running K8s Job (e.g. PVC race where the lifecycle returned early without
    /// cleaning up, or an unhandled exception during the dispatch lifecycle).
    /// If the lifecycle already moved the item to <c>Failed</c>, the transition is a no-op.
    /// Uses <see cref="CancellationToken.None"/> so the cleanup always completes regardless of
    /// the originating request's lifetime (the request token may already be cancelled if the
    /// client disconnected before the lifecycle completed).
    /// Swallows all exceptions — this is a best-effort cleanup that must not mask the 503.
    /// </summary>
    private static async Task SafelyCancelOrphanedDispatchedWorkItemAsync(
        DispatchLifecycleService lifecycle,
        Guid workItemId,
        string reason)
    {
        try
        {
            // FailWorkItemAsync uses WorkItemTransitionService.TransitionAsync which is a no-op
            // (returns false) when the item is not in Dispatched state, so this is safe to call
            // even if the lifecycle already transitioned the item to Failed.
            await lifecycle.FailWorkItemAsync(workItemId, reason, CancellationToken.None);
            Log.Information("DispatchWorkItem: cancelled orphaned Dispatched WorkItem {WorkItemId} after 503 response", workItemId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DispatchWorkItem: failed to cancel orphaned Dispatched WorkItem {WorkItemId} — item may linger in Dispatched state", workItemId);
        }
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
        var item = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.Status })
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return TypedResults.NotFound();

        // Item exists but is in wrong state (e.g. Pending/Running/Succeeded)
        return TypedResults.Conflict($"Cannot requeue work item in status '{item.Status}'.");
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
        var item = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.IssueProviderConfigId, w.IssueIdentifier, w.TaskType, w.Payload })
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return TypedResults.NotFound();

        if (labelSwapService is not null)
        {
            var isReview = item.TaskType == WorkItemTaskType.Review;
            var targetKind = isReview ? LabelTargetKind.PullRequest : LabelTargetKind.Issue;

            // A PR label swap must go through the *repository* provider — SwapPrLabelAsync resolves
            // the config as ProviderKind.Repository — but the work item only stores its issue
            // provider config as a column. For a review the repo provider lives in the serialized
            // JobDistributionRequest payload (same source the assignment endpoint reads), so pull
            // it from there. Passing the issue config id would make the repo lookup miss and the
            // swap silently no-op, which is why review PRs never got the in-progress marker.
            var providerConfigIdValue = item.IssueProviderConfigId;
            if (isReview && item.Payload is not null)
            {
                var payload = JsonSerializer.Deserialize<JobDistributionRequest>(
                    item.Payload, PipelineJsonOptions.Default);
                if (!string.IsNullOrEmpty(payload?.RepoProviderConfigId))
                    providerConfigIdValue = payload.RepoProviderConfigId;
            }

            await labelSwapService.SwapLabelWithRetryAsync(
                id,
                new ProviderConfigId(providerConfigIdValue),
                new IssueIdentifier(item.IssueIdentifier),
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
        var item = await db.WorkItems
            .Where(w => w.Id == id)
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return TypedResults.NotFound();

        item.LastProgressAt = request.Timestamp;
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
        var jobName = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => w.K8sJobName)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(jobName))
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
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var activeStatuses = PipelineConstants.ActiveWorkItemStatuses;
        var recentTerminalCutoff = DateTimeOffset.UtcNow - PipelineConstants.DefaultRestartDedupCooldown;

        // Single query evaluates both conditions at the same database snapshot, eliminating
        // the narrow TOCTOU window that existed when the active-status check and the
        // recently-terminal check were two sequential AnyAsync round-trips. A WorkItem that
        // transitions from an active status to a terminal status between two separate reads
        // could cause both reads to return false and the endpoint to incorrectly report
        // isDistributed = false.
        var isDistributed = await db.WorkItems
            .AsNoTracking()
            .AnyAsync(w =>
                w.IssueIdentifier == issueIdentifier &&
                w.IssueProviderConfigId == issueProviderConfigId &&
                (activeStatuses.Contains(w.Status) ||
                 (w.CompletedAt != null && w.CompletedAt >= recentTerminalCutoff)),
                ct);

        return TypedResults.Ok(new { isDistributed });
    }

    // ── GET /active-identifiers ───────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/active-identifiers
    /// Returns the set of (IssueIdentifier, IssueProviderConfigId) pairs that have active
    /// (non-terminal) WorkItems OR were recently terminated.
    /// Used by KubernetesWorkDistributor.GetActiveIssueIdentifiersAsync for dispatch deduplication.
    /// 200 with array of { issueIdentifier, issueProviderConfigId }.
    /// </summary>
    internal static async Task<IResult> GetActiveIdentifiers(
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var activeStatuses = PipelineConstants.ActiveWorkItemStatuses;
        var recentTerminalCutoff = DateTimeOffset.UtcNow - PipelineConstants.DefaultRestartDedupCooldown;

        // TODO: GetActiveIdentifiers issues two sequential ToListAsync round-trips with the same
        // TOCTOU window that was fixed in GetIsDistributed (see issue #2668). A WorkItem that
        // transitions between the two queries can produce a false negative for the active-identifier
        // check. Consider consolidating into a single query with an OR predicate (same pattern as
        // GetIsDistributed) to eliminate the window. The impact is lower here because
        // GetActiveIdentifiers is a bulk read used for polling, not a per-issue dedup guard.
        var activePairs = await db.WorkItems
            .AsNoTracking()
            .Where(w => activeStatuses.Contains(w.Status))
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
            .ToListAsync(ct);

        var recentTerminalPairs = await db.WorkItems
            .AsNoTracking()
            .Where(w => !activeStatuses.Contains(w.Status) &&
                        w.CompletedAt != null &&
                        w.CompletedAt >= recentTerminalCutoff)
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
            .ToListAsync(ct);

        var result = activePairs
            .Concat(recentTerminalPairs)
            .Select(p => new ActiveIdentifierDto(p.IssueIdentifier, p.IssueProviderConfigId))
            .Distinct()
            .ToList();

        return TypedResults.Ok((IReadOnlyList<ActiveIdentifierDto>)result);
    }

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
    {
        // Postgres path: DbUpdateException wrapping a PostgresException with SQLSTATE 23505
        if (ex is DbUpdateException { InnerException: PostgresException pg })
            return pg.SqlState == "23505";

        // Postgres fallback (non-Npgsql drivers) and EF InMemory path.
        // EF InMemory throws ArgumentException("An item with the same key has already been added")
        // directly — it is NOT wrapped in DbUpdateException — so we must check the top-level
        // message as well as the inner exception message.
        // Note: the EF InMemory phrase is an implementation detail and may change across EF Core versions.
        var message = ex.Message ?? "";
        var innerMessage = ex.InnerException?.Message ?? "";
        return message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || innerMessage.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || innerMessage.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            // EF InMemory exact phrase
            || message.Contains("An item with the same key has already been added", StringComparison.OrdinalIgnoreCase);
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
