using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Handles work item label swapping to agent:in-progress with exponential backoff retry
/// and label reconciliation flagging on exhaustion or shutdown.
/// Extracted from <see cref="PendingWorkItemDrainService"/> to reduce its size (#1871).
/// </summary>
public sealed class LabelSwapService
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ILabelService _labelService;
    private readonly ILogger<LabelSwapService> _logger;

    public LabelSwapService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILabelService labelService,
        ILogger<LabelSwapService> logger)
    {
        // TODO: Add ArgumentNullException.ThrowIfNull for dbFactory, labelService, and logger to
        // match the constructor guard pattern used throughout the Dispatch folder. A null
        // dependency here would produce a NullReferenceException inside the retry/reconciliation
        // paths rather than at construction time. (#1871)
        _dbFactory = dbFactory;
        _labelService = labelService;
        _logger = logger;
    }

    /// <summary>
    /// Swaps the work item label to agent:in-progress with exponential backoff retry.
    /// Flags for reconciliation if all attempts fail or if shutdown occurs mid-retry.
    /// </summary>
    public async Task SwapLabelWithRetryAsync(Guid workItemId, JobDistributionRequest request, CancellationToken ct)
    {
        const int maxLabelSwapAttempts = 3; // 1 initial + 2 retries
        var providerForLabel = request.RunType == PipelineRunType.Review
            ? request.RepoProviderConfigId
            : request.IssueProviderConfigId;
        var targetKind = request.RunType == PipelineRunType.Review
            ? LabelTargetKind.PullRequest
            : LabelTargetKind.Issue;

        bool labelSwapCompleted = false;
        try
        {
            for (int attempt = 1; attempt <= maxLabelSwapAttempts; attempt++)
            {
                if (await TrySwapLabelOnceAsync(workItemId, request, providerForLabel, targetKind, attempt, maxLabelSwapAttempts, ct))
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
            if (!labelSwapCompleted && ct.IsCancellationRequested)
            {
                await FlagForLabelReconciliationAsync(workItemId);
            }
        }
    }

    /// <summary>
    /// Performs a single label swap attempt. Returns <c>true</c> if the swap succeeded
    /// (caller should set <c>labelSwapCompleted = true</c> and break). Returns <c>false</c>
    /// if the attempt failed with a non-cancellation exception (caller should proceed to the
    /// next attempt or stop if retries are exhausted). Propagates <see cref="OperationCanceledException"/>
    /// so the outer <c>finally</c> block can flag for reconciliation on shutdown.
    /// </summary>
    private async Task<bool> TrySwapLabelOnceAsync(
        Guid workItemId,
        JobDistributionRequest request,
        ProviderConfigId providerForLabel,
        LabelTargetKind targetKind,
        int attempt,
        int maxAttempts,
        CancellationToken ct)
    {
        try
        {
            await _labelService.SwapLabelStrictAsync(
                providerForLabel, request.IssueIdentifier, AgentLabels.InProgress, targetKind, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)); // 200ms, 400ms
                _logger.LogWarning(ex,
                    "LabelSwapService: label swap attempt {Attempt}/{Max} failed for WorkItem {WorkItemId}, retrying in {Delay}ms",
                    attempt, maxAttempts, workItemId, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
            else
            {
                _logger.LogWarning(ex,
                    "LabelSwapService: label swap exhausted all {Max} attempts for WorkItem {WorkItemId} — flagging for reconciliation",
                    maxAttempts, workItemId);
                await FlagForLabelReconciliationAsync(workItemId);
            }
            return false;
        }
    }

    /// <summary>
    /// Flags a work item for label reconciliation after the retry loop for SwapLabelStrictAsync
    /// has been exhausted. Uses a separate DbContext to avoid interfering with the outer query.
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
