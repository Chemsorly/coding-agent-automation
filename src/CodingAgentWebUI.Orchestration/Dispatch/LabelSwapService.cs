using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Encapsulates label swap + reconciliation flagging logic with configurable retry policy.
/// Used by both <see cref="DispatchService"/> (K8s mode, maxAttempts=1) and
/// <see cref="PendingWorkItemDrainService"/> (SignalR mode, maxAttempts=3).
/// </summary>
public interface ILabelSwapService
{
    // TODO: Update XML doc to explicitly note that OCE propagation can be delayed by the
    // reconciliation write: FlagForLabelReconciliationAsync uses CancellationToken.None so it
    // completes even during shutdown before the OCE unwinds to the caller.
    // See review finding: DotNetSpecialist WARNING LabelSwapService.cs:56
    /// <summary>
    /// Swaps the work item label to agent:in-progress with exponential backoff retry.
    /// Flags for reconciliation if all attempts fail or if shutdown occurs mid-retry.
    /// Propagates <see cref="OperationCanceledException"/> unconditionally — callers
    /// may observe this if cancellation fires during the swap or during backoff delay.
    /// </summary>
    Task SwapLabelWithRetryAsync(
        Guid workItemId,
        ProviderConfigId providerConfigId,
        IssueIdentifier issueIdentifier,
        LabelTargetKind targetKind,
        CancellationToken ct);
}

/// <summary>
/// Default implementation of <see cref="ILabelSwapService"/>.
/// Extracted from <c>PendingWorkItemDrainService.SwapLabelWithRetryAsync</c>,
/// <c>TrySwapLabelOnceAsync</c>, and <c>FlagForLabelReconciliationAsync</c>.
/// </summary>
internal sealed class LabelSwapService : ILabelSwapService
{
    private readonly ILabelService _labelService;
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ILogger<LabelSwapService> _logger;
    private readonly int _maxAttempts;

    public LabelSwapService(
        ILabelService labelService,
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILogger<LabelSwapService> logger,
        int maxAttempts = 3)
    {
        _labelService = labelService;
        _dbFactory = dbFactory;
        _logger = logger;
        _maxAttempts = maxAttempts;
    }

    /// <inheritdoc/>
    public async Task SwapLabelWithRetryAsync(
        Guid workItemId,
        ProviderConfigId providerConfigId,
        IssueIdentifier issueIdentifier,
        LabelTargetKind targetKind,
        CancellationToken ct)
    {
        bool labelSwapCompleted = false;
        try
        {
            for (int attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                if (await TrySwapLabelOnceAsync(workItemId, providerConfigId, issueIdentifier, targetKind, attempt, ct))
                {
                    labelSwapCompleted = true;
                    break;
                }
            }
        }
        finally
        {
            // If shutdown occurred during backoff (Task.Delay throws OCE) or during
            // SwapLabelStrictAsync itself, the label swap never completed. Flag for
            // reconciliation so OrphanedLabelRecoveryService can fix the stale label. (#1681)
            // TODO: Narrow race: when maxAttempts=1 and a non-OCE exception is thrown, the
            // catch block inside TrySwapLabelOnceAsync already calls FlagForLabelReconciliationAsync.
            // If the caller cancels the token between that call and this finally block, the flag
            // method is called a second time. The double-write is idempotent but produces a
            // redundant DB round-trip and a misleading log warning. Consider tracking whether
            // FlagForLabelReconciliationAsync was already called (e.g. a bool) to skip the
            // finally-block write in that case.
            // See review finding: DotNetSpecialist WARNING LabelSwapService.cs:87
            if (!labelSwapCompleted && ct.IsCancellationRequested)
            {
                await FlagForLabelReconciliationAsync(workItemId);
            }
        }
    }

    /// <summary>
    /// Performs a single label swap attempt. Returns <c>true</c> if the swap succeeded.
    /// Returns <c>false</c> if the attempt failed with a non-cancellation exception
    /// (caller should proceed to the next attempt or stop if retries are exhausted).
    /// Propagates <see cref="OperationCanceledException"/> so the outer <c>finally</c>
    /// block can flag for reconciliation on shutdown.
    /// </summary>
    private async Task<bool> TrySwapLabelOnceAsync(
        Guid workItemId,
        ProviderConfigId providerConfigId,
        IssueIdentifier issueIdentifier,
        LabelTargetKind targetKind,
        int attempt,
        CancellationToken ct)
    {
        try
        {
            await _labelService.SwapLabelStrictAsync(
                providerConfigId, issueIdentifier, AgentLabels.InProgress, targetKind, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (attempt < _maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)); // 200ms, 400ms
                _logger.LogWarning(ex,
                    "LabelSwapService: label swap attempt {Attempt}/{Max} failed for WorkItem {WorkItemId}, retrying in {Delay}ms",
                    attempt, _maxAttempts, workItemId, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
            else
            {
                _logger.LogWarning(ex,
                    "LabelSwapService: label swap exhausted all {Max} attempts for WorkItem {WorkItemId} — flagging for reconciliation",
                    _maxAttempts, workItemId);
                await FlagForLabelReconciliationAsync(workItemId);
            }
            return false;
        }
    }

    /// <summary>
    /// Flags a work item for label reconciliation. Uses a separate DbContext to avoid
    /// interfering with the outer query. Uses <see cref="CancellationToken.None"/> so
    /// that graceful shutdown does not prevent the flag from being persisted.
    /// </summary>
    private async Task FlagForLabelReconciliationAsync(Guid workItemId)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
            var entity = await db.WorkItems.FindAsync([workItemId], CancellationToken.None);
            if (entity is not null)
            {
                entity.NeedsLabelReconciliation = true;
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LabelSwapService: failed to flag WorkItem {WorkItemId} for label reconciliation",
                workItemId);
        }
    }
}
