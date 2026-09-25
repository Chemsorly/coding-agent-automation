using System.Text.Json;
using System.Text.RegularExpressions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api;

/// <summary>
/// Outcome of a <see cref="WorkItemStatusTransitionService.TransitionAsync"/> call.
/// Mirrors the semantics of <see cref="TransitionResult"/> but is surfaced as a typed enum
/// that callers can map to <c>IResult</c> without taking a dependency on the persistence layer.
/// </summary>
public enum StatusTransitionOutcome
{
    /// <summary>A real state change was made and lifecycle events were dispatched.</summary>
    Transitioned,

    /// <summary>The item was already at the requested status; no change was made.</summary>
    AlreadyAtTarget,

    /// <summary>The work item does not exist.</summary>
    NotFound,

    /// <summary>The requested transition is not valid from the item's current state.</summary>
    Rejected,
}

/// <summary>
/// Encapsulates the compound status-transition orchestration that was previously inline in
/// <c>WorkItemAgentEndpoints.PostStatus</c>:
/// <list type="bullet">
///   <item>Infrastructure-recovery guard for <c>Running</c> requests (issue #2459).</item>
///   <item>Terminal-idempotency pre-read guard for incoming terminal statuses (issue #2461).</item>
///   <item><see cref="WorkItemTransitionService.TransitionDetailedAsync"/> call with entity mutation.</item>
///   <item>Lifecycle dispatch (<see cref="IRunLifecycleManager.FailRunAsync"/> /
///         <see cref="IRunLifecycleManager.CancelRunAsync"/>) gated on
///         <see cref="TransitionResult.Transitioned"/>.</item>
///   <item>Terminal-status telemetry emission (fire-and-forget, or awaited via test seam).</item>
/// </list>
/// Placed in <c>CodingAgent.Api</c> (not <c>CodingAgent.Orchestration</c>) because it depends on
/// the concrete <see cref="WorkItemTransitionService"/> from <c>CodingAgent.Infrastructure.Persistence</c>,
/// and the architecture boundary enforced by <c>Orchestration_ShouldNot_ReferenceInfrastructurePersistenceAssembly</c>
/// prohibits that dependency from the Orchestration assembly.
/// </summary>
public sealed partial class WorkItemStatusTransitionService
{
    private readonly WorkItemTransitionService _transitionService;
    private readonly IRunLifecycleManager _runLifecycleManager;
    private readonly IDbContextFactory<PipelineDbContext>? _dbFactory;

