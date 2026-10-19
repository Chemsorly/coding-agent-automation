using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodingAgent.Infrastructure.Persistence.Services;

/// <summary>
/// Implements the shared fallback chain for work item status transitions:
/// <list type="number">
///   <item><description>Direct transition — sets CompletedAt, ErrorMessage, FailureReason.</description></item>
///   <item><description>Two-step via Running — Dispatched→Running→terminal, for any terminal status
///   (Succeeded, Cancelled, or Failed).</description></item>
///   <item><description>Race-induced failure recovery — bypasses terminal state for items stuck
///   in Failed with <c>FailureReason.InfrastructureFailure</c> (SignalR delivery timeout) or
///   <c>FailureReason.Timeout</c> (ReconciliationLoop timeout race where agent completed late).</description></item>
/// </list>
/// This service is a singleton — it contains no per-request state and wraps
/// <see cref="WorkItemTransitionService"/>, which is also singleton.
/// </summary>
public sealed class WorkItemFallbackTransitionService : IWorkItemFallbackTransitionService
{
    private readonly WorkItemTransitionService _workItemTransition;
    private readonly ILogger<WorkItemFallbackTransitionService> _logger;
    private readonly IDbContextFactory<PipelineDbContext>? _dbFactory;

    public WorkItemFallbackTransitionService(
        WorkItemTransitionService workItemTransition,
        ILogger<WorkItemFallbackTransitionService> logger,
        IDbContextFactory<PipelineDbContext>? dbFactory = null)
    {
        ArgumentNullException.ThrowIfNull(workItemTransition);
        ArgumentNullException.ThrowIfNull(logger);
        _workItemTransition = workItemTransition;
        _logger = logger;
        _dbFactory = dbFactory;
    }

    /// <inheritdoc />
    public async Task<bool> TryFallbackChainAsync(
        Guid workItemId, WorkItemStatus status,
        string? errorMessage, FailureReason? failureReason,
        CancellationToken ct)
    {
        // Early-exit: if the item is already in a terminal state that has no valid forward path
        // to the requested target, skip the chain entirely. All three fallback steps would be
        // rejected immediately, generating spurious "Invalid transition" Warning logs.
        //
        // IsIdempotentTransition covers:
        //   • Any terminal → same terminal: idempotent no-op (issue #2139 fix for Failed→Failed;
        //     Succeeded→Succeeded / Cancelled→Cancelled guards prevent duplicate telemetry when
        //     WorkItemTransitionService.TransitionAsync returns true for already-at-target calls).
        //   • Succeeded or Cancelled → any different terminal: no recovery path (3B-001 fix).
        //
        // NOT covered (falls through to the fallback chain):
        //   • Failed → Running/Succeeded/Cancelled: legitimate infra-failure and timeout-race
        //     recovery paths via TryInfrastructureRecoveryAsync (issues #2139, #2146).
        var currentStatus = await _workItemTransition.GetCurrentStatusAsync(workItemId, ct);

        if (IsIdempotentTransition(currentStatus, status))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(
                    "WorkItem {WorkItemId} already in {Current} state, skipping fallback chain for requested {Target}",
                    workItemId, currentStatus, status);
            return false;
        }

        // Step 1: direct transition
        if (await TryDirectAsync(workItemId, status, errorMessage, failureReason, ct))
        {
            await RecordOutcomeAsync(workItemId, status, failureReason);
            return true;
        }

        // Step 2: two-step via Running (Dispatched → Running → terminal)
        // TODO: Including Failed here is a behavioral expansion relative to the original AgentHubFacade
        // which only attempted two-step for Succeeded/Cancelled. Failed reaches terminal directly from all
        // states in the current state machine, so this branch is never exercised for Failed today.
        // If the state machine changes to block direct → Failed from some states, the two-step
        // Running → Failed path will activate. Verify this is intentional before any state-machine changes.
        if (status is WorkItemStatus.Succeeded or WorkItemStatus.Cancelled or WorkItemStatus.Failed)
        {
            if (await TryTwoStepAsync(workItemId, status, errorMessage, failureReason, ct))
            {
                await RecordOutcomeAsync(workItemId, status, failureReason);
                return true;
            }
        }

