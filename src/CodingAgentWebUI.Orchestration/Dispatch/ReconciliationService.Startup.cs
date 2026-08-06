using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

public sealed partial class ReconciliationService
{
    // ── Startup Reconciliation ───────────────────────────────────────────

    private async Task RunStartupReconciliationAsync(CancellationToken ct)
    {
        try
        {
            await ReconcileStartupPvcsAsync(ct);
            await ReconcileStartupLabelsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Error(ex, "ReconciliationService: startup reconciliation failed");
        }
    }

    /// <summary>
    /// On leader acquisition, verify claimed PVCs against existing K8s Jobs.
    /// Clear claims for Jobs that no longer exist.
    /// </summary>
    private async Task ReconcileStartupPvcsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var claimedItems = await db.WorkItems
            .Where(w => w.ClaimedPvcName != null)
            .Select(w => new { w.Id, w.K8sJobName, w.ClaimedPvcName })
            .ToListAsync(ct);

        if (claimedItems.Count == 0) return;

        foreach (var item in claimedItems)
        {
            if (string.IsNullOrEmpty(item.K8sJobName))
            {
                // No job name but has PVC claim — release
                await ClearPvcClaimAsync(item.Id, ct);
                continue;
            }

            if (!await JobExistsAsync(item.K8sJobName, ct))
            {
                Log.Information(
                    "ReconciliationService: startup PVC release — Job {JobName} no longer exists, clearing PVC {Pvc} from WorkItem {WorkItemId}",
                    item.K8sJobName, item.ClaimedPvcName, item.Id);
                await ClearPvcClaimAsync(item.Id, ct);
            }
        }
    }

    /// <summary>
    /// Issues with in-progress labels but no matching non-terminal work item → swap to agent:next.
    /// Only re-queues Succeeded items (label recovery) and Failed items with retryable failure reasons
    /// (Timeout, InfrastructureFailure). Cancelled items and permanently-failed items are excluded.
    /// </summary>
    private async Task ReconcileStartupLabelsAsync(CancellationToken ct)
    {
        if (_labelService is null) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Get all non-terminal work items with their issue identifiers
        var activeIssues = await db.WorkItems
            .Where(w => w.Status != WorkItemStatus.Succeeded &&
                        w.Status != WorkItemStatus.Failed &&
                        w.Status != WorkItemStatus.Cancelled)
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
            .ToListAsync(ct);

        var activeSet = activeIssues
            .Select(x => (x.IssueIdentifier, x.IssueProviderConfigId))
            .ToHashSet();

        // Get recently terminal items that might still have in-progress labels.
        // Excludes: Cancelled (user-intentional), Failed with permanent/unknown reasons.
        // Includes: Succeeded (label recovery), Failed with retryable reasons (Timeout, InfrastructureFailure).
        var recentTerminal = await db.WorkItems
            .Where(w => w.CompletedAt > DateTimeOffset.UtcNow.AddMinutes(-5) &&
                        (w.Status == WorkItemStatus.Succeeded ||
                         (w.Status == WorkItemStatus.Failed &&
                          (w.FailureReason == FailureReason.Timeout ||
                           w.FailureReason == FailureReason.InfrastructureFailure))))
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
            .ToListAsync(ct);

        foreach (var item in recentTerminal)
        {
            if (activeSet.Contains((item.IssueIdentifier, item.IssueProviderConfigId)))
                continue; // Still has an active work item

            try
            {
                await _labelService.SwapLabelAsync(
                    item.IssueProviderConfigId, item.IssueIdentifier, AgentLabels.Next, ct);
                Log.Information(
                    "ReconciliationService: startup label reconciliation — swapped to agent:next for {Issue}",
                    item.IssueIdentifier);
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "ReconciliationService: failed startup label swap for {Issue}",
                    item.IssueIdentifier);
            }
        }
    }
}
