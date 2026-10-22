using System.Text.RegularExpressions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure.Persistence.Services;

/// <summary>
/// Static helpers that record <c>pipeline.run.outcomes</c> and <c>pipeline.run.duration</c>
/// for a terminal WorkItem transition on any completion path (HTTP or hub).
/// Extracted from <c>WorkItemStatusTransitionService</c> so the hub completion path
/// (<see cref="WorkItemFallbackTransitionService"/>) can also record outcomes without
/// taking a dependency on <c>CodingAgent.Api</c>.
/// </summary>
public static partial class RunOutcomeTelemetry
{
    // S1192: shared literal for the fallback run_type / project_name tag value.
    // "unknown" appears in ResolveRunContextAsync (early-exit returns + fallback assignment)
    // and in RecordRunOutcomeMetrics (project_name fallback).
    public const string UnknownTag = "unknown";
    public const string RunTypeTag = "run_type";

    /// <summary>
    /// Queries the DB for the WorkItem's dispatch/completion timestamps, run type, project context, and the tool and
    /// model of its pinned route (spec 053 req 9.1; <c>unknown</c> for work items created before spec 053).
    /// Returns <c>(null, "unknown", null, null, "unknown", "unknown")</c> immediately when <paramref name="dbFactory"/> is null.
    /// </summary>
    public static async Task<(TimeSpan? Duration, string RunTypeTag, string? ProjectId, string? ProjectName, string ProviderTag, string ModelTag)>
        ResolveRunContextAsync(Guid id, IDbContextFactory<PipelineDbContext>? dbFactory, CancellationToken ct)
    {
        if (dbFactory is null)
            return (null, UnknownTag, null, null, UnknownTag, UnknownTag);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // LEFT JOIN WorkItems → PipelineRuns to get run type and project context.
        // PipelineRunEntity.WorkItemId is nullable — the join may yield null.
        // ProjectId and ProjectName come from PipelineRunEntity (string?) when the row exists;
        // otherwise from WorkItemEntity.ProjectId and the Projects table (below).
        // TODO: [WARNING] If a WorkItem has multiple PipelineRunEntity rows (e.g. a retry scenario
        // where the agent creates a second PipelineRun for the same WorkItem), this query returns
        // an arbitrary row because there is no ORDER BY. The resolved run_type and project context
        // may come from an earlier run, not the most recent one. Fix by adding
        // .OrderByDescending(pr => pr.StartedAt) before FirstOrDefaultAsync to consistently pick
        // the most recent run. Low risk today since retries typically reuse the same RunType,
        // but could diverge for future retry-chain scenarios.
        var row = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == id)
            .GroupJoin(
                db.PipelineRuns.AsNoTracking(),
                w => w.Id,
                pr => pr.WorkItemId,
                (w, runs) => new { w, runs })
            .SelectMany(
                x => x.runs.DefaultIfEmpty(),
                (x, pr) => new
                {
                    x.w.DispatchedAt,
                    x.w.CompletedAt,
                    x.w.TaskType,
                    x.w.Payload,
                    WorkItemProjectId = x.w.ProjectId,
                    RunType = pr != null ? pr.RunType : (PipelineRunType?)null,
                    ProjectId = pr != null ? pr.ProjectId : null,
                    ProjectName = pr != null ? pr.ProjectName : null
                })
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return (null, UnknownTag, null, null, UnknownTag, UnknownTag);

        // The PipelineRuns row is often not written yet when the terminal status arrives, so fall back
        // to the project the WorkItem was dispatched for; without it most runs are tagged "unknown".
        var projectId = row.ProjectId ?? row.WorkItemProjectId?.ToString();
        var projectName = row.ProjectName;
        if (projectName is null && row.WorkItemProjectId is { } workItemProjectId)
        {
            projectName = await db.Projects.AsNoTracking()
                .Where(p => p.Id == workItemProjectId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(ct);
        }

        // TODO: [WARNING] This reads row.CompletedAt from the DB. CompletedAt is set by
        // the mutation action before the DB commit, so the DB read will always see the committed
        // value under PostgreSQL's default READ COMMITTED isolation. This assumption holds for the
        // current architecture. If the isolation level changes or telemetry runs before the commit,
        // this read may return null and the duration will be omitted silently.
        TimeSpan? duration = (row.DispatchedAt is not null && row.CompletedAt is not null)
            ? row.CompletedAt.Value - row.DispatchedAt.Value
            : null;

        // Safe fallback: use ToDefaultRunTypeOrNull() which returns null for unknown values
        // rather than ToDefaultRunType() which throws UnreachableException.
        var resolvedRunType = row.RunType ?? row.TaskType.ToDefaultRunTypeOrNull();
        var runTypeTag = resolvedRunType.HasValue
            ? resolvedRunType.Value.ToString().ToLowerInvariant()
            : UnknownTag;

        var (providerTag, modelTag) = RouteTags(row.Payload);
        return (duration, runTypeTag, projectId, projectName, providerTag, modelTag);
    }

    /// <summary>
    /// The <c>provider</c> and <c>model</c> tags from the route pinned in the work item's payload: the job template's tool
    /// (the closed provider set) and the route's model, which the admin sets, so its cardinality is bounded. The model the
    /// agent reports is not used: <c>auto</c> stays <c>auto</c>.
    /// </summary>
    private static (string Provider, string Model) RouteTags(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return (UnknownTag, UnknownTag);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("route", out var route)
                || route.ValueKind != System.Text.Json.JsonValueKind.Object)
                return (UnknownTag, UnknownTag);
            var tool = route.TryGetProperty("tool", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
            var model = route.TryGetProperty("model", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString() : null;
            return (PipelineTelemetry.NormalizeRunProvider(tool), string.IsNullOrWhiteSpace(model) ? UnknownTag : model);
        }
        catch (System.Text.Json.JsonException)
        {
            return (UnknownTag, UnknownTag);
        }
    }

