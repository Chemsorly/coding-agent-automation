using System.Diagnostics;
using CodingAgent.Api;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// The dispatch of a Pending work item:
/// <see cref="CodingAgent.Api.WorkItemDispatchEndpoints.DispatchPendingWorkItem"/>, which the
/// Scheduler's dispatch loop calls. Spec 051 2.3c deleted the synchronous
/// <c>POST /api/work-items/dispatch</c>, which created items directly as Dispatched; every dispatch
/// now starts from the Pending queue.
///
/// <para>
/// Extracted from <c>WorkItemDispatchEndpoints.cs</c> (issue #2743).
/// <see cref="BuildProjectionFromQuickCheck"/> builds the
/// <see cref="CodingAgent.Orchestration.Dispatch.PendingWorkItemProjection"/>.
/// </para>
///
/// <para>
/// Spec 057: every start takes the <see cref="IAgentStartGate"/> (one lock for work items and chats), which counts
/// the running agent Jobs and answers the four optional concurrency limits; the gate's Job list also gives the PVCs
/// running agents hold.
/// </para>
///
/// <para>
/// Registered in DI as <c>AddSingleton</c>. The service is stateless and depends only on
/// <see cref="JobTemplateStore"/> and <see cref="IAgentStartGate"/> (both singletons).
/// </para>
/// </summary>
internal sealed class DispatchWorkItemService
{
    private const string DeferredResult = "deferred";
    private const string TransientResult = "transient";
    private const string NotPendingReason = "not_pending";

    private readonly JobTemplateStore _templateStore;
    private readonly IAgentStartGate _gate;

    public DispatchWorkItemService(JobTemplateStore templateStore, IAgentStartGate gate)
    {
        ArgumentNullException.ThrowIfNull(templateStore);
        ArgumentNullException.ThrowIfNull(gate);
        _templateStore = templateStore;
        _gate = gate;
    }

    /// <summary>
    /// Resolves the <see cref="JobTemplate"/> an item stored with <paramref name="agentSelector"/> is dispatched under:
    /// a direct lookup of the normalized selector, the job template key the API pinned when it routed the item
    /// (spec 053 req 4.9). The limit check and the Job label key on that selector.
    /// </summary>
    /// <returns>The template, or <c>null</c> when the Helm values have none; and the normalized selector.</returns>
    internal (JobTemplate? Template, string EffectiveSelector) ResolveTemplate(string agentSelector)
    {
        var normalizedSelector = JobTemplateStore.NormalizeLabels(agentSelector);
        return (_templateStore.Resolve(normalizedSelector), normalizedSelector);
    }

    // ── Projection factories ─────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="PendingWorkItemProjection"/> from the <see cref="DispatchQuickCheck"/> row read by
    /// <c>DispatchPendingWorkItem</c>'s fast-path query (issue #2988).
    /// </summary>
    /// <param name="quickCheck">The fast-path row.</param>
    /// <param name="effectiveSelector">
    /// The selector of the template the item is dispatched under (e.g. <c>"dotnet,kiro"</c> for an item
    /// stored as <c>"dotnet"</c> that the profile fallback resolved), not the stored selector. It becomes
    /// the Job's <c>caa/agent-selector</c> label and the dispatch metrics' selector (issue #2777).
    /// </param>
    internal static PendingWorkItemProjection BuildProjectionFromQuickCheck(
        DispatchQuickCheck quickCheck,
        string effectiveSelector) =>
        new PendingWorkItemProjection
        {
            Id = quickCheck.Id,
            AgentSelector = effectiveSelector,
            CreatedAt = quickCheck.CreatedAt,
            TimeoutSeconds = quickCheck.TimeoutSeconds,
            TaskType = quickCheck.TaskType,
            ProjectId = quickCheck.ProjectId,
            IssueIdentifier = quickCheck.IssueIdentifier,
            IssueProviderConfigId = quickCheck.IssueProviderConfigId,
            PriorityWeight = quickCheck.PriorityWeight
        };