    public WorkItemStatusTransitionService(
        WorkItemTransitionService transitionService,
        IRunLifecycleManager runLifecycleManager,
        IDbContextFactory<PipelineDbContext>? dbFactory = null)
    {
        ArgumentNullException.ThrowIfNull(transitionService);
        ArgumentNullException.ThrowIfNull(runLifecycleManager);

        _transitionService = transitionService;
        _runLifecycleManager = runLifecycleManager;
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Performs the compound transition: infra-recovery guard → idempotency guard →
    /// <see cref="WorkItemTransitionService.TransitionDetailedAsync"/> → lifecycle dispatch → telemetry.
    /// </summary>
    /// <param name="id">Work item ID.</param>
    /// <param name="request">The incoming status request from the agent.</param>
    /// <param name="ct">Request-scoped cancellation token. Telemetry is launched with
    /// <see cref="CancellationToken.None"/> so it outlives the request.</param>
    /// <param name="awaitTelemetry">
    /// When <c>true</c>, the telemetry task is awaited before returning. Used by integration
    /// tests that assert metric side-effects synchronously. Production callers never pass this.
    /// </param>
    // Suppression: CA1068 — awaitTelemetry is a test-only seam appended after ct intentionally,
    // matching the pre-existing pattern in WorkItemAgentEndpoints.PostStatus.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068", Justification = "Test seam bool appended after ct intentionally")]
    public async Task<StatusTransitionOutcome> TransitionAsync(
        Guid id,
        WorkItemStatusRequest request,
        CancellationToken ct,
        bool awaitTelemetry = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        // TODO: [WARNING] id is not validated for Guid.Empty. Passing Guid.Empty flows through to
        // TryRecoverFromInfrastructureFailureAsync and then TransitionDetailedAsync, which will issue
        // a DB query returning null (not found) and return StatusTransitionOutcome.NotFound. This is the
        // correct observable outcome, but Guid.Empty reaching the persistence layer is a correctness
        // hazard if EF or the DB ever treats the all-zeros GUID specially (e.g. default value handling,
        // optimistic concurrency on a default GUID). Consider adding:
        //   if (id == Guid.Empty) return StatusTransitionOutcome.NotFound;
        // or throwing ArgumentException to fail fast at the public boundary.

        // ── Infrastructure-recovery guard (issue #2459) ───────────────────────────────────
        // When the agent reports Running, attempt to recover a Failed/Timeout or
        // Failed/InfrastructureFailure item back to Running before reaching
        // TransitionDetailedAsync (which would reject Failed→Running as invalid).
        if (request.Status == WorkItemStatus.Running)
        {
            var recovered = await _transitionService.TryRecoverFromInfrastructureFailureAsync(
                id, WorkItemStatus.Running, ct: ct);
            if (recovered)
                return StatusTransitionOutcome.Transitioned;
            // false → not a recoverable race; fall through to TransitionDetailedAsync.
        }

        // ── Pre-read terminal-idempotency guard (issue #2461) ─────────────────────────────
        // For incoming terminal statuses, read the current DB status and return AlreadyAtTarget
        // silently when the item is already at Cancelled or Succeeded.
        // This prevents the "Invalid transition" warning that TransitionCoreAsync would emit
        // for Cancelled→Failed, Succeeded→Failed, etc. The warning cannot be suppressed after
        // TransitionCoreAsync returns Rejected, so a pre-read is required.
        if (request.Status is WorkItemStatus.Failed or WorkItemStatus.Cancelled or WorkItemStatus.Succeeded)
        {
            var currentStatus = await _transitionService.GetCurrentStatusAsync(id, ct);
            if (currentStatus is WorkItemStatus.Cancelled or WorkItemStatus.Succeeded)
                return StatusTransitionOutcome.AlreadyAtTarget;
            // null → item not found; fall through so TransitionDetailedAsync returns NotFound.
            // Any non-terminal current status → fall through for normal processing.
        }

        // ── Core transition ───────────────────────────────────────────────────────────────
        var transitionResult = await _transitionService.TransitionDetailedAsync(
            id, request.Status,
            mutate: entity => ApplyStatusMutation(entity, request),
            ct: ct);

        if (transitionResult == TransitionResult.NotFound)
            return StatusTransitionOutcome.NotFound;

        if (transitionResult == TransitionResult.Rejected)
            return StatusTransitionOutcome.Rejected;

        // ── Lifecycle dispatch + telemetry (Transitioned only) ───────────────────────────
        if (transitionResult == TransitionResult.Transitioned)
        {
            if (request.Status == WorkItemStatus.Failed)
            {
                var failureReason = request.ErrorMessage ?? request.FailureReason ?? "Infrastructure failure";
                // TODO: [WARNING] FailRunAsync is always called with the hardcoded enum value
                // FailureReason.InfrastructureFailure regardless of what the agent reported in
                // request.FailureReason. ApplyStatusMutation persists the agent-supplied reason to the
                // DB entity (e.g. AgentError), but the lifecycle manager always sees InfrastructureFailure.
                // Any downstream logic in IRunLifecycleManager.FailRunAsync that branches on the
                // failure-reason parameter (label-swap rules, alerting, etc.) will misbehave for
                // non-infrastructure failures. This behaviour was present in the original PostStatus
                // inline code and is preserved here unchanged. Consider parsing request.FailureReason
                // with the same IsDefined guard used in ApplyStatusMutation and passing the result to
                // FailRunAsync instead of the hardcoded constant.
                await _runLifecycleManager.FailRunAsync(
                    new RunId(id.ToString()),
                    failureReason,
                    ct,
                    CodingAgent.Pipeline.Models.FailureReason.InfrastructureFailure);
            }
            else if (request.Status == WorkItemStatus.Cancelled)
            {
                await _runLifecycleManager.CancelRunAsync(new RunId(id.ToString()), ct);
            }

            if (request.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
            {
                // CancellationToken.None is intentional: this task outlives the HTTP request
                // lifetime. The request-scoped ct is cancelled when the response is sent,
                // which would cause spurious OperationCanceledException inside the task.
                var emitTask = EmitTerminalStatusTelemetryAsync(id, request, _dbFactory, CancellationToken.None);
                if (awaitTelemetry)
                    await emitTask;
                else
                    // TODO: [WARNING] The fire-and-forget discard (_ = emitTask) is safe as long as all
                    // awaits inside EmitTerminalStatusTelemetryAsync are covered by the outer try/catch.
                    // If EmitTerminalStatusTelemetryAsync is ever modified to add an await outside that
                    // try/catch block (e.g. for a new enrichment read), an exception on that path would
                    // produce an unobserved faulted Task and trigger TaskScheduler.UnobservedTaskException.
                    // The current implementation is safe, but reviewers should ensure any future changes
                    // to EmitTerminalStatusTelemetryAsync keep all awaits inside the try/catch.
                    _ = emitTask;
            }

            return StatusTransitionOutcome.Transitioned;
        }

        // AlreadyAtTarget — idempotent no-op
        return StatusTransitionOutcome.AlreadyAtTarget;
    }

    // ── Private helpers ───────────────────────────────────────────────────────────────────

    private static void ApplyStatusMutation(WorkItemEntity entity, WorkItemStatusRequest request)
    {
        if (request.AgentId is not null)
            entity.AssignedAgentId = request.AgentId;

        if (request.ErrorMessage is not null)
            entity.ErrorMessage = request.ErrorMessage;
        else if (request.Status == WorkItemStatus.Failed)
            entity.ErrorMessage = "Job failed without specific error information";

        if (request.Result is not null)
            entity.Result = request.Result;

        if (request.BranchName is not null)
            entity.BranchName = request.BranchName;

        if (request.Status == WorkItemStatus.Failed)
        {
            // Enum.TryParse succeeds for numeric string inputs (e.g. "99") even when they don't
            // correspond to a named FailureReason member. The IsDefined guard rejects such values
            // so only named members are persisted to entity.FailureReason. (Issue #2667)
            if (request.FailureReason is not null
                && Enum.TryParse<FailureReason>(request.FailureReason, ignoreCase: true, out var parsedReason)
                && Enum.IsDefined(typeof(FailureReason), parsedReason))
            {
                entity.FailureReason ??= parsedReason;
            }
            else
            {
                entity.FailureReason ??= FailureReason.AgentError;
            }
        }

        if (request.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
            entity.CompletedAt = DateTimeOffset.UtcNow;
    }

    private static async Task EmitTerminalStatusTelemetryAsync(
        Guid id,
        WorkItemStatusRequest request,
        IDbContextFactory<PipelineDbContext>? dbFactory,
        // TODO: [WARNING] The production call site fires this method as Task.Run with CancellationToken.None
        // (fire-and-forget). The ct parameter is only meaningful on the awaitTelemetry=true integration-test
        // path. If a test passes a cancellable token and cancels it mid-flight during a slow DB read, the
        // telemetry emission will be silently dropped and the test will see a missing metric without a clear
        // error. Consider documenting that ct should always be CancellationToken.None on the fire-and-forget
        // production path, or assert/log when ct can be cancelled to make the discrepancy observable.
        CancellationToken ct = default)
    {
        try
        {
            TimeSpan? duration = null;
            string runTypeTag = "unknown";
            string? projectId = null;
            string? projectName = null;

            if (dbFactory is not null)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);

                // LEFT JOIN WorkItems → PipelineRuns to get run type and project context.
                // PipelineRunEntity.WorkItemId is nullable — the join may yield null.
                // ProjectId and ProjectName come from PipelineRunEntity (string?), NOT from
                // WorkItemEntity.ProjectId (which is Guid? and lacks ProjectName).
                // TODO: [WARNING] If a WorkItem has multiple PipelineRunEntity rows (e.g. a retry scenario
                // where the agent creates a second PipelineRun for the same WorkItem), this query returns
                // an arbitrary row because there is no ORDER BY. The resolved run_type and project context
                // may come from an earlier run, not the most recent one. Fix by adding
                // .OrderByDescending(pr => pr.CreatedAt) (or StartedAt) inside the DefaultIfEmpty projection
                // before FirstOrDefaultAsync. Low risk today since retries typically reuse the same RunType,
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
                            RunType = pr != null ? pr.RunType : (PipelineRunType?)null,
                            ProjectId = pr != null ? pr.ProjectId : null,
                            ProjectName = pr != null ? pr.ProjectName : null
                        })
                    .FirstOrDefaultAsync(ct);

                if (row is not null)
                {
                    // TODO: [WARNING] This reads row.CompletedAt from the DB. CompletedAt is set by
                    // ApplyStatusMutation (synchronously, before the DB commit) and EmitTerminalStatusTelemetryAsync
                    // runs after that commit, so the DB read will always see the committed value under
                    // PostgreSQL's default READ COMMITTED isolation. This assumption holds for the current
                    // architecture. If the isolation level changes or the telemetry task is moved to run
                    // before the commit, this read may return null and the duration will be omitted silently.
                    if (row.DispatchedAt is not null && row.CompletedAt is not null)
                        duration = row.CompletedAt.Value - row.DispatchedAt.Value;

                    projectId = row.ProjectId;
                    projectName = row.ProjectName;

                    // Safe fallback: use a local switch with null default rather than
                    // ToDefaultRunType() which throws UnreachableException for unknown values.
                    var resolvedRunType = row.RunType ?? row.TaskType switch
                    {
                        WorkItemTaskType.Implementation => (PipelineRunType?)PipelineRunType.Implementation,
                        WorkItemTaskType.Review => PipelineRunType.Review,
                        WorkItemTaskType.Decomposition => PipelineRunType.DecompositionAnalysis,
                        WorkItemTaskType.Consolidation => PipelineRunType.Consolidation,
                        _ => null
                    };
                    runTypeTag = resolvedRunType.HasValue
                        ? resolvedRunType.Value.ToString().ToLowerInvariant()
                        : "unknown";
                }
            }

            // Prefer the typed FailureCategory from the payload; fall back to the string field.
            JobCompletionPayload? payload = null;
            if (request.Result is not null)
            {
                try
                {
                    payload = JsonSerializer.Deserialize<JobCompletionPayload>(
                        request.Result, PipelineJsonOptions.Default);
                }
                catch (JsonException)
                {
                    // TODO: [WARNING] JsonException is thrown when the JSON is syntactically invalid OR
                    // when a required property (e.g. FinalStep, which is [required] on JobCompletionPayload)
                    // is missing. A valid Result payload that omits FinalStep will reach this catch and be
                    // discarded entirely, causing DeriveOutcome to fall back to status-based derivation and
                    // potentially produce the wrong outcome (e.g. 'succeeded' instead of 'pr_created').
                    // Fix: use relaxed deserialization (remove [required] from FinalStep for telemetry,
                    // or deserialize with JsonIgnoreCondition.WhenWritingDefault) so partially-populated
                    // payloads still contribute their non-null fields to outcome derivation.
                    // Malformed payload — treat as absent; fall through to status-based derivation.
                }
            }

            // Build typed failureReason from FailureCategory (payload) first, then string field.
            FailureReason? failureReason = payload?.FailureCategory;
            if (!failureReason.HasValue
                && Enum.TryParse<FailureReason>(request.FailureReason, ignoreCase: true, out var parsedReason)
                && Enum.IsDefined(typeof(FailureReason), parsedReason))
            {
                failureReason = parsedReason;
            }

            var (outcome, failureReasonTag) = DeriveOutcome(request.Status, payload, failureReason);

            RecordRunOutcomeMetrics(runTypeTag, outcome, failureReasonTag, projectId, projectName, duration);

            WorkDistributionTelemetry.LogTerminalStatus(
                id, request.Status, duration, request.AgentId,
                failureReason);
        }
        catch (Exception ex)
        {
            Serilog.Log.ForContext("SourceContext", nameof(WorkItemStatusTransitionService))
                .Warning(ex, "Failed to emit terminal status telemetry for WorkItem {Id}", id);
        }
    }

    /// <summary>
    /// Derives the outcome label and failure_reason tag from the request and completion payload.
    /// Ordering is critical: AnalysisRecommendation checks come before status/failure-reason checks
    /// because wont_do and needs_refinement both carry FailureReason.GateRejected in the request —
    /// the outcome must be identified first so failure_reason can be forced to "none" for those paths.
    /// </summary>
    private static (string Outcome, string FailureReasonTag) DeriveOutcome(
        WorkItemStatus status,
        JobCompletionPayload? payload,
        FailureReason? failureReason)
    {
        // 1. Cancelled — no payload content relevant.
        if (status == WorkItemStatus.Cancelled)
            return ("cancelled", "none");

        // 2. ConflictRestart — agent signals via FinalStep.
        // TODO: [WARNING] Priority 2 (ConflictRestart) comes before priorities 3–4 (AnalysisRecommendation).
        // A payload with FinalStep=ConflictRestart AND AnalysisRecommendation=WontDo/NotReady would produce
        // outcome='conflict_restart' (not 'wont_do'/'needs_refinement'). This combination should never occur
        // in practice. If issue #2956 introduces new FinalStep values that overlap with analysis gate outcomes,
        // review this ordering to ensure the intended outcome is produced. The pre-initialization does not
        // include a (run_type, "conflict_restart", non-"none" failure_reason) series; an invalid combo would
        // create an uninitialized series and partially defeat the pre-init goal.
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

        // 6. Draft PR.
        // TODO: [WARNING] This check does not verify that PullRequestUrl is non-empty before
        // emitting 'draft_pr'. A payload where IsDraftPr=true but PullRequestUrl is null/empty
        // (e.g. a pre-PR step that sets IsDraftPr=true by accident) will produce outcome='draft_pr'
        // rather than falling through to 'succeeded' or 'failed'. Fix: guard with
        // !string.IsNullOrEmpty(payload?.PullRequestUrl), mirroring priority 5.
        if (payload?.IsDraftPr == true)
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
    /// Records pipeline.run.outcomes (counter) and pipeline.run.duration (histogram) for a
    /// single terminal transition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Tag set for pipeline.run.outcomes:</strong> <c>run_type</c>, <c>outcome</c>,
    /// <c>failure_reason</c>, and <c>pipeline.project_name</c> (Requirement 1).
    /// The pre-initialization in <c>Program.PreInitializeMetrics</c> emits only the 3-tag combination
    /// (without <c>pipeline.project_name</c>) per Requirement 7, which says to leave
    /// <c>pipeline.project_name</c> out of pre-initialization because it has unbounded cardinality.
    /// This means pre-initialized series (3 tags) and live series (4 tags) have different Prometheus
    /// label fingerprints; the first real event for a new project name will still show as 0 in
    /// <c>increase()</c> until a second event arrives, but the pre-init goal is met for the closed
    /// tag dimensions (<c>run_type</c>, <c>outcome</c>, <c>failure_reason</c>).
    /// </para>
    /// </remarks>
    private static void RecordRunOutcomeMetrics(
        string runTypeTag,
        string outcome,
        string failureReasonTag,
        string? projectId,
        string? projectName,
        TimeSpan? duration)
    {
        // 4-tag label set per Requirement 1: run_type, outcome, failure_reason, pipeline.project_name.
        // pipeline.project_name is excluded from pre-initialization (unbounded cardinality) per Req 7.
        PipelineTelemetry.RunOutcomes.Add(1,
            new KeyValuePair<string, object?>("run_type", runTypeTag),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("failure_reason", failureReasonTag),
            new KeyValuePair<string, object?>("pipeline.project_name", projectName ?? "unknown"));

        if (duration.HasValue && duration.Value.TotalSeconds >= 0)
        {
            PipelineTelemetry.RunDuration.Record(duration.Value.TotalSeconds,
                new KeyValuePair<string, object?>("run_type", runTypeTag),
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
