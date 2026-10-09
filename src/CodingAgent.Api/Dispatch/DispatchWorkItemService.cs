using System.Diagnostics;
using System.Text.Json;
using CodingAgent.Api;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// Shared helpers for the two synchronous dispatch handlers:
/// <see cref="CodingAgent.Api.WorkItemDispatchEndpoints.DispatchWorkItem"/> and
/// <see cref="CodingAgent.Api.WorkItemDispatchEndpoints.DispatchPendingWorkItem"/>.
///
/// <para>
/// Extracted from <c>WorkItemDispatchEndpoints.cs</c> (issue #2743) to eliminate the
/// duplicated concurrency-map query, gate block, entity-construction, and unique-violation
/// fallback that previously appeared independently in each handler.
/// Further extraction (issue #2988): <see cref="BuildDispatchPreambleAsync"/> composes the
/// snapshot + PVC queries into one call;
/// <see cref="BuildProjectionFromEntity"/> and <see cref="BuildProjectionFromQuickCheck"/>
/// centralise the <see cref="CodingAgent.Orchestration.Dispatch.PendingWorkItemProjection"/>
/// initializer that was duplicated in both handlers.
/// </para>
///
/// <para>
/// Registered in DI as <c>AddSingleton</c>. The service is stateless and depends only on
/// <see cref="JobTemplateStore"/> (also a singleton). It does NOT hold a
/// <see cref="IDbContextFactory{TContext}"/> — callers pass the already-open
/// <see cref="PipelineDbContext"/> into <see cref="BuildConcurrencySnapshotAsync"/> so that
/// the advisory-lock context used by <c>DispatchPendingWorkItem</c> is reused correctly.
/// </para>
/// </summary>
internal sealed class DispatchWorkItemService
{
    private const string DeferredResult = "deferred";
    private const string NotPendingReason = "not_pending";

    private readonly JobTemplateStore _templateStore;

    public DispatchWorkItemService(JobTemplateStore templateStore)
    {
        ArgumentNullException.ThrowIfNull(templateStore);
        _templateStore = templateStore;
    }

    // ── Concurrency snapshot ─────────────────────────────────────────────────

    /// <summary>
    /// Builds the Dispatched||Running concurrency map keyed on the selector of the template each active
    /// item runs under (see <see cref="ResolveTemplateAsync"/>): the normalized AgentSelector, or for an
    /// item dispatched through the profile fallback, the profile's labels its partial selector expands to.
    ///
    /// <para>
    /// Replaces the identical 8-line LINQ blocks that appeared independently in
    /// <c>DispatchPendingWorkItem</c> (~L375) and <c>DispatchWorkItem</c> (~L588).
    /// </para>
    ///
    /// <para>
    /// <strong>Important:</strong> callers must pass their own open
    /// <see cref="PipelineDbContext"/>. Do NOT open a new context inside this method — for
    /// <c>DispatchPendingWorkItem</c> the supplied context is the advisory-locked one, and
    /// opening a fresh context here would bypass that lock, re-introducing the TOCTOU window
    /// the lock was added to close.
    /// </para>
    /// </summary>
    /// <param name="db">An open, caller-owned <see cref="PipelineDbContext"/>.</param>
    /// <param name="templateResolver">Resolves the partial selectors of items dispatched through the profile fallback.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Dictionary mapping template selector → active-item count.
    /// Empty dictionary when no active items exist.
    /// </returns>
    internal async Task<Dictionary<string, int>> BuildConcurrencySnapshotAsync(
        PipelineDbContext db,
        DispatchTemplateResolver templateResolver,
        CancellationToken ct)
    {
        var activeCounts = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
            .GroupBy(w => w.AgentSelector)
            .Select(g => new { Selector = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Aggregate by normalized key — multiple raw selectors may normalize to the same key
        // (e.g. "kiro,dotnet" and "dotnet,kiro" both normalize to "dotnet,kiro"). Sum their counts
        // so that all active items contribute to the correct concurrency-gate lookup.
        // TODO [WARNING]: The previous implementation used ToDictionary() which would throw
        // ArgumentException if the GroupBy result contained two rows with the same raw selector
        // (should be impossible under EF/Postgres GroupBy semantics, but would surface as a
        // crash-detectable invariant violation). This foreach silently merges such rows instead,
        // which is arguably more correct but hides the data-consistency anomaly. If a duplicate
        // raw-selector anomaly occurs in production it will be silently absorbed rather than logged.
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in activeCounts)
        {
            // An item dispatched through the profile fallback keeps its stored partial selector ("dotnet")
            // but runs under the profile's template ("dotnet,kiro"). Count it under that template's selector,
            // the key DispatchPendingWorkItemAsync checks, or the gate never sees the items the fallback dispatched.
            var (_, templateKey) = await ResolveTemplateAsync(
                item.Selector, templateResolver, "BuildConcurrencySnapshot", warnOnExpansion: false, ct);
            result[templateKey] = result.GetValueOrDefault(templateKey, 0) + item.Count;
        }
        return result;
    }

    /// <summary>
    /// The <see cref="JobTemplate"/> for <paramref name="agentSelector"/> by direct lookup, without the
    /// profile fallback. Used by <c>DispatchWorkItem</c>, which answers 422 for a selector without a template.
    /// </summary>
    internal JobTemplate? ResolveTemplate(string agentSelector) => _templateStore.Resolve(agentSelector);

    /// <summary>
    /// Resolves the <see cref="JobTemplate"/> an item stored with <paramref name="agentSelector"/> is
    /// dispatched under, and the selector that template is keyed on: a direct lookup of the normalized
    /// selector first, then the profile fallback, which expands a partial selector such as <c>"dotnet"</c>
    /// to the profile's labels, <c>"dotnet,kiro"</c>. The concurrency snapshot, the gate and the selector lock
    /// all key on that selector.
    /// </summary>
    /// <returns>
    /// The template, or <c>null</c> when neither lookup finds one; and the template's selector, or the
    /// normalized <paramref name="agentSelector"/> when no template is found.
    /// </returns>
    private async Task<(JobTemplate? Template, string EffectiveSelector)> ResolveTemplateAsync(
        string agentSelector,
        DispatchTemplateResolver templateResolver,
        string callerName,
        bool warnOnExpansion,
        CancellationToken ct)
    {
        var normalizedSelector = JobTemplateStore.NormalizeLabels(agentSelector);
        var template = _templateStore.Resolve(normalizedSelector);
        if (template is not null)
            return (template, normalizedSelector);

        // The profile lookup takes the raw selector (it matches MatchLabels); its result is normalized here
        // because profile labels are joined without trimming.
        var (fallbackTemplate, fallbackSelector) = await templateResolver.ResolveTemplateViaProfileAsync(
            agentSelector, callerName, warnOnExpansion, ct);
        return fallbackTemplate is not null && fallbackSelector is not null
            ? (fallbackTemplate, JobTemplateStore.NormalizeLabels(fallbackSelector))
            : (null, normalizedSelector);
    }

    // ── Dispatch preamble ────────────────────────────────────────────────────

    /// <summary>
    /// Builds the dispatch preamble shared by <c>DispatchPendingWorkItem</c> and
    /// <c>DispatchWorkItem</c>: the concurrency snapshot and the PVC availability result (issue #2988).
    ///
    /// <para>
    /// Composes <see cref="BuildConcurrencySnapshotAsync"/> with
    /// <see cref="DispatchLifecycleService.GetPvcPool"/> and
    /// <see cref="DispatchLifecycleService.QueryAvailablePvcsAsync"/> into a single call,
    /// eliminating the three-line preamble that was duplicated in both handlers.
    /// </para>
    ///
    /// <para>
    /// <strong>Caller responsibilities (not absorbed here):</strong>
    /// <list type="bullet">
    ///   <item>
    ///     <c>DispatchPendingWorkItem</c>: call this method <em>inside</em> the advisory lock,
    ///     after the post-lock status re-check. The lock-ordering constraint cannot be enforced
    ///     by this method — it is the caller's responsibility.
    ///   </item>
    ///   <item>
    ///     <c>DispatchPendingWorkItem</c>: emit
    ///     <c>WorkDistributionTelemetry.UpdateCredentialPoolMetrics</c> immediately after this
    ///     call, using <c>pvcResult.AvailablePvcs.Count</c> and <c>pvcResult.ClaimedCount</c>
    ///     from the returned tuple. That metric belongs exclusively to the
    ///     <c>DispatchPendingWorkItem</c> path and must NOT be absorbed here.
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="db">Caller-owned open <see cref="PipelineDbContext"/>. Must be the same
    /// context used for any subsequent calls on this request (e.g. the advisory-locked context
    /// in <c>DispatchPendingWorkItem</c>).</param>
    /// <param name="lifecycle">The <see cref="DispatchLifecycleService"/> singleton that owns
    /// the PVC pool configuration.</param>
    /// <param name="templateResolver">Passed to <see cref="BuildConcurrencySnapshotAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of the concurrency snapshot (from <see cref="BuildConcurrencySnapshotAsync"/>)
    /// and the PVC availability result (from
    /// <see cref="DispatchLifecycleService.QueryAvailablePvcsAsync"/>).
    /// </returns>
    internal async Task<(Dictionary<string, int> concurrencyBySelector, PvcAvailabilityResult pvcResult)>
        BuildDispatchPreambleAsync(
            PipelineDbContext db, DispatchLifecycleService lifecycle, DispatchTemplateResolver templateResolver, CancellationToken ct)
    {
        var concurrencyBySelector = await BuildConcurrencySnapshotAsync(db, templateResolver, ct);
        var pvcPool = lifecycle.GetPvcPool();
        var pvcResult = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, pvcPool, ct);
        return (concurrencyBySelector, pvcResult);
    }

