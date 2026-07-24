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
public sealed class WorkItemTransitionService : IWorkItemQueryService
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
    /// <param name="ct">Cancellation token.</param>
    /// <param name="maxRetries">Maximum retry attempts on concurrency conflict (default 3).</param>
    public async Task<bool> TransitionAsync(
        Guid workItemId, WorkItemStatus target,
        Action<WorkItemEntity>? mutate = null,
        CancellationToken ct = default, int maxRetries = 3)
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
        }

        _logger.LogWarning(
            "WorkItem {WorkItemId} transition to {Target} failed after exhausting all retries",
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
            _ => false
        };

    /// <summary>
    /// Attempts to recover a WorkItem from an infrastructure-failure-induced Failed state.
    /// This is an explicit, auditable bypass of the terminal state machine rule that only
    /// activates when the FailureReason is InfrastructureFailure (e.g., SignalR delivery timeout).
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

            // Only recover infrastructure failures (delivery timeouts), not legitimate agent errors
            if (item.FailureReason != FailureReason.InfrastructureFailure)
                return false;

            // Perform the recovery transition
            item.Status = desiredStatus;
            mutate?.Invoke(item);

            try
            {
                await db.SaveChangesAsync(ct);
                _logger.LogWarning(
                    "Recovered WorkItem {WorkItemId} from infrastructure-failure-induced Failed to {DesiredStatus}",
                    workItemId, desiredStatus);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxRetries)
            {
                _logger.LogInformation(
                    "Concurrency conflict on WorkItem {WorkItemId} recovery to {DesiredStatus}, retry {Attempt}/{MaxRetries}",
                    workItemId, desiredStatus, attempt + 1, MaxRetries);
                // Row modified by another writer — retry with fresh state
            }
        }

        // Structurally unreachable for the "all-retries-throw" case (final attempt propagates unhandled).
        // Exists for pattern symmetry with TransitionCoreAsync.
        // TODO: Document in method XML doc that callers must handle DbUpdateConcurrencyException when
        // all retries are exhausted under sustained concurrency pressure — the method is not purely true/false.
        _logger.LogWarning(
            "WorkItem {WorkItemId} recovery to {DesiredStatus} failed after exhausting all retries",
            workItemId, desiredStatus);
        return false;
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
        }, ct);
    }

    /// <inheritdoc />
    // TODO: No integration test exists to verify this query correctly filters only
    // FailureReason.AgentError and excludes Timeout, InfrastructureFailure, and
    // TokenRefreshFailure. The unit tests mock the interface so the actual DB filtering
    // logic has no coverage. A regression in the EF predicate would go undetected.
    public async Task<bool> HasAgentErrorSinceAsync(
        string issueIdentifier, ProviderConfigId issueProviderConfigId,
        DateTimeOffset since, CancellationToken ct)
    {
        var providerConfigIdValue = issueProviderConfigId.Value;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkItems.AnyAsync(w =>
            w.IssueIdentifier == issueIdentifier
            && w.IssueProviderConfigId == providerConfigIdValue
            && w.Status == WorkItemStatus.Failed
            && w.FailureReason == FailureReason.AgentError
            && w.CompletedAt > since, ct);
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetLastSuccessfulCompletionAsync(
        string issueIdentifier, ProviderConfigId issueProviderConfigId,
        CancellationToken ct)
    {
        var providerConfigIdValue = issueProviderConfigId.Value;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkItems
            .Where(w => w.IssueIdentifier == issueIdentifier
                && w.IssueProviderConfigId == providerConfigIdValue
                && w.Status == WorkItemStatus.Succeeded
                && w.CompletedAt != null)
            .Select(w => (DateTimeOffset?)w.CompletedAt)
            .MaxAsync(ct);
    }
}