    // ── Gate block ───────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the PVC gate (the concurrency limits are answered by the agent start gate before, spec 057).
    ///
    /// <para>
    /// Returns a non-null <see cref="IResult"/> (503) when the gate fires; returns <c>null</c> when it passes.
    /// </para>
    ///
    /// <para>
    /// <strong>Telemetry note:</strong> this method does NOT emit
    /// <c>WorkDistributionTelemetry.PvcPoolExhaustions</c>.
    /// That call is path-specific (<c>DispatchPendingWorkItem</c> only) and must
    /// remain in the respective handler, outside this method. The credential-pool gauges are
    /// not emitted from the API at all — the Scheduler leader owns them.
    /// </para>
    /// </summary>
    /// <param name="sanitizedSelector">
    /// Log-safe (CRLF-stripped) form of the selector, for log messages.
    /// </param>
    /// <param name="pvcResult">PVC availability snapshot.</param>
    /// <param name="isKiroAgent"><c>true</c> when the template targets a kiro provider.</param>
    /// <param name="callerName">Short name used in log messages (e.g. <c>"DispatchPendingWorkItem"</c>).</param>
    /// <returns>
    /// A non-null <see cref="IResult"/> if the gate fires; <c>null</c> when it passes.
    /// </returns>
    internal IResult? ApplyGates( // NOSONAR S2325 — instance API: callers and tests use an instance
        string sanitizedSelector,
        PvcAvailabilityResult pvcResult,
        bool isKiroAgent,
        string callerName)
    {
        // TODO [WARNING]: `callerName` is embedded into a Serilog structured-log message via a
        // positional hole ({CallerName}). All current call sites pass hardcoded string literals so
        // there is no immediate injection risk. However, the parameter is typed as plain `string`
        // with no validation — a future call site that passes user-controlled input would embed it
        // verbatim in log output. Serilog's structured API prevents format-string injection, but
        // CRLF characters could smuggle extra log lines into text sinks. Fix: restrict to an enum
        // or validate/truncate to alphanumeric-only at the entry point.

        // PVC gate — only fires for kiro agents.
        // NOTE: PvcPoolExhaustions.Add(1) is NOT called here — it belongs exclusively to the
        // DispatchPendingWorkItem path and is emitted by the handler after detecting a 503 result.
        if (isKiroAgent && pvcResult.AvailablePvcs.Count == 0)
        {
            Log.Information(
                "{CallerName}: no PVC available for kiro agent selector {Selector} — returning 503",
                callerName, sanitizedSelector);
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return null;
    }

    // ── Unique-violation fallback ────────────────────────────────────────────

    /// <summary>
    /// Returns the shared <c>"A live work item already exists for this issue."</c>
    /// <see cref="IResult"/>: <c>POST /api/work-items</c> answers it when the issue already has live work.
    /// </summary>
    internal static IResult HandleUniqueViolationFallback()
        => TypedResults.Conflict("A live work item already exists for this issue.");

    // ── Dispatch lifecycle executor ──────────────────────────────────────────────

    /// <summary>
    /// Runs <see cref="DispatchLifecycleService.ExecuteDispatchLifecycleAsync"/> and returns:
    /// <list type="bullet">
    ///   <item>The result from <paramref name="onSuccess"/> when dispatch completes.</item>
    ///   <item>503 Service Unavailable when the lifecycle returns without dispatching (e.g. PVC
    ///     race, race-condition early exit) or when it throws a non-cancellation exception. The item
    ///     started as Pending, so there is no orphaned Dispatched row to clean up.</item>
    /// </list>
    ///
    /// <para>
    /// <c>WorkDistributionTelemetry.PvcPoolExhaustions</c> is emitted by the caller
    /// (<c>DispatchPendingWorkItem</c>), not inside this method.
    /// </para>
    /// </summary>
    /// <param name="ctx">Dispatch lifecycle context, built by the caller.</param>
    /// <param name="lifecycle">
    /// The shared singleton <see cref="DispatchLifecycleService"/>. Passed in (not resolved
    /// from DI) to preserve the <c>_pvcSelectLock</c> singleton semantics.
    /// </param>
    /// <param name="onSuccess">
    /// Factory called with the dispatched work-item ID when dispatch succeeds.
    /// Returns the <see cref="IResult"/> the endpoint returns to the client (e.g. 200 OK).
    /// </param>
    /// <param name="workItemId">Work-item GUID for log messages.</param>
    /// <param name="callerName">Short caller name for log messages (e.g. <c>"DispatchPendingWorkItem"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The success <see cref="IResult"/> on success; 503 on failure.</returns>
    internal async Task<IResult> RunDispatchLifecycleAsync( // NOSONAR S2325 — instance API: callers and tests use an instance
        DispatchLifecycleContext ctx,
        DispatchLifecycleService lifecycle,
        Func<Guid, IResult> onSuccess,
        Guid workItemId,
        string callerName,
        CancellationToken ct)
    {
        bool dispatched = false;
        try
        {
            await lifecycle.ExecuteDispatchLifecycleAsync(
                ctx,
                prepareVariant: workItem => WorkItemDispatchEndpoints.PrepareDispatchVariantAsync(ctx.Db, workItem, ct),
                onDispatchSuccess: _ =>
                {
                    dispatched = true;
                    return Task.CompletedTask;
                },
                ct,
                onFailure: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "{CallerName}: unhandled exception during dispatch lifecycle for {WorkItemId}",
                callerName, workItemId);
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!dispatched)
        {
            Log.Warning("{CallerName}: lifecycle did not dispatch WorkItem {WorkItemId} (PVC race or K8s failure) — returning 503",
                callerName, workItemId);
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return onSuccess(workItemId);
    }

    // ── Gate + context + lifecycle entry point ───────────────────────────────

    /// <summary>
    /// Runs the <c>isKiroAgent → ApplyGates → DispatchLifecycleContext → RunDispatchLifecycleAsync</c>
    /// sequence for a resolved Pending item (issue #2890).
    ///
    /// <para>
    /// <strong>Caller responsibilities (not absorbed here):</strong>
    /// <list type="bullet">
    ///   <item>
    ///     Hold the agent start lease (spec 057) and pass the PVC availability computed from its held PVCs as
    ///     <see cref="ResolvedDispatchRequest.PvcResult"/> — the lock ordering cannot be replicated inside this method.
    ///   </item>
    ///   <item>
    ///     After this method returns, <c>DispatchPendingWorkItem</c> checks whether the result
    ///     is a 503 and emits <c>WorkDistributionTelemetry.PvcPoolExhaustions</c> — that counter
    ///     belongs to that path and must not be moved inside this method.
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="request">The per-call inputs (see <see cref="ResolvedDispatchRequest"/>).</param>
    /// <param name="lifecycle">
    /// Shared singleton <see cref="DispatchLifecycleService"/>. Passed in to preserve
    /// <c>_pvcSelectLock</c> singleton semantics.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A non-null <see cref="IResult"/> from <see cref="ApplyGates"/> if the PVC gate fires;
    /// the result from <see cref="ResolvedDispatchRequest.OnSuccess"/> on dispatch success;
    /// 503 Service Unavailable on lifecycle failure.
    /// </returns>
    internal async Task<IResult> DispatchResolvedWorkItemAsync(
        ResolvedDispatchRequest request,
        DispatchLifecycleService lifecycle,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = request.Template;
        var pvcResult = request.PvcResult;
        var callerName = request.CallerName;

        // Compute isKiroAgent exactly once via JobTemplateProviderType.IsKiro — the "kiro" literal lives there only (AC3).
        var isKiroAgent = JobTemplateProviderType.IsKiro(template.ProviderType);

        // PVC gate.
        // NOTE: PvcPoolExhaustions is NOT emitted here —
        // that telemetry call belongs exclusively to the DispatchPendingWorkItem handler.
        var gateResult = ApplyGates(request.SanitizedSelector, pvcResult, isKiroAgent, callerName);
        if (gateResult is not null)
            return gateResult;

        var ctx = new DispatchLifecycleContext(
            request.Db,
            request.Projection,
            template,
            isKiroAgent,
            pvcResult.AvailablePvcs,
            request.LogPrefix);

        return await RunDispatchLifecycleAsync(
            ctx,
            lifecycle,
            request.OnSuccess,
            request.WorkItemId,
            callerName,
            ct);
    }

    // ── Pending-dispatch orchestration ───────────────────────────────────────

    /// <summary>
    /// Encapsulates the full orchestration body of
    /// <c>POST /api/work-items/{id}/dispatch</c> (the claim-by-CAS Scheduler path).
    /// Extracted from <see cref="CodingAgent.Api.WorkItemDispatchEndpoints.DispatchPendingWorkItem"/>
    /// by issue #3286 to satisfy Clean Architecture (presentation layer must not own orchestration).
    ///
    /// <para>
    /// The endpoint handler becomes a one-line delegator after this extraction; all selector
    /// normalization, the agent start gate, template resolution, limit and PVC checks, and result
    /// interpretation live here.
    /// </para>
    ///
    /// <para>
    /// <strong>Telemetry note:</strong>
    /// <c>WorkDistributionTelemetry.PvcPoolExhaustions.Add(1)</c> belongs exclusively to this path
    /// and is emitted here (not inside <see cref="InterpretDispatchResult"/> or
    /// <see cref="DispatchResolvedWorkItemAsync"/>).
    /// </para>
    /// </summary>
    /// <param name="id">The GUID of the Pending WorkItem to dispatch.</param>
    /// <param name="dbFactory">Factory for opening the caller-owned <see cref="PipelineDbContext"/>.</param>
    /// <param name="lifecycle">The shared singleton <see cref="DispatchLifecycleService"/>.</param>
    /// <param name="rollout">Routes an item a previous-release API stored without a selector (spec 053 DD10).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <list type="bullet">
    ///   <item>200 <c>DispatchPendingResponse(true,"none")</c> — dispatch succeeded.</item>
    ///   <item>200 <c>DispatchPendingResponse(false,"not_pending"|"no_template"|"concurrency_limit"|"global_concurrency_limit")</c>
    ///     — expected backpressure outcome.</item>
    ///   <item>200 <c>DispatchPendingResponse(false,"no_route")</c> — the item had no route and none resolves; it was
    ///     failed with the reason.</item>
    ///   <item>404 Not Found — no WorkItem with this ID.</item>
    ///   <item>503 Service Unavailable — no PVC available, lock timeout, running agents not countable, K8s failure,
    ///     or a routing error.</item>
    /// </list>
    /// </returns>
    internal async Task<IResult> DispatchPendingWorkItemAsync(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        DispatchLifecycleService lifecycle,
        RolloutRouting rollout,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Fast path: check status before acquiring the lock.
        // Not an atomic guarantee — the CAS inside ExecuteDispatchLifecycleAsync is the
        // correctness guard. This check avoids unnecessary lock contention for items that
        // are already Dispatched or in a terminal state.
        var quickCheck = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new DispatchQuickCheck
            {
                Id = w.Id,
                Status = w.Status,
                AgentSelector = w.AgentSelector,
                TimeoutSeconds = w.TimeoutSeconds,
                TaskType = w.TaskType,
                ProjectId = w.ProjectId,
                IssueIdentifier = w.IssueIdentifier,
                IssueProviderConfigId = w.IssueProviderConfigId,
                PriorityWeight = w.PriorityWeight,
                CreatedAt = w.CreatedAt
            })
            .FirstOrDefaultAsync(ct);

        if (quickCheck is null)
            return TypedResults.NotFound();

        if (quickCheck.Status != WorkItemStatus.Pending)
        {
            Log.Information("DispatchPendingWorkItem: WorkItem {WorkItemId} is not Pending (status={Status}) — returning 200/deferred",
                id, quickCheck.Status);
            WorkDistributionTelemetry.RecordDispatchAttempt(DeferredResult, NotPendingReason);
            return TypedResults.Ok(new DispatchPendingResponse(false, NotPendingReason));
        }

        var agentSelector = quickCheck.AgentSelector;
        if (string.IsNullOrEmpty(agentSelector))
        {
            // Spec 053 DD10: a previous-release API replica stored the request of a current-release caller without
            // a selector or route. Route it now, before the start gate; a refusal fails it.
            var (routedSelector, early) = await rollout.RouteAsync(id, ct);
            if (early is not null)
                return early;
            agentSelector = routedSelector!;
        }

        // Sanitize agentSelector before embedding it in any log message or HTTP response body.
        // agentSelector originates from a database column set by POST /api/work-items callers;
        // a crafted value containing \r\n can inject fake log lines (log forging). All log
        // call sites below use sanitizedSelector / sanitizedEffectiveSelector instead
        // of the raw values. See also: LogSanitizer in CodingAgent.Infrastructure.Common.
        var sanitizedSelector = LogSanitizer.SanitizeForLog(agentSelector);

        // Template: the job template key the API pinned when it routed the item (spec 053 req 4.9). The limit check
        // and the Job label key on it. A missing template is reported after the gate and the post-lock re-check.
        var (template, effectiveSelector) = ResolveTemplate(agentSelector);
        var sanitizedEffectiveSelector = LogSanitizer.SanitizeForLog(effectiveSelector);

        // Enter the agent start gate (spec 057) and perform the post-lock TOCTOU re-read. Both are encapsulated in
        // TryEnterDispatchAsync (issue #3235). Lock scope: count → limit checks → PVC gate → CAS → Job creation. The
        // same lock serializes chat launches, so a chat and a dispatch cannot both pass a limit only one may pass.
        var (enteredLease, lockEarlyReturn) = await TryEnterDispatchAsync(_gate, db, id, ct);
        if (lockEarlyReturn is not null)
            return lockEarlyReturn;
        await using var lease = enteredLease!;

        if (template is null)
        {
            Log.Warning("DispatchPendingWorkItem: no job template for selector {Selector} — returning 200/deferred", sanitizedSelector);
            WorkDistributionTelemetry.RecordDispatchAttempt(DeferredResult, "no_template");
            return TypedResults.Ok(new DispatchPendingResponse(false, "no_template"));
        }

        // Spec 057 req 3.1: the limit for all agents first, then the template's limit (lowest of Helm and UI each).
        var decision = lease.CheckWorkItem(effectiveSelector);
        if (!decision.Allowed)
            return HoldForLimit(decision, sanitizedEffectiveSelector);

        // Spec 057 DD1: the PVCs running agent Jobs (chats included) hold come from the gate's one Job list.
        var pvcResult = await lifecycle.QueryDispatchablePvcsAsync(db, lease.Jobs.HeldPvcs, ct);

        // Build the projection for the shared dispatch helper (issue #2988). Its AgentSelector is the
        // canonical effectiveSelector, which the Job label and the dispatch metrics carry (#2777).
        var projection = BuildProjectionFromQuickCheck(quickCheck, effectiveSelector);

        // Gate + context construction + lifecycle execution via shared helper (issue #2890).
        // The PvcPoolExhaustions counter is emitted after the call (below) rather than inside the helper —
        // it belongs exclusively to this path and must fire only on the 503/PVC-gate result.
        var dispatchResult = await DispatchResolvedWorkItemAsync(
            new ResolvedDispatchRequest
            {
                Db = db,
                Template = template,
                Projection = projection,
                SanitizedSelector = sanitizedEffectiveSelector,
                PvcResult = pvcResult,
                LogPrefix = "pending-dispatch ",
                OnSuccess = _ => TypedResults.Ok(id),
                WorkItemId = id,
                CallerName = "DispatchPendingWorkItem"
            },
            lifecycle,
            ct);

        // ── Result interception: delegate to shared helper (issue #3235, #3260) ──
        // InterpretDispatchResult returns an explicit DispatchInterpretOutcome alongside the IResult.
        //   PvcExhausted503           → 503 from PVC gate; caller emits PvcPoolExhaustions.Add(1)
        //   K8sError503               → 503 from K8s failure; return as-is
        //   PassThrough               → success; emit RecordDispatchAttempt("dispatched","none")
        var isKiroAgent = JobTemplateProviderType.IsKiro(template.ProviderType);
        var (interpretedResult, outcome) = InterpretDispatchResult(dispatchResult, pvcResult, isKiroAgent);

        // Branch exclusively on the explicit outcome discriminator (issue #3260).
        // No reference-equality or re-matched status-code checks remain here.
        return ApplyDispatchOutcomeSwitch(outcome, interpretedResult);
    }

    /// <summary>
    /// Spec 057 req 3.1, 3.2, 7.1, 7.4, 7.5: the answer for a Pending item a concurrency limit holds. The item stays
    /// Pending; the Scheduler stops the selector for the cycle (<c>concurrency_limit</c>) or ends the cycle
    /// (<c>global_concurrency_limit</c>).
    /// </summary>
    private static Microsoft.AspNetCore.Http.HttpResults.Ok<DispatchPendingResponse> HoldForLimit(AgentStartDecision decision, string sanitizedSelector)
    {
        var scope = AgentCapacityRules.TagValue(decision.Scope!.Value);
        var source = AgentCapacityRules.TagValue(decision.Source!.Value);
        Activity.Current?.SetTag("workdistribution.agents.running", decision.Running);
        Activity.Current?.SetTag("workdistribution.agents.limit", decision.Limit);
        Activity.Current?.SetTag("workdistribution.limit_scope", scope);
        Activity.Current?.SetTag("workdistribution.limit_source", source);
        Log.Information(
            "{CallerName}: {Scope} concurrency limit reached for selector {Selector} ({Current}/{Max}, {LimitSource}) — deferred",
            "DispatchPendingWorkItem", scope, sanitizedSelector, decision.Running, decision.Limit, source);
        WorkDistributionTelemetry.RecordDispatchAttempt(DeferredResult, decision.Reason!);
        return TypedResults.Ok(new DispatchPendingResponse(false, decision.Reason!));
    }

    /// <summary>
    /// Maps a <see cref="DispatchInterpretOutcome"/> to the final <see cref="IResult"/> for the
    /// <c>DispatchPendingWorkItemAsync</c> path. Extracted from the inline switch so the
    /// <c>UnreachableException</c> arm is directly testable (issue #3309).
    /// </summary>
    /// <param name="outcome">The outcome discriminator returned by <see cref="InterpretDispatchResult"/>.</param>
    /// <param name="interpretedResult">The (possibly rewritten) <see cref="IResult"/> from <see cref="InterpretDispatchResult"/>.</param>
    /// <returns>The <see cref="IResult"/> to return to the caller.</returns>
    /// <exception cref="UnreachableException">
    /// Thrown when <paramref name="outcome"/> is not a known <see cref="DispatchInterpretOutcome"/> value.
    /// This arm is dead code under current callers — it fires only if a new enum variant is added
    /// without updating this switch (issue #3309).
    /// </exception>
    internal static IResult ApplyDispatchOutcomeSwitch(DispatchInterpretOutcome outcome, IResult interpretedResult) =>
        outcome switch
        {
            DispatchInterpretOutcome.PvcExhausted503 => EmitPvcExhaustionAndReturn(interpretedResult),
            DispatchInterpretOutcome.K8sError503 => interpretedResult,
            DispatchInterpretOutcome.PassThrough => DispatchSuccessFallThrough(),
            _ => throw new UnreachableException($"Unhandled DispatchInterpretOutcome value: {outcome}")
        };

    private static IResult EmitPvcExhaustionAndReturn(IResult r)
    {
        WorkDistributionTelemetry.PvcPoolExhaustions.Add(1);
        return r;
    }

    private static IResult DispatchSuccessFallThrough()
    {
        WorkDistributionTelemetry.RecordDispatchAttempt("dispatched", "none");
        return TypedResults.Ok(new DispatchPendingResponse(true, "none"));
    }

    // ── Moved helpers (from WorkItemDispatchEndpoints, issue #3286) ──────────────

    /// <summary>
    /// Enters the agent start gate (spec 057; before: a per-selector advisory lock) and performs the post-lock
    /// TOCTOU status re-read. Moved from <see cref="CodingAgent.Api.WorkItemDispatchEndpoints"/> (issue #3286).
    ///
    /// <para>
    /// Returns <c>(null, earlyReturn)</c> when the lock timed out (503), the running agents could not be counted
    /// (503), or the item is no longer <c>Pending</c> after the lock was taken (200/deferred). The caller must return
    /// <c>earlyReturn</c> immediately without proceeding to the dispatch lifecycle.
    /// </para>
    /// <para>
    /// Returns <c>(lease, null)</c> when the item is <c>Pending</c> and the lease was entered.
    /// The caller <b>must</b> <c>await using</c> the returned lease to hold the lock for the full dispatch
    /// lifecycle scope:
    /// <code>
    /// var (lease, earlyReturn) = await TryEnterDispatchAsync(...);
    /// if (earlyReturn is not null) return earlyReturn;
    /// await using var _ = lease!;
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="gate">The agent start gate; its lease holds the <c>dispatch-agents</c> lock.</param>
    /// <param name="db">Caller-owned open <see cref="PipelineDbContext"/> for the post-lock re-read.
    /// Must use <c>AsNoTracking()</c> internally — see Risk 6 in the issue analysis.</param>
    /// <param name="id">Work-item GUID to re-read after the lock was taken.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>(lease, null)</c> on success (item is Pending, lock held);
    /// <c>(null, earlyReturn)</c> on lock timeout or count failure (503), or post-lock non-Pending (200/deferred).
    /// </returns>
    internal static async Task<(IAgentStartLease? lease, IResult? earlyReturn)> TryEnterDispatchAsync(
        IAgentStartGate gate,
        PipelineDbContext db,
        Guid id,
        CancellationToken ct)
    {
        IAgentStartLease acquiredLease;
        try
        {
            acquiredLease = await gate.EnterAsync(ct);
        }
        catch (TimeoutException)
        {
            // PostgresDistributedLockProvider retries with pg_try_advisory_lock for up to 60s.
            // A timeout means another start has held the lock for that entire window (e.g. a slow
            // K8s API response). Return 503 — transient, the Scheduler should retry next cycle.
            Log.Warning( // NOSONAR S6667 — expected lock timeout; the message says so
                "DispatchPendingWorkItem: agent start lock acquisition timed out for WorkItem {WorkItemId} — returning 503",
                id);
            WorkDistributionTelemetry.RecordDispatchAttempt(TransientResult, "lock_timeout");
            return (null, TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }
        catch (AgentCountUnavailableException)
        {
            // Spec 057 req 2.4: without a count no agent starts. The gate logged the cause; retried next cycle.
            WorkDistributionTelemetry.RecordDispatchAttempt(TransientResult, "agent_count_unavailable");
            return (null, TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }

        // Wrap the post-lock body in try/catch so that any transient exception (e.g. a
        // NpgsqlException on the re-read query) disposes the lease before propagating.
        // Without this guard the lock is held until the underlying connection is recycled,
        // blocking all concurrent agent starts (issue #3701).
        //
        // The TimeoutException catch above is intentionally outside this scope — it fires before
        // acquiredLease is assigned and is unaffected by this change.
        //
        // leaseTransferred guards against double-dispose: the non-Pending path calls
        // acquiredLease.DisposeAsync() inside the try block and then returns. If that disposal
        // throws, the catch fires. Without the flag the catch would call DisposeAsync() a second
        // time, violating the IAsyncDisposable contract (issue #3795).
        bool leaseTransferred = false;
        try
        {
            // Post-lock status re-read: close the TOCTOU window.
            // Another dispatch path (WorkItemDispatchLoop, or a concurrent call
            // to this endpoint) may have transitioned the item from Pending → Dispatched between
            // the fast-path check and lock acquisition. The lock serialises concurrent
            // callers on this endpoint; without this re-read, the loser would enter
            // ExecuteDispatchLifecycleAsync, find the item no longer Pending, exit early
            // (dispatched = false), and the endpoint would return 503. The Scheduler interprets
            // 503 as a transient error and schedules an unnecessary full-cycle retry.
            //
            // AsNoTracking is mandatory: a tracked query would return the EF first-level cache
            // snapshot from the fast-path check (which saw Pending), masking the intervening
            // state transition. Test 15 (DispatchPendingWorkItem_ConcurrentDispatch_LosingCallerReceives409Conflict)
            // relies on this fresh-query behaviour.
            //
            // Nullable projection: a missing row returns null (WorkItems use status transitions, not
            // DELETE) and is treated as non-Pending to avoid dispatching a ghost.
            var postLockCheck = await db.WorkItems.AsNoTracking()
                .Select(w => new { w.Id, w.Status })
                .FirstOrDefaultAsync(w => w.Id == id, ct);

            if (postLockCheck is null || postLockCheck.Status != WorkItemStatus.Pending)
            {
                Log.Information(
                    "DispatchPendingWorkItem: WorkItem {WorkItemId} is no longer Pending after lock acquisition (status={Status}) — returning 200/deferred",
                    id, postLockCheck?.Status);
                WorkDistributionTelemetry.RecordDispatchAttempt(DeferredResult, NotPendingReason);
                // Set leaseTransferred before calling DisposeAsync: if the disposal throws the
                // catch block will see leaseTransferred = true and will not attempt a second
                // disposal, preventing the double-dispose SonarQube S3966 flagged (issue #3795).
                leaseTransferred = true;
                await acquiredLease.DisposeAsync();
                return (null, TypedResults.Ok(new DispatchPendingResponse(false, NotPendingReason)));
            }

            leaseTransferred = true;
            return (acquiredLease, null);
        }
        catch
        {
            if (!leaseTransferred)
                await acquiredLease.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Interprets the raw <see cref="IResult"/> returned by
    /// <see cref="DispatchResolvedWorkItemAsync"/> and applies path-specific telemetry (issue #3235, moved from
    /// <see cref="CodingAgent.Api.WorkItemDispatchEndpoints"/> by issue #3286). Concurrency limits never reach this
    /// method: the agent start gate answers them before the lifecycle runs (spec 057).
    ///
    /// <para>
    /// <strong>Telemetry boundary — NOT emitted inside this helper:</strong>
    /// <list type="bullet">
    ///   <item><c>WorkDistributionTelemetry.PvcPoolExhaustions.Add(1)</c> — belongs exclusively
    ///     to the <see cref="DispatchPendingWorkItemAsync"/> path; the caller emits it after
    ///     checking the returned result.</item>
    ///   <item><c>WorkDistributionTelemetry.RecordDispatchAttempt("dispatched","none")</c> — belongs
    ///     exclusively to the <see cref="DispatchPendingWorkItemAsync"/> success fall-through; the
    ///     caller emits it when the returned result is not a 503.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <strong>503 disambiguation note:</strong> <paramref name="pvcResult"/> was computed inside the agent start
    /// lease, before <c>DispatchResolvedWorkItemAsync</c> ran. The heuristic relies on gate-ordering (PVC gate before
    /// K8s lifecycle) being a structural invariant.
    /// </para>
    /// </summary>
    /// <param name="rawResult">The result returned by <c>DispatchResolvedWorkItemAsync</c>.</param>
    /// <param name="pvcResult">PVC availability snapshot taken before dispatch. Used for 503 disambiguation.</param>
    /// <param name="isKiroAgent"><c>true</c> when the resolved template targets a kiro provider.</param>
    /// <returns>
    /// A tuple of the result and an explicit <see cref="DispatchInterpretOutcome"/>
    /// discriminator. The caller uses the outcome to branch without reference-equality or re-matched
    /// status-code checks (issue #3260).
    /// </returns>
    internal static (IResult Result, DispatchInterpretOutcome Outcome) InterpretDispatchResult(
        IResult rawResult,
        PvcAvailabilityResult pvcResult,
        bool isKiroAgent)
    {
        // ── 503: PVC exhaustion or K8s failure ───────────────────────────────
        if (rawResult is Microsoft.AspNetCore.Http.IStatusCodeHttpResult { StatusCode: 503 })
        {
            // Gate ordering: ApplyGates runs the PVC gate before the K8s lifecycle.
            // The PVC gate fires if AvailablePvcs is empty — same condition PvcPoolExhaustions
            // has always checked. Use PVC availability to distinguish the two sub-cases.
            // Note: the disambiguation relies on the gate-ordering guarantee (PVC gate
            // before K8s lifecycle) being a structural invariant — if that ordering ever changes,
            // this heuristic may misclassify k8s_error as pvc_unavailable or vice versa.
            if (!pvcResult.AvailablePvcs.Any() && isKiroAgent)
            {
                WorkDistributionTelemetry.RecordDispatchAttempt(TransientResult, "pvc_unavailable");
                return (rawResult, DispatchInterpretOutcome.PvcExhausted503);
            }

            WorkDistributionTelemetry.RecordDispatchAttempt(TransientResult, "k8s_error");
            return (rawResult, DispatchInterpretOutcome.K8sError503);
        }

        // ── Success or any other result ───────────────────────────────────────
        // Pass through unchanged. The DispatchPendingWorkItemAsync caller emits
        // RecordDispatchAttempt("dispatched","none") and wraps in DispatchPendingResponse(true,"none").
        return (rawResult, DispatchInterpretOutcome.PassThrough);
    }
}

/// <summary>
/// The fields <c>DispatchPendingWorkItem</c>'s fast-path query reads before entering the agent start gate
/// (no Payload loaded). See <see cref="DispatchWorkItemService.BuildProjectionFromQuickCheck"/>.
/// </summary>
internal sealed record DispatchQuickCheck
{
    public required Guid Id { get; init; }
    public required WorkItemStatus Status { get; init; }
    public required string AgentSelector { get; init; }
    public required int TimeoutSeconds { get; init; }
    public required WorkItemTaskType TaskType { get; init; }
    public Guid? ProjectId { get; init; }
    public required string IssueIdentifier { get; init; }
    public required string IssueProviderConfigId { get; init; }
    public required int PriorityWeight { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Per-call inputs of <see cref="DispatchWorkItemService.DispatchResolvedWorkItemAsync"/>, built by
/// <c>DispatchPendingWorkItem</c>.
/// </summary>
internal sealed record ResolvedDispatchRequest
{
    /// <summary>Caller-owned open <see cref="PipelineDbContext"/>.</summary>
    public required PipelineDbContext Db { get; init; }

    /// <summary>Resolved <see cref="JobTemplate"/> for the selector.</summary>
    public required JobTemplate Template { get; init; }

    /// <summary>
    /// Pre-constructed <see cref="PendingWorkItemProjection"/>. Each handler builds this from
    /// its own field sources; construction cannot be unified in the dispatch helper.
    /// </summary>
    public required PendingWorkItemProjection Projection { get; init; }

    /// <summary>CRLF-stripped form of the selector for log messages.</summary>
    public required string SanitizedSelector { get; init; }

    /// <summary>
    /// PVC availability snapshot produced by the caller via
    /// <see cref="DispatchLifecycleService.QueryDispatchablePvcsAsync"/> inside the agent start lease.
    /// </summary>
    public required PvcAvailabilityResult PvcResult { get; init; }

    /// <summary>
    /// Prefix embedded in K8s Job names and log messages: <c>"pending-dispatch "</c>.
    /// </summary>
    public required string LogPrefix { get; init; }

    /// <summary>Factory producing the 200 <see cref="IResult"/> when dispatch succeeds.</summary>
    public required Func<Guid, IResult> OnSuccess { get; init; }

    /// <summary>Work-item GUID for log messages and the success result.</summary>
    public required Guid WorkItemId { get; init; }

    /// <summary>Short name for log messages (e.g. <c>"DispatchPendingWorkItem"</c>).</summary>
    public required string CallerName { get; init; }
}