        // Step 3: infrastructure-failure recovery (last resort)
        if (await TryInfrastructureRecoveryAsync(workItemId, status, errorMessage, failureReason, ct))
        {
            await RecordOutcomeAsync(workItemId, status, failureReason);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public Task<WorkItemStatus?> GetCurrentStatusAsync(Guid workItemId, CancellationToken ct)
        => _workItemTransition.GetCurrentStatusAsync(workItemId, ct);

    private async Task<bool> TryDirectAsync(
        Guid workItemId, WorkItemStatus status,
        string? errorMessage, FailureReason? failureReason,
        CancellationToken ct)
    {
        var result = await _workItemTransition.TransitionAsync(workItemId, status, BuildMutationAction(status, errorMessage, failureReason), ct: ct);

        if (result)
        {
            _logger.LogInformation(
                "WorkItem {WorkItemId} transitioned to {Status}",
                workItemId, status);
        }

        return result;
    }

    private async Task<bool> TryTwoStepAsync(
        Guid workItemId, WorkItemStatus status,
        string? errorMessage, FailureReason? failureReason,
        CancellationToken ct)
    {
        _logger.LogWarning(
            "WorkItem {WorkItemId} direct transition to {Status} rejected, attempting two-step via Running",
            workItemId, status);

        var intermediateResult = await _workItemTransition.TransitionAsync(
            workItemId, WorkItemStatus.Running, ct: ct);

        if (!intermediateResult)
            return false;

        // Transition succeeded to Running — now transition to the terminal status.
        var finalResult = await _workItemTransition.TransitionAsync(
            workItemId, status, BuildMutationAction(status, errorMessage, failureReason), ct: ct);

        if (finalResult)
        {
            _logger.LogInformation(
                "WorkItem {WorkItemId} two-step transition to {Status} succeeded (via Running)",
                workItemId, status);
        }
        else
        {
            // Running → terminal failed. Item is now stuck in Running with no further fallback here.
            // Log a warning so this doesn't go unnoticed — the caller's retry loop or
            // ReconciliationService will eventually recover the item.
            _logger.LogWarning(
                "WorkItem {WorkItemId} two-step transition: Running → {Status} failed after intermediate Running succeeded. Item may be stuck in Running.",
                workItemId, status);
        }

        return finalResult;
    }

    private async Task<bool> TryInfrastructureRecoveryAsync(
        Guid workItemId, WorkItemStatus status,
        string? errorMessage, FailureReason? failureReason,
        CancellationToken ct)
    {
        var recovered = await _workItemTransition.TryRecoverFromInfrastructureFailureAsync(
            workItemId, status, BuildMutationAction(status, errorMessage, failureReason), ct);

        if (recovered)
        {
            _logger.LogWarning(
                "WorkItem {WorkItemId} recovered from race-induced Failed to {Status}",
                workItemId, status);
        }

        return recovered;
    }

    /// <summary>
    /// Records <c>pipeline.run.outcomes</c> for a terminal transition that actually changed state.
    /// No-op when <see cref="_dbFactory"/> is null (test and E2E constructions that omit the factory)
    /// or when <paramref name="status"/> is not a terminal status.
    /// Telemetry failures are caught and logged at Warning; they never propagate to the caller.
    /// </summary>
    private async Task RecordOutcomeAsync(Guid workItemId, WorkItemStatus status, FailureReason? failureReason)
    {
        if (_dbFactory is null) return;
        if (status is not (WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)) return;

        try
        {
            var (duration, runTypeTag, _, projectName) =
                await RunOutcomeTelemetry.ResolveRunContextAsync(workItemId, _dbFactory, CancellationToken.None);
            var (outcome, failureReasonTag) =
                RunOutcomeTelemetry.DeriveOutcome(status, payload: null, failureReason);
            RunOutcomeTelemetry.RecordRunOutcomeMetrics(runTypeTag, outcome, failureReasonTag, projectName, duration);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "WorkItemFallbackTransitionService: outcome telemetry for WorkItem {WorkItemId} failed (non-fatal)",
                workItemId);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when calling the fallback chain for the given
    /// <paramref name="current"/> → <paramref name="target"/> transition would be a no-op
    /// or is structurally impossible, so the chain should be skipped entirely.
    ///
    /// <list type="bullet">
    ///   <item><description>
    ///     Any terminal → same terminal: idempotent no-op (e.g. Failed→Failed, Succeeded→Succeeded,
    ///     Cancelled→Cancelled). <see cref="WorkItemTransitionService.TransitionAsync"/> returns
    ///     <see langword="true"/> for already-at-target calls, so without this guard a second hub
    ///     completion event would incorrectly record a duplicate metric.
    ///   </description></item>
    ///   <item><description>
    ///     Succeeded or Cancelled → any different state: no recovery path exists for these states.
    ///   </description></item>
    /// </list>
    ///
    /// Explicitly returns <see langword="false"/> (chain should proceed) for Failed → any different
    /// state, because Failed items can be recovered via
    /// <see cref="TryInfrastructureRecoveryAsync"/> (infra-failure and timeout-race paths).
    /// </summary>
    private static bool IsIdempotentTransition(WorkItemStatus? current, WorkItemStatus target)
    {
        if (current is not (WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled))
            return false;   // non-terminal current: always run the chain

        if (current == target)
            return true;    // same-state no-op for all three terminal states

        // Failed can recover to other states (infra failure / timeout race);
        // Succeeded and Cancelled have no recovery path.
        return current is WorkItemStatus.Succeeded or WorkItemStatus.Cancelled;
    }

    private static Action<WorkItemEntity> BuildMutationAction(
        WorkItemStatus status, string? errorMessage, FailureReason? failureReason)
        => item =>
        {
            if (status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
                item.CompletedAt = DateTimeOffset.UtcNow;
            if (status == WorkItemStatus.Failed)
            {
                item.ErrorMessage = errorMessage ?? "Job failed without specific error information";
                item.FailureReason ??= failureReason ?? FailureReason.AgentError;
            }
        };
}