    // ── Projection factories ─────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="PendingWorkItemProjection"/> from a <see cref="WorkItemEntity"/>.
    /// Used by <c>DispatchWorkItem</c>, which creates an entity directly as Dispatched and then
    /// constructs the projection from that entity (issue #2988).
    /// </summary>
    /// <param name="entity">The already-persisted <see cref="WorkItemEntity"/>.</param>
    internal static PendingWorkItemProjection BuildProjectionFromEntity(WorkItemEntity entity) =>
        new PendingWorkItemProjection
        {
            Id = entity.Id,
            AgentSelector = entity.AgentSelector,
            CreatedAt = entity.CreatedAt,
            TimeoutSeconds = entity.TimeoutSeconds,
            TaskType = entity.TaskType,
            ProjectId = entity.ProjectId,
            IssueIdentifier = entity.IssueIdentifier,
            IssueProviderConfigId = entity.IssueProviderConfigId,
            PriorityWeight = entity.PriorityWeight
        };

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
    /// Applies the concurrency gate and PVC gate.
    ///
    /// <para>
    /// Returns a non-null <see cref="IResult"/> (409 or 503) when any gate fires;
    /// returns <c>null</c> when all gates pass.
    /// </para>
    ///
    /// <para>
    /// <strong>Telemetry note:</strong> this method does NOT emit
    /// <c>WorkDistributionTelemetry.PvcPoolExhaustions</c> or
    /// <c>WorkDistributionTelemetry.UpdateCredentialPoolMetrics</c>.
    /// Those calls are path-specific (<c>DispatchPendingWorkItem</c> only) and must
    /// remain in the respective handler, outside this method.
    /// </para>
    ///
    /// <para>
    /// The concurrency check delegates to
    /// <see cref="DispatchStateBuilder.IsAtConcurrencyLimit"/> which already normalises the
    /// zero/unlimited semantics — no further normalization is needed.
    /// </para>
    /// </summary>
    /// <param name="normalizedSelector">
    /// Normalized agent-selector key (output of <see cref="JobTemplateStore.NormalizeLabels"/>).
    /// Used as the concurrency-map lookup key and in the 409 response body.
    /// </param>
    /// <param name="sanitizedSelector">
    /// Log-safe (CRLF-stripped) form of the selector, for embedding in response bodies and log messages.
    /// </param>
    /// <param name="concurrencyBySelector">
    /// Concurrency snapshot produced by <see cref="BuildConcurrencySnapshotAsync"/>.
    /// </param>
    /// <param name="pvcResult">PVC availability snapshot.</param>
    /// <param name="template">Resolved <see cref="JobTemplate"/> for this selector.</param>
    /// <param name="isKiroAgent"><c>true</c> when the template targets a kiro provider.</param>
    /// <param name="callerName">Short name used in log messages (e.g. <c>"DispatchWorkItem"</c>).</param>
    /// <returns>
    /// A non-null <see cref="IResult"/> if a gate fires; <c>null</c> when all gates pass.
    /// </returns>
    internal IResult? ApplyGates( // NOSONAR S2325 — instance API: callers and tests use an instance
        string normalizedSelector,
        string sanitizedSelector,
        Dictionary<string, int> concurrencyBySelector,
        PvcAvailabilityResult pvcResult,
        JobTemplate template,
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

        // TODO [WARNING]: `sanitizedSelector` is embedded into the Conflict response body
        // ($"Concurrency limit reached for selector '{sanitizedSelector}' ..."). The parameter
        // name implies CRLF-stripping has already been applied by the caller (via
        // LogSanitizer.SanitizeForLog), and both current call sites do apply sanitization before
        // passing it. The contract is implicit — this method accepts a plain `string` with no
        // enforcement that sanitization was performed. A future caller passing a raw, unsanitized
        // selector sourced from user input would reflect that input into the HTTP response body,
        // enabling response-splitting (raw HTTP/1.1) or stored XSS if the body is rendered
        // unescaped in a frontend. Fix: add an internal debug-mode assertion or rename to
        // a sanitized-string wrapper type to make the contract explicit.

        // Concurrency gate — delegates to DispatchStateBuilder.IsAtConcurrencyLimit
        // which already handles maxConcurrent <= 0 as "no limit".
        if (DispatchStateBuilder.IsAtConcurrencyLimit(normalizedSelector, concurrencyBySelector, template.MaxConcurrent))
        {
            var currentCount = concurrencyBySelector.GetValueOrDefault(normalizedSelector, 0);
            Log.Information(
                "{CallerName}: concurrency limit reached for selector {Selector} ({Current}/{Max}) — returning 409",
                callerName, sanitizedSelector, currentCount, template.MaxConcurrent);
            return TypedResults.Conflict(
                $"Concurrency limit reached for selector '{sanitizedSelector}' ({currentCount}/{template.MaxConcurrent}).");
        }

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

    // ── Entity factory ───────────────────────────────────────────────────────

    /// <summary>
    /// Constructs a <see cref="WorkItemEntity"/> from a <see cref="JobDistributionRequest"/>.
    ///
    /// <para>
    /// Replaces the near-identical entity-construction blocks in <c>CreateWorkItem</c>
    /// (~L97–120) and <c>DispatchWorkItem</c> (~L640–660). The <paramref name="status"/>
    /// and <paramref name="dispatchedAt"/> parameters handle the behavioural difference
    /// between the two paths:
    /// <list type="bullet">
    ///   <item><c>CreateWorkItem</c> path — <c>Status = Pending</c>, <c>DispatchedAt = null</c>.</item>
    ///   <item><c>DispatchWorkItem</c> path — <c>Status = Dispatched</c>, <c>DispatchedAt = DateTimeOffset.UtcNow</c>.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <c>DispatchPendingWorkItem</c> does NOT use this factory — that handler claims an
    /// already-existing Pending row and builds a <see cref="PendingWorkItemProjection"/>, not a
    /// new <see cref="WorkItemEntity"/>.
    /// </para>
    /// </summary>
    internal static WorkItemEntity CreateWorkItemEntity(
        Guid workItemId,
        JobDistributionRequest request,
        WorkItemStatus status,
        DateTimeOffset? dispatchedAt,
        string payloadJson)
    {
        return new WorkItemEntity
        {
            Id = workItemId,
            TaskType = request.TaskType,
            IssueIdentifier = request.IssueIdentifier.Value,
            IssueProviderConfigId = request.IssueProviderConfigId,
            Status = status,
            DispatchedAt = dispatchedAt,
            Payload = payloadJson,
            AgentSelector = JobTemplateStore.NormalizeLabels(request.AgentSelector ?? ""),
            // Clamp zero/negative TimeoutSeconds to DefaultAgentTimeout (issue #2745).
            // A zero stored value causes ReconciliationLoop to immediately force-fail Running items
            // because the elapsed time always exceeds the zero timeout.
            TimeoutSeconds = request.TimeoutSeconds > 0
                ? request.TimeoutSeconds
                : (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds,
            ProjectId = request.ProjectId,
            CreatedAt = DateTimeOffset.UtcNow,
            PriorityWeight = InitiatedByConstants.IsManual(request.InitiatedBy) ? 100 : 0,
            TraceParent = request.TraceContext?.GetValueOrDefault("traceparent")
                ?? PipelineTelemetry.FormatTraceParent(Activity.Current)
        };
    }

    // ── Unique-violation fallback ────────────────────────────────────────────

    /// <summary>
    /// Returns the shared <c>"A live work item already exists for this issue."</c>
    /// <see cref="IResult"/> used as the final fallback in unique-constraint violation handlers.
    ///
    /// <para>
    /// This covers only the shared final fallback line — it does NOT replace the
    /// <c>DispatchWorkItem</c>-specific active-state branching (200 for Dispatched/Running,
    /// 409 for non-active states), which remains inline in the handler.
    /// </para>
    /// </summary>
    internal static IResult HandleUniqueViolationFallback()
        => TypedResults.Conflict("A live work item already exists for this issue.");

    // ── Unique-violation idempotent-retry resolver ───────────────────────────────

    /// <summary>
    /// Handles the <c>catch (Exception ex) when (IsUniqueViolation(ex))</c> body that was
    /// duplicated across <c>CreateWorkItem</c> and <c>DispatchWorkItem</c>.
    ///
    /// <para>
    /// Opens a fresh <see cref="PipelineDbContext"/> (the original context is faulted after a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>) and re-queries by
    /// <paramref name="workItemId"/>:
    /// <list type="bullet">
    ///   <item>
    ///     <c>isNewlyCreated=true</c> (<c>CreateWorkItem</c> path): checks whether any row with
    ///     this ID exists. Returns 201 if the row exists (idempotent PK retry); otherwise
    ///     delegates to <see cref="HandleUniqueViolationFallback"/> (partial unique-index conflict).
    ///   </item>
    ///   <item>
    ///     <c>isNewlyCreated=false</c> (<c>DispatchWorkItem</c> path): projects the existing
    ///     row's status. Returns 200 if active (<c>Dispatched</c> or <c>Running</c>); returns
    ///     409 Conflict with a status message if non-active; otherwise delegates to
    ///     <see cref="HandleUniqueViolationFallback"/>.
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="dbFactory">
    /// Factory used to open a fresh context. Must NOT be the faulted context from the caller's
    /// <c>SaveChangesAsync</c> path.
    /// </param>
    /// <param name="workItemId">The ID of the work item that triggered the unique violation.</param>
    /// <param name="isNewlyCreated">
    /// <c>true</c> when called from <c>CreateWorkItem</c>; <c>false</c> when called from
    /// <c>DispatchWorkItem</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<IResult> HandleUniqueViolationAsync(
        IDbContextFactory<PipelineDbContext> dbFactory,
        Guid workItemId,
        bool isNewlyCreated,
        CancellationToken ct)
    {
        await using var freshDb = await dbFactory.CreateDbContextAsync(ct);

        if (isNewlyCreated)
        {
            // CreateWorkItem path: existence check only.
            // Idempotent PK retry — row already exists with this RunId → return 201 so
            // EnsureSuccessStatusCode() on the retried response succeeds.
            var exists = await freshDb.WorkItems.AnyAsync(w => w.Id == workItemId, ct);
            if (exists)
                return TypedResults.Created($"/api/work-items/{workItemId}", workItemId);
        }
        else
        {
            // DispatchWorkItem path: status-aware check.
            // Return 200 only when the existing item is still active (Dispatched or Running).
            // For terminal or Pending states, return 409 so the Scheduler re-queues the issue
            // rather than treating it as dispatched — a Failed/Cancelled item has no K8s Job
            // and returning 200 would cause DispatchOrchestrationService to swap the GitHub
            // label to agent:in-progress with no running job.
            var existing = await freshDb.WorkItems.AsNoTracking()
                .Select(w => new { w.Id, w.Status })
                .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (existing is not null)
            {
                var isActive = existing.Status is WorkItemStatus.Dispatched or WorkItemStatus.Running;
                if (isActive)
                    return TypedResults.Ok(workItemId);
                Log.Warning("DispatchWorkItemService.HandleUniqueViolationAsync: idempotent retry for {WorkItemId} but existing item is in non-active state {Status} — returning 409",
                    workItemId, existing.Status);
                return TypedResults.Conflict($"Work item {workItemId} already exists in non-active state {existing.Status}.");
            }
        }

        // Postgres 23505: partial unique index on (IssueIdentifier, IssueProviderConfigId)
        // for non-terminal statuses — a different run is already live for this issue.
        return HandleUniqueViolationFallback();
    }

    // ── Dispatch lifecycle executor ──────────────────────────────────────────────

    /// <summary>
    /// Encapsulates the <c>try/catch + !dispatched + 503</c> block that was duplicated inline
    /// in both <c>DispatchPendingWorkItem</c> and <c>DispatchWorkItem</c>.
    ///
    /// <para>
    /// Calls <see cref="DispatchLifecycleService.ExecuteDispatchLifecycleAsync"/> and returns:
    /// <list type="bullet">
    ///   <item>The result from <paramref name="onSuccess"/> when dispatch completes.</item>
    ///   <item>503 Service Unavailable when the lifecycle returns without dispatching (e.g. PVC
    ///     race, race-condition early exit) or when it throws a non-cancellation exception.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <strong>Caller responsibilities (preserved, not absorbed):</strong>
    /// <list type="bullet">
    ///   <item>
    ///     <c>WorkDistributionTelemetry.PvcPoolExhaustions</c> must be emitted by the caller
    ///     (<c>DispatchPendingWorkItem</c>) BEFORE this call, not inside this method.
    ///   </item>
    ///   <item>
    ///     <paramref name="onDispatchFailure"/> must be <c>null</c> for
    ///     <c>DispatchPendingWorkItem</c> (item started as Pending — no orphaned Dispatched row
    ///     to clean up) and <c>SafelyCancelOrphanedDispatchedWorkItemAsync</c> for
    ///     <c>DispatchWorkItem</c>.
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="ctx">
    /// Dispatch lifecycle context (built differently by each handler — not constructed here).
    /// </param>
    /// <param name="lifecycle">
    /// The shared singleton <see cref="DispatchLifecycleService"/>. Passed in (not resolved
    /// from DI) to preserve the <c>_pvcSelectLock</c> singleton semantics.
    /// </param>
    /// <param name="onDispatchFailure">
    /// Called with <c>(workItemId, reason)</c> on the 503 path.
    /// <c>null</c> on the <c>DispatchPendingWorkItem</c> path (no orphan cleanup needed).
    /// <c>SafelyCancelOrphanedDispatchedWorkItemAsync</c> on the <c>DispatchWorkItem</c> path.
    /// </param>
    /// <param name="onSuccess">
    /// Factory called with the dispatched work-item ID when dispatch succeeds.
    /// Returns the <see cref="IResult"/> the endpoint returns to the client (e.g. 200 OK).
    /// </param>
    /// <param name="workItemId">Work-item GUID for log messages.</param>
    /// <param name="callerName">Short caller name for log messages (e.g. <c>"DispatchWorkItem"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The success <see cref="IResult"/> on success; 503 on failure.</returns>
    internal async Task<IResult> RunDispatchLifecycleAsync( // NOSONAR S2325 — instance API: callers and tests use an instance
        DispatchLifecycleContext ctx,
        DispatchLifecycleService lifecycle,
        Func<Guid, string, Task>? onDispatchFailure,
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
            if (onDispatchFailure is not null)
                await onDispatchFailure(workItemId, $"Dispatch lifecycle threw: {ex.Message}");
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!dispatched)
        {
            Log.Warning("{CallerName}: lifecycle did not dispatch WorkItem {WorkItemId} (PVC race or K8s failure) — returning 503",
                callerName, workItemId);
            if (onDispatchFailure is not null)
                await onDispatchFailure(workItemId, "Dispatch did not complete (PVC race or K8s failure)");
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return onSuccess(workItemId);
    }

    // ── Shared gate + context + lifecycle entry point ────────────────────────

    // TODO [WARNING]: DispatchResolvedWorkItemAsync has no dedicated unit tests. It is reached indirectly
    // via SynchronousDispatchEndpointTests and DispatchPendingWorkItemEndpointTests, but the specific
    // branching inside this method — gate fires → return gate result; onDispatchFailure null vs non-null
    // on the 503 path; ExpectedInitialStatus routing — is never explicitly constrained. A regression that
    // incorrectly wires onDispatchFailure as always-null regardless of the caller's argument would not be
    // caught by the existing tests. Add direct unit tests for at minimum: (a) gate fires → returns gate
    // result without calling lifecycle; (b) 503 path with onDispatchFailure non-null → failure callback
    // is invoked; (c) success path → onSuccess result returned. (TestQualityReviewer review [WARNING])
    /// <summary>
    /// Runs the shared <c>isKiroAgent → ApplyGates → DispatchLifecycleContext → RunDispatchLifecycleAsync</c>
    /// sequence that was previously duplicated in both <c>DispatchPendingWorkItem</c> and
    /// <c>DispatchWorkItem</c> (issue #2890).
    ///
    /// <para>
    /// <strong>Caller responsibilities (not absorbed here):</strong>
    /// <list type="bullet">
    ///   <item>
    ///     Call <see cref="BuildConcurrencySnapshotAsync"/> and pass the result as
    ///     <see cref="ResolvedDispatchRequest.ConcurrencyBySelector"/>. The snapshot must be taken while holding
    ///     the advisory lock (on the <c>DispatchPendingWorkItem</c> path) or before entity
    ///     creation (on the <c>DispatchWorkItem</c> path) — the lock ordering cannot be
    ///     replicated inside this method.
    ///   </item>
    ///   <item>
    ///     Call <see cref="DispatchLifecycleService.QueryAvailablePvcsAsync"/> and pass the
    ///     result as <see cref="ResolvedDispatchRequest.PvcResult"/>. The <c>DispatchPendingWorkItem</c> path
    ///     also emits <c>WorkDistributionTelemetry.UpdateCredentialPoolMetrics</c> immediately
    ///     after that call — that metric must stay in the handler, not here.
    ///   </item>
    ///   <item>
    ///     Construct <see cref="PendingWorkItemProjection"/> from handler-specific sources
    ///     (<c>quickCheck.*</c> vs <c>entity.*</c>) and pass it as <see cref="ResolvedDispatchRequest.Projection"/>.
    ///   </item>
    ///   <item>
    ///     After this method returns, <c>DispatchPendingWorkItem</c> checks whether the result
    ///     is a 503 and emits <c>WorkDistributionTelemetry.PvcPoolExhaustions</c> — that counter
    ///     belongs exclusively to that path and must not be moved inside this method.
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
    /// A non-null <see cref="IResult"/> from <see cref="ApplyGates"/> if a gate fires;
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
        var concurrencyBySelector = request.ConcurrencyBySelector;
        var pvcResult = request.PvcResult;
        var callerName = request.CallerName;

        // Compute isKiroAgent exactly once via JobTemplateProviderType.IsKiro — the "kiro" literal lives there only (AC3).
        var isKiroAgent = JobTemplateProviderType.IsKiro(template.ProviderType);

        // Concurrency gate + PVC gate.
        // NOTE: PvcPoolExhaustions and UpdateCredentialPoolMetrics are NOT emitted here —
        // those telemetry calls belong exclusively to the DispatchPendingWorkItem handler.
        // TODO [WARNING]: On the DispatchWorkItem path, concurrencyBySelector was built before entity
        // creation. The WorkItem was persisted as Dispatched between the early gate (in the handler) and
        // this second ApplyGates call, so the snapshot does not reflect the newly created item. If the
        // concurrency limit is N and exactly N-1 items are active, the early gate passes (N-1 < N), the
        // Dispatched row is created (live count becomes N), and this gate also passes (sees N-1 < N).
        // This means the path can exceed the concurrency limit by 1. Pre-existing risk — not introduced
        // by this refactor — but the new double-gate comment ("capacity cannot shrink") is misleading:
        // capacity CAN appear to grow when this method is reached on the DispatchWorkItem path because
        // the row we just created is not yet reflected in the snapshot. (Correctness review [WARNING])
        var gateResult = ApplyGates(
            request.NormalizedSelector, request.SanitizedSelector, concurrencyBySelector,
            pvcResult, template, isKiroAgent, callerName);
        if (gateResult is not null)
            return gateResult;

        var ctx = new DispatchLifecycleContext(
            request.Db,
            request.Projection,
            template,
            isKiroAgent,
            pvcResult.AvailablePvcs,
            concurrencyBySelector,
            request.LogPrefix)
        {
            ExpectedInitialStatus = request.ExpectedInitialStatus
        };

        return await RunDispatchLifecycleAsync(
            ctx,
            lifecycle,
            request.OnDispatchFailure,
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
    /// normalization, lock acquisition, template resolution, concurrency gating, and result
    /// interpretation live here.
    /// </para>
    ///
    /// <para>
    /// <strong>Telemetry note:</strong>
    /// <c>WorkDistributionTelemetry.PvcPoolExhaustions.Add(1)</c> belongs exclusively to this path
    /// and is emitted here (not inside <see cref="InterpretDispatchResult"/> or
    /// <see cref="DispatchResolvedWorkItemAsync"/>). Do not emit it from <c>DispatchWorkItem</c>
    /// or <see cref="DispatchResolvedWorkItemAsync"/>.
    /// </para>
    /// </summary>
    /// <param name="id">The GUID of the Pending WorkItem to dispatch.</param>
    /// <param name="dbFactory">Factory for opening the caller-owned <see cref="PipelineDbContext"/>.</param>
    /// <param name="lifecycle">The shared singleton <see cref="DispatchLifecycleService"/>.</param>
    /// <param name="templateResolver">Used for profile-based template fallback resolution.</param>
    /// <param name="lockProvider">Distributed lock provider for the per-selector advisory lock.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <list type="bullet">
    ///   <item>200 <c>DispatchPendingResponse(true,"none")</c> — dispatch succeeded.</item>
    ///   <item>200 <c>DispatchPendingResponse(false,"not_pending"|"no_template"|"concurrency_limit")</c>
    ///     — expected backpressure outcome.</item>
    ///   <item>404 Not Found — no WorkItem with this ID.</item>
    ///   <item>503 Service Unavailable — no PVC available, advisory lock timeout, or K8s failure.</item>
    /// </list>
    /// </returns>
    internal async Task<IResult> DispatchPendingWorkItemAsync(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        DispatchLifecycleService lifecycle,
        DispatchTemplateResolver templateResolver,
        IDistributedLockProvider lockProvider,
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
        // Sanitize agentSelector before embedding it in any log message or HTTP response body.
        // agentSelector originates from a database column set by POST /api/work-items callers;
        // a crafted value containing \r\n can inject fake log lines (log forging). All log and
        // Conflict call sites below use sanitizedSelector / sanitizedEffectiveSelector instead
        // of the raw values. See also: LogSanitizer in CodingAgent.Infrastructure.Common.
        var sanitizedSelector = LogSanitizer.SanitizeForLog(agentSelector);

        // Template — try direct resolve first, then profile-based fallback (issue #2777). The
        // effective selector is the template's: "dotnet,kiro" for an item stored as "dotnet" that the
        // fallback resolved. The lock, the concurrency snapshot and the gate all key on it. Resolved
        // before the lock (templates and profiles are configuration, not locked state) because a lock
        // keyed on the stored "dotnet" would let this dispatch and one for an item stored as "dotnet,kiro"
        // run concurrently and both pass the same gate. A missing template is still reported after the
        // lock and the post-lock re-check, as before.
        var (template, effectiveSelector) = await ResolveTemplateAsync(
            agentSelector, templateResolver, "DispatchPendingWorkItem", warnOnExpansion: true, ct);
        var sanitizedEffectiveSelector = LogSanitizer.SanitizeForLog(effectiveSelector);

        // Acquire advisory lock keyed on the effective selector and perform the post-lock
        // TOCTOU re-read. Both are encapsulated in TryEnterSelectorDispatchAsync (issue #3235):
        // - Lock scope: snapshot → gate → CAS (inside ExecuteDispatchLifecycleAsync).
        // - Note: this lock only protects concurrent calls to THIS endpoint. DispatchWorkItem
        //   (collection-level) and WorkItemDispatchLoop (Scheduler background loop) do NOT acquire
        //   this lock. Cross-path correctness is provided by the CAS in ExecuteDispatchLifecycleAsync.
        var (lockHandle, lockEarlyReturn) = await TryEnterSelectorDispatchAsync(lockProvider, effectiveSelector, db, id, ct);
        if (lockEarlyReturn is not null)
            return lockEarlyReturn;
        await using var _ = lockHandle!;

        // Build the concurrency snapshot and PVC availability result via the shared preamble.
        // IMPORTANT: this call must remain inside the advisory lock, after the post-lock status
        // re-check. The lock-ordering constraint is: acquire lock → re-check status → preamble.
        // NOTE: do NOT use DispatchStateBuilder.BuildStateAsync here — it does NOT normalise
        // keys (uses raw x.Selector). BuildDispatchPreambleAsync keys each active item on its
        // template's selector so active items stored by any path are counted correctly.
        var (concurrencyBySelector, pvcResult) = await BuildDispatchPreambleAsync(db, lifecycle, templateResolver, ct);
        // Emit credential-pool gauge BEFORE the PVC gate so the metric is always updated
        // whenever the PVC query runs (including on PVC-exhaustion 503).
        // Deliberate exception to the BuildStateAsync restriction: this endpoint is the
        // Scheduler-driven dispatch path and is the appropriate emitter for this metric.
        WorkDistributionTelemetry.UpdateCredentialPoolMetrics(pvcResult.AvailablePvcs.Count, pvcResult.ClaimedCount);

        if (template is null)
        {
            Log.Warning("DispatchPendingWorkItem: no job template for selector {Selector} — returning 200/deferred", sanitizedSelector);
            WorkDistributionTelemetry.RecordDispatchAttempt(DeferredResult, "no_template");
            return TypedResults.Ok(new DispatchPendingResponse(false, "no_template"));
        }

        // Build the projection for the shared dispatch helper (issue #2988). Its AgentSelector is the
        // canonical effectiveSelector, which the Job label and the dispatch metrics carry (#2777).
        var projection = BuildProjectionFromQuickCheck(quickCheck, effectiveSelector);

        // Gate + context construction + lifecycle execution via shared helper (issue #2890).
        // ExpectedInitialStatus is Pending (the default) — this item already exists as Pending;
        // the lifecycle race-guard must see Pending after K8s Job creation. Do NOT pass Dispatched
        // here (that override applies to DispatchWorkItem which creates items directly as Dispatched).
        // onDispatchFailure is null — the item started as Pending, so SafelyCancelOrphanedDispatchedWorkItemAsync
        // must NOT be called (no orphaned Dispatched row exists on this path).
        // The PvcPoolExhaustions counter is emitted after the call (below) rather than inside the helper —
        // it belongs exclusively to this path and must fire only on the 503/PVC-gate result.
        var dispatchResult = await DispatchResolvedWorkItemAsync(
            new ResolvedDispatchRequest
            {
                Db = db,
                Template = template,
                Projection = projection,
                NormalizedSelector = effectiveSelector,
                SanitizedSelector = sanitizedEffectiveSelector,
                ConcurrencyBySelector = concurrencyBySelector,
                PvcResult = pvcResult,
                ExpectedInitialStatus = WorkItemStatus.Pending,
                LogPrefix = "pending-dispatch ",
                OnDispatchFailure = null,
                OnSuccess = _ => TypedResults.Ok(id),
                WorkItemId = id,
                CallerName = "DispatchPendingWorkItem"
            },
            lifecycle,
            ct);

        // ── Result interception: delegate to shared helper (issue #3235, #3260) ──
        // InterpretDispatchResult returns an explicit DispatchInterpretOutcome alongside the IResult.
        //   ConcurrencyLimitRewritten → 409 was rewritten to 200+DispatchPendingResponse
        //   PvcExhausted503           → 503 from PVC gate; caller emits PvcPoolExhaustions.Add(1)
        //   K8sError503               → 503 from K8s failure; return as-is
        //   PassThrough               → success; emit RecordDispatchAttempt("dispatched","none")
        var isKiroAgent = JobTemplateProviderType.IsKiro(template.ProviderType);
        var (interpretedResult, outcome) = InterpretDispatchResult(dispatchResult, pvcResult, isKiroAgent, rewriteConcurrencyLimitAsDeferred: true);

        // Branch exclusively on the explicit outcome discriminator (issue #3260).
        // No reference-equality or re-matched status-code checks remain here.
        return ApplyDispatchOutcomeSwitch(outcome, interpretedResult);
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
            DispatchInterpretOutcome.ConcurrencyLimitRewritten => interpretedResult,
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
    /// Acquires the per-selector advisory lock and performs the post-lock TOCTOU status re-read.
    /// Moved from <see cref="CodingAgent.Api.WorkItemDispatchEndpoints"/> (issue #3286).
    ///
    /// <para>
    /// Returns <c>(null, earlyReturn)</c> when the lock timed out (503) or the item is no longer
    /// <c>Pending</c> after lock acquisition (200/deferred). The caller must return
    /// <c>earlyReturn</c> immediately without proceeding to the dispatch lifecycle.
    /// </para>
    /// <para>
    /// Returns <c>(lockHandle, null)</c> when the item is <c>Pending</c> and the lock was acquired.
    /// The caller <b>must</b> <c>await using</c> the returned <paramref name="lockHandle"/> to hold
    /// the lock for the full dispatch lifecycle scope:
    /// <code>
    /// var (lockHandle, earlyReturn) = await TryEnterSelectorDispatchAsync(...);
    /// if (earlyReturn is not null) return earlyReturn;
    /// await using var _ = lockHandle!;
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="lockProvider">The distributed lock provider.</param>
    /// <param name="normalizedSelector">Normalized (not raw) selector of the item's template — the key the
    /// concurrency gate checks — used as the lock key.</param>
    /// <param name="db">Caller-owned open <see cref="PipelineDbContext"/> for the post-lock re-read.
    /// Must use <c>AsNoTracking()</c> internally — see Risk 6 in the issue analysis.</param>
    /// <param name="id">Work-item GUID to re-read after lock acquisition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>(lockHandle, null)</c> on success (item is Pending, lock held);
    /// <c>(null, earlyReturn)</c> on lock timeout (503) or post-lock non-Pending (200/deferred).
    /// </returns>
    internal static async Task<(IAsyncDisposable? lockHandle, IResult? earlyReturn)> TryEnterSelectorDispatchAsync(
        IDistributedLockProvider lockProvider,
        string normalizedSelector,
        PipelineDbContext db,
        Guid id,
        CancellationToken ct)
    {
        // TODO [WARNING]: acquiredLock is not wrapped in try/finally after assignment. If
        // FirstOrDefaultAsync throws (e.g. transient Npgsql.NpgsqlException), the acquired
        // advisory lock is never disposed, leaving it held until the PostgreSQL connection is
        // recycled. All subsequent callers for the same selector would then time out (60 s → 503).
        // Fix: wrap the post-lock query in a try/finally and call acquiredLock.DisposeAsync() in
        // the finally block on the exceptional path, or restructure using await using from the
        // point of assignment. See review finding: DotNetSpecialist @ line 737.
        IAsyncDisposable acquiredLock;
        try
        {
            acquiredLock = await lockProvider.AcquireAsync($"dispatch-selector:{normalizedSelector}", ct);
        }
        catch (TimeoutException)
        {
            // PostgresDistributedLockProvider retries with pg_try_advisory_lock for up to 60s.
            // A timeout means another call has held the lock for that entire window (e.g. a slow
            // K8s API response). Return 503 — transient, the Scheduler should retry next cycle.
            Log.Warning( // NOSONAR S6667 — expected lock timeout; the message says so
                "DispatchPendingWorkItem: advisory lock acquisition timed out for selector {Selector} — returning 503",
                LogSanitizer.SanitizeForLog(normalizedSelector));
            WorkDistributionTelemetry.RecordDispatchAttempt("transient", "lock_timeout");
            return (null, TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }

        // Post-lock status re-read: close the TOCTOU window.
        // Another dispatch path (WorkItemDispatchLoop, DispatchWorkItem, or a concurrent call
        // to this endpoint) may have transitioned the item from Pending → Dispatched between
        // the fast-path check and lock acquisition. The advisory lock serialises concurrent
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
            await acquiredLock.DisposeAsync();
            return (null, TypedResults.Ok(new DispatchPendingResponse(false, NotPendingReason)));
        }

        return (acquiredLock, null);
    }

    /// <summary>
    /// Interprets the raw <see cref="IResult"/> returned by
    /// <see cref="DispatchResolvedWorkItemAsync"/> and applies path-specific result rewriting
    /// and telemetry (issue #3235, moved from <see cref="CodingAgent.Api.WorkItemDispatchEndpoints"/>
    /// by issue #3286).
    ///
    /// <para>
    /// Shared by <see cref="DispatchPendingWorkItemAsync"/> (with
    /// <paramref name="rewriteConcurrencyLimitAsDeferred"/>=<c>true</c>) and
    /// <c>WorkItemDispatchEndpoints.DispatchWorkItem</c> (with
    /// <paramref name="rewriteConcurrencyLimitAsDeferred"/>=<c>false</c>).
    /// </para>
    ///
    /// <para>
    /// <strong>Telemetry boundary — NOT emitted inside this helper:</strong>
    /// <list type="bullet">
    ///   <item><c>WorkDistributionTelemetry.PvcPoolExhaustions.Add(1)</c> — belongs exclusively
    ///     to the <see cref="DispatchPendingWorkItemAsync"/> path; the caller emits it after
    ///     checking the returned result.</item>
    ///   <item><c>WorkDistributionTelemetry.RecordDispatchAttempt("dispatched","none")</c> — belongs
    ///     exclusively to the <see cref="DispatchPendingWorkItemAsync"/> success fall-through; the
    ///     caller emits it when the returned result is not a 409/503.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <strong>503 disambiguation note:</strong> <paramref name="pvcResult"/> was captured before
    /// the advisory lock was acquired and before <c>DispatchResolvedWorkItemAsync</c> ran. The
    /// heuristic relies on gate-ordering (PVC gate before K8s lifecycle) being a structural
    /// invariant — see the TODO [WARNING] in the original result-interception block.
    /// </para>
    /// </summary>
    /// <param name="rawResult">The result returned by <c>DispatchResolvedWorkItemAsync</c>.</param>
    /// <param name="pvcResult">PVC availability snapshot taken before dispatch. Used for 503 disambiguation.</param>
    /// <param name="isKiroAgent"><c>true</c> when the resolved template targets a kiro provider.</param>
    /// <param name="rewriteConcurrencyLimitAsDeferred">
    /// <c>true</c> for <see cref="DispatchPendingWorkItemAsync"/>: a 409 result is rewritten to
    /// <c>200 DispatchPendingResponse(false,"concurrency_limit")</c>.
    /// <c>false</c> for <c>DispatchWorkItem</c>: 409 passes through unchanged.
    /// </param>
    /// <returns>
    /// A tuple of the (possibly rewritten) result and an explicit <see cref="DispatchInterpretOutcome"/>
    /// discriminator. The caller uses the outcome to branch without reference-equality or re-matched
    /// status-code checks (issue #3260).
    /// </returns>
    internal static (IResult Result, DispatchInterpretOutcome Outcome) InterpretDispatchResult(
        IResult rawResult,
        PvcAvailabilityResult pvcResult,
        bool isKiroAgent,
        bool rewriteConcurrencyLimitAsDeferred)
    {
        // ── 409: concurrency limit ────────────────────────────────────────────
        // Use IStatusCodeHttpResult (the interface) rather than StatusCodeHttpResult (concrete class)
        // because ApplyGates returns TypedResults.Conflict(...) which is Conflict<string>,
        // NOT StatusCodeHttpResult. Both implement IStatusCodeHttpResult.
        if (rawResult is Microsoft.AspNetCore.Http.IStatusCodeHttpResult { StatusCode: 409 })
        {
            if (rewriteConcurrencyLimitAsDeferred)
            {
                WorkDistributionTelemetry.RecordDispatchAttempt(DeferredResult, "concurrency_limit");
                return (TypedResults.Ok(new DispatchPendingResponse(false, "concurrency_limit")), DispatchInterpretOutcome.ConcurrencyLimitRewritten);
            }
            // DispatchWorkItem path: 409 passes through as-is.
            return (rawResult, DispatchInterpretOutcome.PassThrough);
        }

        // ── 503: PVC exhaustion or K8s failure ───────────────────────────────
        if (rawResult is Microsoft.AspNetCore.Http.IStatusCodeHttpResult { StatusCode: 503 })
        {
            // Gate ordering: ApplyGates runs the PVC gate before the K8s lifecycle.
            // The PVC gate fires if AvailablePvcs is empty — same condition PvcPoolExhaustions
            // has always checked. Use PVC availability to distinguish the two sub-cases.
            // TODO [WARNING]: pvcResult was captured before the advisory lock was acquired and
            // before DispatchResolvedWorkItemAsync ran. If a PVC becomes available between
            // snapshot and execution, pvcResult.AvailablePvcs may not reflect the state at the
            // time of the 503. The disambiguation relies on the gate-ordering guarantee (PVC gate
            // before K8s lifecycle) being a structural invariant — if that ordering ever changes,
            // this heuristic may misclassify k8s_error as pvc_unavailable or vice versa.
            if (!pvcResult.AvailablePvcs.Any() && isKiroAgent)
            {
                WorkDistributionTelemetry.RecordDispatchAttempt("transient", "pvc_unavailable");
                return (rawResult, DispatchInterpretOutcome.PvcExhausted503);
            }

            WorkDistributionTelemetry.RecordDispatchAttempt("transient", "k8s_error");
            return (rawResult, DispatchInterpretOutcome.K8sError503);
        }

        // ── Success or any other result ───────────────────────────────────────
        // Pass through unchanged. The DispatchPendingWorkItemAsync caller emits
        // RecordDispatchAttempt("dispatched","none") and wraps in DispatchPendingResponse(true,"none").
        return (rawResult, DispatchInterpretOutcome.PassThrough);
    }
}

/// <summary>
/// The fields <c>DispatchPendingWorkItem</c>'s fast-path query reads before taking the selector lock
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
/// Per-call inputs of <see cref="DispatchWorkItemService.DispatchResolvedWorkItemAsync"/>. Each dispatch path
/// (<c>DispatchPendingWorkItem</c>, <c>DispatchWorkItem</c>) builds one from its own sources.
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

    /// <summary>Normalized agent-selector key for the concurrency gate and context.</summary>
    public required string NormalizedSelector { get; init; }

    /// <summary>CRLF-stripped form of the selector for log messages and response bodies.</summary>
    public required string SanitizedSelector { get; init; }

    /// <summary>
    /// Snapshot produced by <see cref="DispatchWorkItemService.BuildConcurrencySnapshotAsync"/> in the caller.
    /// </summary>
    // TODO [WARNING]: This property should be typed as IReadOnlyDictionary<string, int> to make the
    // no-mutation contract explicit. The caller's snapshot must remain stable for the lifetime of the
    // dispatch call; FinalizeDispatchAsync mutates a copy inside DispatchLifecycleContext, not this value
    // directly — but the contract is implicit. Changing to IReadOnlyDictionary would catch future
    // regressions at compile time and surface the intent clearly.
    // On the DispatchWorkItem path, the early-gate check (earlyGateResult in the handler) uses the same
    // snapshot; if the lifecycle ever mutated this dictionary, the double-gate assumption would break
    // silently. (DotNetSpecialist review [WARNING])
    public required Dictionary<string, int> ConcurrencyBySelector { get; init; }

    /// <summary>
    /// PVC availability snapshot produced by the caller via
    /// <see cref="DispatchLifecycleService.QueryAvailablePvcsAsync"/>.
    /// </summary>
    public required PvcAvailabilityResult PvcResult { get; init; }

    /// <summary>
    /// <see cref="WorkItemStatus.Pending"/> for <c>DispatchPendingWorkItem</c>;
    /// <see cref="WorkItemStatus.Dispatched"/> for <c>DispatchWorkItem</c>.
    /// Controls <see cref="DispatchLifecycleContext.ExpectedInitialStatus"/> used by the
    /// race-condition guard inside <c>HandleOrphanedJobIfRaceDetectedAsync</c>.
    /// </summary>
    public required WorkItemStatus ExpectedInitialStatus { get; init; }

    /// <summary>
    /// Prefix embedded in K8s Job names and log messages:
    /// <c>"pending-dispatch "</c> for <c>DispatchPendingWorkItem</c>;
    /// <c>"sync-dispatch "</c> for <c>DispatchWorkItem</c>.
    /// </summary>
    public required string LogPrefix { get; init; }

    /// <summary>
    /// Called with <c>(workItemId, reason)</c> on the 503 path.
    /// <c>null</c> for <c>DispatchPendingWorkItem</c> (item started as Pending — no orphaned
    /// Dispatched row exists). <c>SafelyCancelOrphanedDispatchedWorkItemAsync</c> for
    /// <c>DispatchWorkItem</c>.
    /// </summary>
    public Func<Guid, string, Task>? OnDispatchFailure { get; init; }

    /// <summary>Factory producing the 200 <see cref="IResult"/> when dispatch succeeds.</summary>
    public required Func<Guid, IResult> OnSuccess { get; init; }

    /// <summary>Work-item GUID for log messages and the success result.</summary>
    public required Guid WorkItemId { get; init; }

    /// <summary>Short name for log messages (e.g. <c>"DispatchWorkItem"</c>).</summary>
    public required string CallerName { get; init; }
}
