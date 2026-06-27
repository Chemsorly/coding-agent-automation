using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// Handles optimistic concurrency conflicts on WorkItem status updates.
/// Uses IDbContextFactory for singleton-safe context creation (compatible with BackgroundServices).
/// </summary>
public sealed class WorkItemTransitionService
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ILogger<WorkItemTransitionService> _logger;

    public WorkItemTransitionService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILogger<WorkItemTransitionService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
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
            (WorkItemStatus.Dispatched, WorkItemStatus.Running or WorkItemStatus.Failed or WorkItemStatus.Cancelled) => true,
            (WorkItemStatus.Running, WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled) => true,
            _ => false
        };
}
