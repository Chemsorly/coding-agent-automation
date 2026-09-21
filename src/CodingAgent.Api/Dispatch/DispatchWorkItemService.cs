using System.Diagnostics;
using System.Text.Json;
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
    private readonly JobTemplateStore _templateStore;

    public DispatchWorkItemService(JobTemplateStore templateStore)
    {
        ArgumentNullException.ThrowIfNull(templateStore);
        _templateStore = templateStore;
    }

    // ── Concurrency snapshot ─────────────────────────────────────────────────

    /// <summary>
    /// Builds the Dispatched||Running concurrency map keyed on
    /// <see cref="JobTemplateStore.NormalizeLabels"/> of each active item's AgentSelector.
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
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Dictionary mapping normalized AgentSelector → active-item count.
    /// Empty dictionary when no active items exist.
    /// </returns>
    internal async Task<Dictionary<string, int>> BuildConcurrencySnapshotAsync(
        PipelineDbContext db,
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
            var normalizedKey = JobTemplateStore.NormalizeLabels(item.Selector);
            result[normalizedKey] = result.GetValueOrDefault(normalizedKey, 0) + item.Count;
        }
        return result;
    }

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
    internal IResult? ApplyGates(
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
}
