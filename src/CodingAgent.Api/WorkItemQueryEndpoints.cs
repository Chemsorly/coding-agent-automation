using System.Text.Json;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api;

/// <summary>
/// Minimal API endpoints for read-only Work Item query and metrics routes:
/// pending, active, retry-count, staleness, counts-by-status, and status.
/// </summary>
public static class WorkItemQueryEndpoints
{
    /// <summary>
    /// Maps read-only work item query endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapWorkItemQueryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work-items")
            .RequireAuthorization(ApiAuthPolicies.Agent);

        group.MapGet("/pending", GetPendingWorkItems).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/active", GetActiveWorkItems).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/{id:guid}/retry-count", GetRetryCount).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/staleness", GetStaleness).RequireAuthorization(ApiAuthPolicies.Operator);

        // ── Metrics feed for the Scheduler's WorkItemCountsPoller ─────────────
        group.MapGet("/counts-by-status", GetCountsByStatus).RequireAuthorization(ApiAuthPolicies.Operator);
        group.MapGet("/{id:guid}/status", GetWorkItemStatus).RequireAuthorization(ApiAuthPolicies.Operator);
    }

    // ── GET /pending ──────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/pending
    /// Returns Pending work items for all task types (including Consolidation), ordered by
    /// PriorityWeight DESC, CreatedAt ASC.
    /// Query param: maxResults (default 50).
    /// Includes display fields (IssueTitle, InitiatedBy, ProjectName, ProjectId) extracted from
    /// the Payload JSONB column for the Agent Monitoring Job Queue UI.
    /// </summary>
    // TODO: [WARNING] IConfiguration was removed from this method's parameter list as part of #2566
    // (the flag read was deleted). The MapGet registration uses a method group reference
    // (GetPendingWorkItems), so ASP.NET Core's minimal API binder resolves parameters by type from
    // DI/route/query — no IConfiguration argument is passed explicitly and none will leak. Confirmed
    // correct: the registration site does not pass IConfiguration positionally. If IConfiguration
    // is ever re-added here, verify the registration site matches.
    internal static async Task<IResult> GetPendingWorkItems(
        IDbContextFactory<PipelineDbContext> dbFactory,
        int maxResults = 50,
        string? projectId = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Phase 1: SQL projection — include Payload and ProjectId alongside the 7 scalar fields.
        // Payload is fetched here so we can extract display fields in-memory (Phase 2).
        var pending = db.WorkItems
            .AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Pending);

        // Optional project scope. WorkItem.ProjectId is a uuid column while the switcher passes the
        // project's id as a string (PipelineProject.Id is a Guid-string), so parse before comparing.
        if (!string.IsNullOrEmpty(projectId) && Guid.TryParse(projectId, out var scopeProjectId))
            pending = pending.Where(w => w.ProjectId == scopeProjectId);

        var raw = await pending
            .OrderByDescending(w => w.PriorityWeight)
            .ThenBy(w => w.CreatedAt)
            .Take(maxResults)
            .Select(w => new
            {
                w.Id,
                w.IssueIdentifier,
                w.IssueProviderConfigId,
                w.TaskType,
                w.CreatedAt,
                w.AgentSelector,
                w.RetryCount,
                w.Payload,
                w.ProjectId,
                w.TimeoutSeconds,
                w.PriorityWeight,
                w.TraceParent
            })
            .ToListAsync(ct);

        // Phase 2: in-memory deserialization to extract display fields from Payload.
        // Uses PipelineJsonOptions.Lenient (PropertyNameCaseInsensitive=true) for robustness against
        // payloads written by older serializer configs or with PascalCase keys.
        // A malformed payload produces null display fields rather than a 500 — same defensive pattern
        // used in GetAssignment and PostLabelSwap.
        var items = raw.Select(w =>
        {
            JobDistributionRequest? req = null;
            if (w.Payload is not null)
            {
                try
                {
                    req = JsonSerializer.Deserialize<JobDistributionRequest>(w.Payload, PipelineJsonOptions.Lenient);
                }
                catch (JsonException)
                {
                    // Corrupt or legacy payload — fall back to null display fields for this row.
                }
            }
            return new PendingWorkItemDto
            {
                Id = w.Id,
                IssueIdentifier = w.IssueIdentifier,
                IssueProviderConfigId = w.IssueProviderConfigId,
                TaskType = w.TaskType,
                CreatedAt = w.CreatedAt,
                AgentSelector = w.AgentSelector,
                RetryCount = w.RetryCount,
                TimeoutSeconds = w.TimeoutSeconds,
                PriorityWeight = w.PriorityWeight,
                IssueTitle = req?.IssueDetail?.Title,
                InitiatedBy = req?.InitiatedBy,
                ProjectName = req?.ProjectName,
                ProjectId = w.ProjectId,
                TraceParent = w.TraceParent
            };
        }).ToList();

        return TypedResults.Ok((IReadOnlyList<PendingWorkItemDto>)items);
    }

    // ── GET /active ───────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/active?olderThanSeconds=N
    /// Returns WorkItems in Dispatched or Running status with DispatchedAt &lt; now - N seconds.
    /// Used by ReconciliationService for timeout enforcement and short-circuit Dispatched sweep.
    /// </summary>
    internal static async Task<IResult> GetActiveWorkItems(
        int olderThanSeconds,
        IDbContextFactory<PipelineDbContext> dbFactory,
        // TODO: The nullable optional DI service creates a silent-degradation pattern — if
        // IOrchestratorRunService is ever accidentally unregistered, the endpoint silently returns
        // no CurrentStep enrichment instead of failing at startup. Tests call this handler directly
        // with an explicit null argument so they do not rely on DI optional resolution; in
        // production the service is always registered as a singleton. Consider switching to a
        // non-nullable required [FromServices] parameter once the test callability story is clear.
        IOrchestratorRunService? runService = null,
        string? projectId = null,
        CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-olderThanSeconds);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var active = db.WorkItems
            .AsNoTracking()
            .Where(w => (w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
                     && (w.DispatchedAt < cutoff
                         // Fallback for items where DispatchedAt is null (e.g., claim write failed):
                         // use CreatedAt so they are not permanently invisible to timeout enforcement.
                         // 1C-001: NULL < cutoff evaluates to NULL (falsy) in SQL, excluding these rows.
                         // TODO [WARNING]: This fallback uses olderThanSeconds (TimeoutCanaryMinAgeSeconds=60s)
                         // as the CreatedAt cutoff, but the reconciliation loop's grace window is
                         // NullDispatchedAtGraceWindowSeconds (default 3600s). The query therefore
                         // over-fetches null-DispatchedAt items (those aged 60s–3600s are returned but
                         // immediately skipped by the loop's grace-window check), creating up to 60× more
                         // API traffic than necessary. The fallback cutoff should use
                         // NullDispatchedAtGraceWindowSeconds rather than olderThanSeconds, or the loop
                         // should explicitly document that it expects and discards within-grace items.
                         // (Correctness review [WARNING])
                         || (w.DispatchedAt == null && w.CreatedAt < cutoff)));
        // Optional project scope (not passed by reconciliation). ProjectId is a uuid column; parse the
        // switcher's Guid-string id before comparing.
        if (!string.IsNullOrEmpty(projectId) && Guid.TryParse(projectId, out var scopeProjectId))
            active = active.Where(w => w.ProjectId == scopeProjectId);

        var items = await active
            .Select(w => new
            {
                w.Id,
                w.Status,
                w.DispatchedAt,
                w.CreatedAt,
                w.AgentSelector,
                w.IssueIdentifier,
                w.K8sJobName,
                w.TimeoutSeconds,
                w.Payload
            })
            .ToListAsync(ct);

        // Phase 2: in-memory deserialization to extract IssueTitle and InitiatedBy from Payload.
        // Same defensive pattern as GetPendingWorkItems — a malformed or absent payload
        // produces null display fields rather than a 500.
        var dtos = items.Select(w =>
        {
            string? issueTitle = null;
            string? initiatedBy = null;
            if (w.Payload is not null)
            {
                try
                {
                    var req = JsonSerializer.Deserialize<JobDistributionRequest>(w.Payload, PipelineJsonOptions.Lenient);
                    issueTitle = req?.IssueDetail?.Title;
                    initiatedBy = req?.InitiatedBy;
                }
                catch (JsonException)
                {
                    // Corrupt or legacy payload — leave display fields null.
                }
            }
            return new ActiveWorkItemDto
            {
                Id = w.Id,
                Status = w.Status,
                DispatchedAt = w.DispatchedAt,
                CreatedAt = w.CreatedAt,
                AgentSelector = w.AgentSelector,
                IssueIdentifier = w.IssueIdentifier,
                K8sJobName = w.K8sJobName,
                TimeoutSeconds = w.TimeoutSeconds,
                IssueTitle = issueTitle,
                InitiatedBy = initiatedBy
            };
            // TODO [WARNING]: ct is available and used in the SQL phase (ToListAsync(ct)) but is not
            // propagated to this in-memory LINQ loop. Under normal payload sizes this is harmless because
            // the deserialization is synchronous and fast. If payload sizes grow significantly, consider
            // adding a cancellation check (ct.ThrowIfCancellationRequested()) inside the loop body.
        }).ToList();

        // Enrich with live pipeline step from the in-memory run service when available.
        // runService may be null in test scenarios that construct the handler directly without DI.
        if (runService is not null)
        {
            for (var i = 0; i < dtos.Count; i++)
            {
                // TODO: The explicit cast (RunId) calls ArgumentException.ThrowIfNullOrEmpty internally.
                // Guid.ToString() is always non-null/non-empty, so this is safe in practice, but if
                // ActiveWorkItemDto.Id ever becomes nullable (Guid?) the cast would throw instead of
                // skipping enrichment. Consider using new RunId(dtos[i].Id.ToString()) for clarity.
                var liveRun = runService.GetRun((RunId)dtos[i].Id.ToString());
                if (liveRun is not null)
                    dtos[i] = dtos[i] with { CurrentStep = liveRun.CurrentStep };
            }
        }

        return TypedResults.Ok((IReadOnlyList<ActiveWorkItemDto>)dtos);
    }

    // ── GET /{id}/retry-count ──────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/{id}/retry-count
    /// Returns { "retryCount": int }. 200 or 404.
    /// </summary>
    internal static async Task<IResult> GetRetryCount(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new { w.RetryCount })
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(new { retryCount = item.RetryCount });
    }

    // ── GET /staleness ─────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/staleness?issueIdentifier=...&amp;issueProviderConfigId=...&amp;since=...
    /// Returns WorkItemStalenessResult.
    /// </summary>
    internal static async Task<IResult> GetStaleness(
        string issueIdentifier,
        string issueProviderConfigId,
        DateTimeOffset since,
        WorkItemTransitionService transitionService,
        CancellationToken ct)
    {
        var issueId = new IssueIdentifier(issueIdentifier);
        var providerConfigId = new ProviderConfigId(issueProviderConfigId);

        var hasAgentError = await transitionService.HasAgentErrorSinceAsync(issueId, providerConfigId, since, ct);
        var lastSuccess = await transitionService.GetLastSuccessfulCompletionAsync(issueId, providerConfigId, ct);

        return TypedResults.Ok(new WorkItemStalenessResult
        {
            HasAgentErrorSince = hasAgentError,
            LastSuccessfulCompletion = lastSuccess
        });
    }

    // ── GET /api/work-items/counts-by-status ─────────────────────────────────

    /// <summary>
    /// Returns work item counts grouped by (Status, AgentSelector).
    /// Called by the Scheduler's WorkItemCountsPoller to feed Prometheus gauges.
    /// </summary>
    internal static async Task<IResult> GetCountsByStatus(
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var counts = await db.WorkItems
            .GroupBy(w => new { w.Status, w.AgentSelector })
            .Select(g => new
            {
                Status = g.Key.Status.ToString(),
                AgentSelector = g.Key.AgentSelector,
                Count = (long)g.Count()
            })
            .ToListAsync(ct);

        return Results.Ok(counts.Select(c =>
            new CodingAgent.Api.Client.WorkItemCountDto(c.Status, c.AgentSelector, c.Count))
            .ToArray());
    }

    // ── GET /{id}/status ──────────────────────────────────────────────────

    /// <summary>
    /// GET /api/work-items/{id}/status
    /// Returns the current <see cref="WorkItemStatus"/> of a WorkItem.
    /// Used by KubernetesWorkDistributor.GetJobStatusAsync to check run status.
    /// 200 with { status: string }, 404 if not found.
    /// </summary>
    internal static async Task<IResult> GetWorkItemStatus(
        Guid id,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var status = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => (WorkItemStatus?)w.Status)
            .FirstOrDefaultAsync(ct);

        if (status is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(new { status });
    }
}
