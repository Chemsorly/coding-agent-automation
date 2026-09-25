using System.Text.Json;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.EntityFrameworkCore;
using ILogger = Serilog.ILogger;

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
    private readonly ILogger _logger;

    public WorkItemStatusTransitionService(
        WorkItemTransitionService transitionService,
        IRunLifecycleManager runLifecycleManager,
        IDbContextFactory<PipelineDbContext>? dbFactory = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transitionService);
        ArgumentNullException.ThrowIfNull(runLifecycleManager);

        _transitionService = transitionService;
        _runLifecycleManager = runLifecycleManager;
        _dbFactory = dbFactory;
        // TODO: [WARNING] Serilog.Log.Logger is the global static logger. If this constructor runs before
        // Serilog's bootstrap configuration is applied (e.g., in test setup without a configured static
        // logger), Log.Logger resolves to SilentLogger and ForContext<>() returns a no-op logger.
        // ResolveFailedFinalLabel's diagnostic log lines (disallowed-label Information and JsonException
        // catch) would emit nothing, making label-parsing issues invisible at debug time. This is a
        // pre-existing pattern in this codebase; the ILogger? optional parameter makes the silent-logger
        // path the default in integration tests that don't inject a logger. Consider requiring a non-null
        // ILogger in the constructor or using a NullLogger fallback that is explicitly documented.
        // See review finding [WARNING] (DotNetSpecialist).
        _logger = logger ?? Serilog.Log.Logger.ForContext<WorkItemStatusTransitionService>();
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

                // Parse the pipeline's intended terminal label from the HTTP payload.
                // The agent serializes the full JobCompletionPayload into request.Result; we read
                // FinalLabel from it so the HTTP path can honour agent:needs-refinement outcomes
                // without depending on the SignalR ReportJobCompleted path (which is replica-dependent).
                // Allowlist: only agent:needs-refinement is accepted. Any success label, re-queue label,
                // active-state label, or unknown value falls back to agent:error (null resolvedFinalLabel).
                var resolvedFinalLabel = ResolveFailedFinalLabel(request.Result);

                await _runLifecycleManager.FailRunWithLabelAsync(
                    new RunId(id.ToString()),
                    failureReason,
                    resolvedFinalLabel,
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

    /// <summary>
    /// Parses <paramref name="resultJson"/> (the serialized <see cref="JobCompletionPayload"/>
    /// from the agent's HTTP POST body) and returns <see cref="AgentLabels.NeedsRefinement"/> when
    /// the payload's <c>FinalLabel</c> is exactly that value.
    ///
    /// Returns <c>null</c> for every other case:
    /// <list type="bullet">
    ///   <item><paramref name="resultJson"/> is null or empty</item>
    ///   <item>JSON is malformed or does not contain <c>FinalLabel</c></item>
    ///   <item><c>FinalLabel</c> is any other label — including success labels
    ///   (<c>agent:done</c>, <c>agent:next</c>, <c>agent:epic-review</c>, <c>agent:wont-do</c>),
    ///   re-queue labels (<c>agent:cancelled</c>), active-state labels, or unknown values</item>
    /// </list>
    ///
    /// The narrow allowlist prevents a <c>Failed</c> HTTP transition from ending with a success or
    /// re-queue label, which would bypass quality gates and confuse <see cref="IssueReworkService"/>.
    /// </summary>
    private string? ResolveFailedFinalLabel(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
            return null;

        try
        {
            // TODO: [WARNING] PipelineJsonOptions.Lenient uses PropertyNameCaseInsensitive=true, which
            // handles camelCase/PascalCase mismatches between agent serialization and server-side C# property
            // names. Verify that FinalLabel is serialized by the agent with a casing that Lenient can match;
            // a mismatch would silently return a null FinalLabel and fall through to agent:error without any
            // log entry, making the failure indistinguishable from the intentional null-result branch.
            // The existing tests serialize via PipelineJsonOptions.Default (PascalCase) — that roundtrip is
            // covered, but camelCase serialization from the agent is not explicitly tested here.
            // See review finding [WARNING] (SecurityReviewer, DotNetSpecialist).
            var payload = JsonSerializer.Deserialize<JobCompletionPayload>(resultJson, PipelineJsonOptions.Lenient);
            var finalLabel = payload?.FinalLabel;

            if (finalLabel is null)
                return null;

            if (finalLabel == AgentLabels.NeedsRefinement)
                return AgentLabels.NeedsRefinement;

            // Non-null but not in the allowlist — log at Information so operators can diagnose
            // unexpected values without flooding the warning channel.
            // TODO: [WARNING] The raw finalLabel value from the agent-controlled HTTP payload is written
            // directly into a Serilog structured log message. Serilog captures it as a structured property
            // (mitigating classic format-string injection), but the raw string still flows into all
            // configured sinks (file, Loki, etc.). A crafted FinalLabel containing newlines, ANSI escape
            // sequences, or very long strings could pollute log output or cause log-storage issues.
            // Consider truncating to a safe maximum length (e.g., 128 chars) and stripping control
            // characters before logging. See review finding [WARNING] (SecurityReviewer).
            _logger.Information(
                "WorkItemStatusTransitionService: ignoring FinalLabel={FinalLabel} from Failed HTTP payload — only agent:needs-refinement is accepted; falling back to agent:error",
                finalLabel);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.Information(ex,
                "WorkItemStatusTransitionService: could not parse FinalLabel from Failed HTTP payload (malformed JSON) — falling back to agent:error");
            return null;
        }
    }

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
