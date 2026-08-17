using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// Implements the shared fallback chain for work item status transitions:
/// <list type="number">
///   <item><description>Direct transition — sets CompletedAt, ErrorMessage, FailureReason.</description></item>
///   <item><description>Two-step via Running — Dispatched→Running→terminal, for any terminal status
///   (Succeeded, Cancelled, or Failed).</description></item>
///   <item><description>Infrastructure-failure recovery — bypasses terminal state for items stuck
///   in Failed with <c>FailureReason.InfrastructureFailure</c>.</description></item>
/// </list>
/// This service is a singleton — it contains no per-request state and wraps
/// <see cref="WorkItemTransitionService"/>, which is also singleton.
/// </summary>
public sealed class WorkItemFallbackTransitionService : IWorkItemFallbackTransitionService
{
    private readonly WorkItemTransitionService _workItemTransition;
    private readonly ILogger<WorkItemFallbackTransitionService> _logger;

    public WorkItemFallbackTransitionService(
        WorkItemTransitionService workItemTransition,
        ILogger<WorkItemFallbackTransitionService> logger)
    {
        ArgumentNullException.ThrowIfNull(workItemTransition);
        ArgumentNullException.ThrowIfNull(logger);
        _workItemTransition = workItemTransition;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryFallbackChainAsync(
        Guid workItemId, WorkItemStatus status,
        string? errorMessage, FailureReason? failureReason,
        CancellationToken ct)
    {
        // Step 1: direct transition
        if (await TryDirectAsync(workItemId, status, errorMessage, failureReason, ct))
            return true;

        // Step 2: two-step via Running (Dispatched → Running → terminal)
        // TODO: Including Failed here is a behavioral expansion relative to the original AgentHubFacade
        // which only attempted two-step for Succeeded/Cancelled. Failed reaches terminal directly from all
        // states in the current state machine, so this branch is never exercised for Failed today.
        // If the state machine changes to block direct → Failed from some states, the two-step
        // Running → Failed path will activate. Verify this is intentional before any state-machine changes.
        if (status is WorkItemStatus.Succeeded or WorkItemStatus.Cancelled or WorkItemStatus.Failed)
        {
            var twoStepResult = await TryTwoStepAsync(workItemId, status, errorMessage, failureReason, ct);
            if (twoStepResult)
                return true;
        }

        // Step 3: infrastructure-failure recovery (last resort)
        return await TryInfrastructureRecoveryAsync(workItemId, status, errorMessage, failureReason, ct);
    }

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
                "WorkItem {WorkItemId} recovered from infrastructure-failure Failed to {Status}",
                workItemId, status);
        }

        return recovered;
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