    /// <summary>
    /// Derives the outcome label and failure_reason tag from the completion status and payload.
    /// Ordering is critical: AnalysisRecommendation checks come before status/failure-reason checks
    /// because wont_do and needs_refinement both carry FailureReason.GateRejected in the request —
    /// the outcome must be identified first so failure_reason can be forced to "none" for those paths.
    /// When called from the hub path, <paramref name="payload"/> is always <c>null</c>; only
    /// <c>succeeded</c>, <c>failed</c>, <c>timeout</c>, and <c>cancelled</c> are then possible.
    /// </summary>
    public static (string Outcome, string FailureReasonTag) DeriveOutcome(
        WorkItemStatus status,
        JobCompletionPayload? payload,
        FailureReason? failureReason)
    {
        // 1. Cancelled — no payload content relevant.
        if (status == WorkItemStatus.Cancelled)
            return ("cancelled", "none");

        // 2. ConflictRestart — agent signals via FinalStep.
        if (payload?.FinalStep == PipelineStep.ConflictRestart)
            return ("conflict_restart", "none");

        // 3. WontDo gate — arrives as Succeeded + FailureReason.GateRejected; use AnalysisRecommendation.
        if (payload?.AnalysisRecommendation == AnalysisGateResult.WontDo)
            return ("wont_do", "none");

        // 4. NeedsRefinement gate — arrives as Failed + FailureReason.GateRejected; use AnalysisRecommendation.
        if (payload?.AnalysisRecommendation == AnalysisGateResult.NotReady)
            return ("needs_refinement", "none");

        // 5. PR created (non-draft).
        if (!string.IsNullOrEmpty(payload?.PullRequestUrl) && !payload.IsDraftPr)
            return ("pr_created", "none");

        // 6. Draft PR — requires a non-empty PullRequestUrl (mirrors priority 5) so a payload with
        // IsDraftPr=true but no URL (e.g. a pre-PR step that sets the flag prematurely) falls through
        // to 'succeeded'/'failed' instead of being mislabelled 'draft_pr'.
        if (!string.IsNullOrEmpty(payload?.PullRequestUrl) && payload.IsDraftPr)
            return ("draft_pr", "none");

        // 7. Timeout failure.
        if (failureReason == FailureReason.Timeout)
            return ("timeout", "timeout");

        // 8. Any other Succeeded run (review, decomposition, consolidation).
        if (status == WorkItemStatus.Succeeded)
            return ("succeeded", "none");

        // 9. Fallthrough — failed with a specific reason.
        var tag = failureReason.HasValue
            ? PascalToSnakeCaseTag(failureReason.Value.ToString())
            : "none";
        return ("failed", tag);
    }

    /// <summary>
    /// Records <c>pipeline.run.outcomes</c> (counter) and <c>pipeline.run.duration</c> (histogram)
    /// for a single terminal transition.
    /// </summary>
    /// <remarks>
    /// <strong>Tag set for pipeline.run.outcomes:</strong> <c>run_type</c>, <c>outcome</c>,
    /// <c>failure_reason</c>, <c>pipeline.project_name</c>, <c>provider</c> and <c>model</c> (spec 053 req 9.1).
    /// </remarks>
    // TODO: [WARNING] The three non-nullable string parameters (runTypeTag, outcome, failureReasonTag)
    // lack ArgumentNullException.ThrowIfNull guards. All current call sites produce non-null values,
    // so there is no runtime risk today, but the public API contract is incomplete. Add guards when
    // this method is next touched to fail fast with a clear diagnostic rather than silently writing
    // null tag values into the OTEL metric stream.
    public static void RecordRunOutcomeMetrics(
        string runTypeTag,
        string outcome,
        string failureReasonTag,
        string? projectName,
        TimeSpan? duration,
        string? providerTag = null,
        string? modelTag = null)
    {
        // 6-tag label set: run_type, outcome, failure_reason, pipeline.project_name, provider, model.
        PipelineTelemetry.RunOutcomes.Add(1,
            new KeyValuePair<string, object?>(RunTypeTag, runTypeTag),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("failure_reason", failureReasonTag),
            new KeyValuePair<string, object?>("pipeline.project_name", projectName ?? UnknownTag),
            new KeyValuePair<string, object?>("provider", providerTag ?? UnknownTag),
            new KeyValuePair<string, object?>("model", modelTag ?? UnknownTag));

        if (duration.HasValue && duration.Value.TotalSeconds >= 0)
        {
            PipelineTelemetry.RunDuration.Record(duration.Value.TotalSeconds,
                new KeyValuePair<string, object?>(RunTypeTag, runTypeTag),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <summary>
    /// Converts a PascalCase enum member name to snake_case lowercase.
    /// E.g. <c>QualityGateExhausted</c> → <c>"quality_gate_exhausted"</c>.
    /// </summary>
    private static string PascalToSnakeCaseTag(string pascalCase) =>
        PascalCaseBoundaryRegex().Replace(pascalCase, "_$1").ToLowerInvariant();

    [System.Text.RegularExpressions.GeneratedRegex("(?<=[a-z0-9])([A-Z])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PascalCaseBoundaryRegex();
}
