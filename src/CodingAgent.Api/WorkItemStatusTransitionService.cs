using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
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
public sealed class WorkItemStatusTransitionService
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
        CancellationToken ct = default)
    {
        try
        {
            TimeSpan? duration = null;
            if (dbFactory is not null)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var item = await db.WorkItems.AsNoTracking()
                    .Where(w => w.Id == id)
                    .Select(w => new { w.DispatchedAt, w.CompletedAt })
                    .FirstOrDefaultAsync(ct);
                if (item?.DispatchedAt is not null && item.CompletedAt is not null)
                    duration = item.CompletedAt.Value - item.DispatchedAt.Value;
            }

            // Enum.TryParse succeeds for numeric string inputs (e.g. "99") even when they don't
            // correspond to a named FailureReason member, yielding an undefined enum instance that
            // would become a high-cardinality metric tag. The IsDefined guard rejects such values
            // so only named members reach the telemetry dimension. (Issue #2341)
            FailureReason? failureReason = Enum.TryParse<FailureReason>(request.FailureReason, ignoreCase: true, out var parsedReason)
                && Enum.IsDefined(typeof(FailureReason), parsedReason)
                ? parsedReason
                : (FailureReason?)null;

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
}
