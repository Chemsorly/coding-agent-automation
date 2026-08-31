using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// Handles optimistic concurrency conflicts on WorkItem status updates.
/// Uses IDbContextFactory for singleton-safe context creation (compatible with BackgroundServices).
/// Wraps DB operations with a Polly resilience pipeline for transient fault tolerance.
/// </summary>
public sealed class WorkItemTransitionService : IWorkItemQueryService, IWorkItemTransitionService
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ILogger<WorkItemTransitionService> _logger;
    private readonly ResiliencePipeline? _resiliencePipeline;

    /// <summary>
    /// Well-known pipeline key for DB background operations (matches WorkDistributionRegistration).
    /// </summary>
    internal const string DbBackgroundPipelineKey = "db-background";

    public WorkItemTransitionService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILogger<WorkItemTransitionService> logger,
        ResiliencePipelineProvider<string>? pipelineProvider = null)
    {
        _dbFactory = dbFactory;
        _logger = logger;

        // Optional: if Polly pipelines are registered (DB mode), use them for transient fault retry.
        if (pipelineProvider is not null)
        {
            try
            {
                _resiliencePipeline = pipelineProvider.GetPipeline(DbBackgroundPipelineKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve resilience pipeline '{Key}', operating without retry protection",
                    DbBackgroundPipelineKey);
            }
        }
    }

    /// <summary>
    /// Attempts a state transition with retry-on-conflict.
    /// Re-reads the row after DbUpdateConcurrencyException and re-validates
    /// that the transition is still legal against the refreshed state.
    /// Returns true if transition succeeded, false if rejected or row moved past target.
    /// </summary>
    /// <param name="workItemId">The work item to transition.</param>
    /// <param name="target">The desired target status.</param>
    /// <param name="mutate">Optional action to set additional fields during the transition (e.g., CompletedAt, ErrorMessage).</param>
    /// <param name="maxRetries">Maximum retry attempts on concurrency conflict (default 3).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> TransitionAsync(
        Guid workItemId, WorkItemStatus target,
        Action<WorkItemEntity>? mutate = null,
        int maxRetries = 3,
        CancellationToken ct = default)
    {
        if (_resiliencePipeline is not null)
        {
            return await _resiliencePipeline.ExecuteAsync(
                async token => await TransitionCoreAsync(workItemId, target, mutate, token, maxRetries),
                ct);
        }

        return await TransitionCoreAsync(workItemId, target, mutate, ct, maxRetries);
    }

    private async Task<bool> TransitionCoreAsync(
        Guid workItemId, WorkItemStatus target,
        Action<WorkItemEntity>? mutate,
        CancellationToken ct, int maxRetries)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var item = await db.WorkItems.FindAsync([workItemId], ct);
            if (item is null)
            {
                _logger.LogWarning("WorkItem {WorkItemId} not found during transition to {Target}", workItemId, target);
                return false;
            }

            // Already at target (idempotent)
            if (item.Status == target) return true;

            // Validate transition is legal from current state
            if (!IsValidTransition(item.Status, target))
            {
                _logger.LogWarning(
                    "Invalid transition for WorkItem {WorkItemId}: {Current} → {Target}",
                    workItemId, item.Status, target);
                return false;
            }

            item.Status = target;
            mutate?.Invoke(item);

            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxRetries)
            {
                _logger.LogInformation(
                    "Concurrency conflict on WorkItem {WorkItemId} transition to {Target}, retry {Attempt}/{MaxRetries}",
                    workItemId, target, attempt + 1, maxRetries);
                // Row modified by another writer — retry with fresh state
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Final attempt exhausted — all retries consumed by concurrency conflicts
                _logger.LogWarning(ex,
                    "WorkItem {WorkItemId} transition to {Target} failed after all retries (concurrency exhausted)",
                    workItemId, target);
                return false;
            }
        }

        _logger.LogWarning(
            "WorkItem {WorkItemId} transition to {Target} failed after exhausting all retries",
            workItemId, target);
        return false;
    }

    /// <summary>
    /// Atomic compare-and-swap transition.
    /// Succeeds only if the current status matches <paramref name="expectedCurrent"/>.
    /// Never idempotent — returns false when current == target regardless of expectedCurrent.
    /// Two concurrent callers with the same expectedCurrent will see exactly one succeed.
    /// </summary>
    /// <param name="workItemId">The work item to transition.</param>
    /// <param name="expectedCurrent">The status the row must currently have for the transition to apply.</param>
    /// <param name="target">The desired target status.</param>
    /// <param name="mutate">Optional action to set additional fields on success (e.g., AssignedAgentId, DispatchedAt).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> TransitionIfAsync(
        Guid workItemId,
        WorkItemStatus expectedCurrent,
        WorkItemStatus target,
        Action<WorkItemEntity>? mutate = null,
        CancellationToken ct = default)
    {
        if (_resiliencePipeline is not null)
            return await _resiliencePipeline.ExecuteAsync(
                async token => await TransitionIfCoreAsync(workItemId, expectedCurrent, target, mutate, 3, token), ct);

        return await TransitionIfCoreAsync(workItemId, expectedCurrent, target, mutate, 3, ct);
    }

    private async Task<bool> TransitionIfCoreAsync(
        Guid workItemId, WorkItemStatus expectedCurrent, WorkItemStatus target,
        Action<WorkItemEntity>? mutate, int maxRetries, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var item = await db.WorkItems.FindAsync([workItemId], ct);
            if (item is null)
            {
                _logger.LogWarning("WorkItem {WorkItemId} not found during TransitionIfAsync to {Target}", workItemId, target);
                return false;
            }

            // Not idempotent — fail if already at target
            if (item.Status == target) return false;

            // CAS guard — fail if current != expected
            if (item.Status != expectedCurrent) return false;

            // Standard transition validation
            if (!IsValidTransition(item.Status, target))
            {
                _logger.LogWarning(
                    "Invalid transition for WorkItem {WorkItemId}: {Current} → {Target} (TransitionIfAsync)",
                    workItemId, item.Status, target);
                return false;
            }

            item.Status = target;
            mutate?.Invoke(item);

            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxRetries)
            {
                _logger.LogInformation(
                    ex,
                    "Concurrency conflict on WorkItem {WorkItemId} TransitionIfAsync to {Target}, retry {Attempt}/{MaxRetries}",
                    workItemId, target, attempt + 1, maxRetries);
                // Row changed — retry; the loop will re-read and re-check both conditions
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Final attempt exhausted — all retries consumed by concurrency conflicts
                _logger.LogWarning(
                    ex,
                    "WorkItem {WorkItemId} TransitionIfAsync to {Target} failed after all retries (concurrency exhausted)",
                    workItemId, target);
                return false;
            }
        }

        _logger.LogWarning(
            "WorkItem {WorkItemId} TransitionIfAsync to {Target} failed after exhausting all retries",
            workItemId, target);
        return false;
    }

    /// <summary>
    /// Determines whether a state transition from <paramref name="current"/> to <paramref name="target"/> is allowed.
    /// </summary>
    public static bool IsValidTransition(WorkItemStatus current, WorkItemStatus target)
        => (current, target) switch
        {
            (WorkItemStatus.Pending, WorkItemStatus.Dispatched or WorkItemStatus.Failed or WorkItemStatus.Cancelled) => true,
            (WorkItemStatus.Dispatched, WorkItemStatus.Running or WorkItemStatus.Failed or WorkItemStatus.Cancelled or WorkItemStatus.Pending) => true,
            (WorkItemStatus.Running, WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled) => true,
            // Requeue paths: Failed/Cancelled → Pending (Req 6.1, POST /api/work-items/{id}/requeue)
            (WorkItemStatus.Failed, WorkItemStatus.Pending) => true,
            (WorkItemStatus.Cancelled, WorkItemStatus.Pending) => true,
            _ => false
        };

    /// <summary>
    /// Attempts to recover a WorkItem from a race-induced Failed state.
    /// This is an explicit, auditable bypass of the terminal state machine rule that activates
    /// when the FailureReason is <see cref="FailureReason.InfrastructureFailure"/> (e.g., SignalR
    /// delivery timeout) or <see cref="FailureReason.Timeout"/> (e.g., ReconciliationLoop timeout
    /// race where the agent completed after the server gave up waiting). Both reasons represent
    /// "server gave up waiting, but the agent may still succeed" and share identical recovery semantics.
    /// Does NOT modify IsValidTransition — the standard state machine remains strict.
    /// Wraps DB operations in the Polly resilience pipeline for transient fault tolerance,
    /// and retries up to 3 times on DbUpdateConcurrencyException (matching TransitionCoreAsync).
    /// </summary>
    /// <param name="workItemId">The work item to recover.</param>
    /// <param name="desiredStatus">The target status (Running, Succeeded, Failed, or Cancelled).</param>
    /// <param name="mutate">Optional action to set additional fields during recovery.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if recovery succeeded, false if not applicable (wrong state, wrong reason, or item not found).</returns>
    public async Task<bool> TryRecoverFromInfrastructureFailureAsync(
        Guid workItemId, WorkItemStatus desiredStatus,
        Action<WorkItemEntity>? mutate = null,
        CancellationToken ct = default)
    {
        if (_resiliencePipeline is not null)
        {
            return await _resiliencePipeline.ExecuteAsync(
                async token => await TryRecoverFromInfrastructureFailureCoreAsync(workItemId, desiredStatus, mutate, token),
                ct);
        }

        return await TryRecoverFromInfrastructureFailureCoreAsync(workItemId, desiredStatus, mutate, ct);
    }

    private async Task<bool> TryRecoverFromInfrastructureFailureCoreAsync(
        Guid workItemId, WorkItemStatus desiredStatus,
        Action<WorkItemEntity>? mutate,
        CancellationToken ct)
    {
        const int MaxRetries = 3;

        // Validate desiredStatus upfront (no retry needed for invalid input)
        if (desiredStatus is not (WorkItemStatus.Running or WorkItemStatus.Succeeded
            or WorkItemStatus.Failed or WorkItemStatus.Cancelled))
        {
            return false;
        }

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var item = await db.WorkItems.FindAsync([workItemId], ct);
            if (item is null)
            {
                _logger.LogWarning("TryRecoverFromInfrastructureFailure: WorkItem {WorkItemId} not found", workItemId);
                return false;
            }

            // Idempotent: already at target
            if (item.Status == desiredStatus)
                return true;

            // Only recover from Failed state
            if (item.Status != WorkItemStatus.Failed)
                return false;

            // Only recover race-induced failures (delivery timeouts, reconciliation-loop timeouts),
            // not legitimate agent errors. Both InfrastructureFailure and Timeout represent
            // "server gave up waiting, but agent may still succeed" — recovery semantics are identical.
            if (item.FailureReason is not (FailureReason.InfrastructureFailure or FailureReason.Timeout))
                return false;

            // Perform the recovery transition
            item.Status = desiredStatus;
            mutate?.Invoke(item);

            try
            {
                await db.SaveChangesAsync(ct);
                _logger.LogWarning(
                    "Recovered WorkItem {WorkItemId} from {FailureReason}-induced Failed to {DesiredStatus}",
                    workItemId, item.FailureReason, desiredStatus);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxRetries)
            {
                var retryAttempt = attempt + 1;
                _logger.LogInformation(
                    "Concurrency conflict on WorkItem {WorkItemId} recovery to {DesiredStatus}, retry {Attempt}/{MaxRetries}",
                    workItemId, desiredStatus, retryAttempt, MaxRetries);
                // Row modified by another writer — retry with fresh state
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Final attempt exhausted — all retries consumed by concurrency conflicts
                _logger.LogWarning(ex,
                    "WorkItem {WorkItemId} recovery to {DesiredStatus} failed after all retries (concurrency exhausted)",
                    workItemId, desiredStatus);
                return false;
            }
        }

        _logger.LogWarning(
            "WorkItem {WorkItemId} recovery to {DesiredStatus} failed after exhausting all retries",
            workItemId, desiredStatus);
        return false;
    }

    /// <summary>
    /// Gets the current status of a work item, or null if not found.
    /// Used by <see cref="WorkItemFallbackTransitionService"/> for early-exit detection.
    /// </summary>
    public async Task<WorkItemStatus?> GetCurrentStatusAsync(Guid workItemId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == workItemId)
            .Select(w => (WorkItemStatus?)w.Status)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Gets the current RetryCount for a work item.
    /// </summary>
    public async Task<int> GetRetryCountAsync(Guid workItemId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        return item?.RetryCount ?? 0;
    }

    /// <summary>
    /// Updates the <see cref="WorkItemEntity.PriorityWeight"/> of a Pending work item.
    /// Returns <see langword="true"/> on success, <see langword="false"/> if not found or not Pending.
    /// The caller is responsible for range validation (0–1000) before calling this method.
    /// Retries up to <paramref name="maxRetries"/> times on DbUpdateConcurrencyException.
    /// </summary>
    public async Task<UpdatePriorityWeightResult> UpdatePriorityWeightAsync(
        Guid workItemId,
        int priorityWeight,
        CancellationToken ct,
        int maxRetries = 3)
    {
        Exception? lastConcurrencyEx = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var item = await db.WorkItems.FindAsync([workItemId], ct);
            if (item is null)
                return UpdatePriorityWeightResult.NotFound;

            if (item.Status != WorkItemStatus.Pending)
                return UpdatePriorityWeightResult.NotPending;

            item.PriorityWeight = priorityWeight;

            try
            {
                await db.SaveChangesAsync(ct);
                return UpdatePriorityWeightResult.Success;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                lastConcurrencyEx = ex;
                if (attempt < maxRetries)
                {
                    _logger.LogInformation(
                        ex,
                        "Concurrency conflict on WorkItem {WorkItemId} PriorityWeight update, retry {Attempt}/{MaxRetries}",
                        workItemId, attempt + 1, maxRetries);
                }
            }
        }

        _logger.LogWarning(
            lastConcurrencyEx,
            "WorkItem {WorkItemId} PriorityWeight update failed after all {MaxRetries} retries (concurrency exhausted)",
            workItemId, maxRetries);
        // TODO: Returning NotFound here is semantically incorrect — the item exists and is Pending; only the
        // save failed due to repeated concurrency conflicts. The caller maps NotFound → HTTP 404, so clients
        // receive a misleading 404 instead of a conflict/retry signal. Introduce a distinct
        // UpdatePriorityWeightResult.ConcurrencyConflict value (or throw) and map it to HTTP 409 in the endpoint.
        return UpdatePriorityWeightResult.NotFound;
    }

    /// <summary>
    /// Re-queues a work item: transitions back to Pending, increments RetryCount,
    /// clears DispatchedAt and AssignedAgentId so the drain service picks it up again.
    /// </summary>
    public async Task RequeueAsync(Guid workItemId, CancellationToken ct)
    {
        await TransitionAsync(workItemId, WorkItemStatus.Pending, item =>
        {
            item.RetryCount++;
            item.DispatchedAt = null;
            item.AssignedAgentId = null;
        }, ct: ct);
    }

    /// <inheritdoc />
    public async Task<bool> HasAgentErrorSinceAsync(
        IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId,
        DateTimeOffset since, CancellationToken ct)
    {
        var issueIdentifierValue = issueIdentifier.Value;
        var providerConfigIdValue = issueProviderConfigId.Value;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkItems.AnyAsync(w =>
            w.IssueIdentifier == issueIdentifierValue
            && w.IssueProviderConfigId == providerConfigIdValue
            && w.Status == WorkItemStatus.Failed
            && w.FailureReason == FailureReason.AgentError
            && w.CompletedAt > since, ct);
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetLastSuccessfulCompletionAsync(
        IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId,
        CancellationToken ct)
    {
        var issueIdentifierValue = issueIdentifier.Value;
        var providerConfigIdValue = issueProviderConfigId.Value;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkItems
            .Where(w => w.IssueIdentifier == issueIdentifierValue
                && w.IssueProviderConfigId == providerConfigIdValue
                && w.Status == WorkItemStatus.Succeeded
                && w.CompletedAt != null)
            .Select(w => w.CompletedAt)
            .MaxAsync(ct);
    }
}

/// <summary>
/// Result codes returned by <see cref="WorkItemTransitionService.UpdatePriorityWeightAsync"/>.
/// </summary>
public enum UpdatePriorityWeightResult
{
    /// <summary>Update succeeded.</summary>
    Success,
    /// <summary>Work item was not found.</summary>
    NotFound,
    /// <summary>Work item exists but is not in Pending status.</summary>
    NotPending
}
