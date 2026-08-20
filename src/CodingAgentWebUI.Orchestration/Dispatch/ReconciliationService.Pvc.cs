using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Pipeline.Models;
using k8s;
using k8s.Autorest;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

public sealed partial class ReconciliationService
{
    // ── PVC Release ──────────────────────────────────────────────────────

    /// <summary>
    /// When a K8s Job is confirmed deleted, clear ClaimedPvcName on the associated WorkItem.
    /// Do NOT release on terminal status alone — pod may still be mounted.
    /// </summary>
    private async Task ReleasePvcForWorkItemAsync(Guid workItemId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var item = await db.WorkItems.FindAsync([workItemId], ct);
        if (item is null || item.ClaimedPvcName is null) return;

        var pvc = item.ClaimedPvcName;
        item.ClaimedPvcName = null;

        try
        {
            await db.SaveChangesAsync(ct);
            Log.Information("ReconciliationService: released PVC {Pvc} from WorkItem {WorkItemId}",
                pvc, workItemId);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Log.Warning(ex, "ReconciliationService: concurrency conflict releasing PVC for WorkItem {WorkItemId}",
                workItemId);
        }
    }

    /// <summary>
    /// During poll: verify claimed PVCs by checking if their Jobs still exist.
    /// Also handles the crash-recovery case: Pending items with ClaimedPvcName but no K8s Job
    /// (crash between DB write and Job creation leaves stale claims).
    /// </summary>
    private async Task ReconcilePvcsFromPollAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Case 1: Terminal items with claimed PVCs whose Jobs no longer exist
        var terminalClaimedItems = await db.WorkItems
            .Where(w => w.ClaimedPvcName != null && w.K8sJobName != null &&
                        (w.Status == WorkItemStatus.Succeeded ||
                         w.Status == WorkItemStatus.Failed ||
                         w.Status == WorkItemStatus.Cancelled))
            .Select(w => new { w.Id, w.K8sJobName })
            .ToListAsync(ct);

        // Case 2: Pending items with stale PVC claims (crash between DB write and Job creation)
        var pendingWithStaleClaims = await db.WorkItems
            .Where(w => w.ClaimedPvcName != null &&
                        w.Status == WorkItemStatus.Pending &&
                        w.K8sJobName != null)
            .Select(w => new { w.Id, w.K8sJobName })
            .ToListAsync(ct);

        var allItemsToCheck = terminalClaimedItems.Concat(pendingWithStaleClaims).ToList();
        if (allItemsToCheck.Count == 0) return;

        var existingJobs = await GetExistingJobNamesAsync(ct);
        if (existingJobs is null) return;

        foreach (var item in allItemsToCheck)
        {
            if (ct.IsCancellationRequested) break;

            if (!existingJobs.Contains(item.K8sJobName!))
            {
                await ReleasePvcForWorkItemAsync(item.Id, ct);
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<HashSet<string>?> GetExistingJobNamesAsync(CancellationToken ct)
    {
        try
        {
            var jobList = await _kubeClient.BatchV1.ListNamespacedJobAsync(
                _options.Namespace,
                labelSelector: $"{ManagedByLabel}={ManagedByValue}",
                cancellationToken: ct);

            return jobList.Items?
                .Where(j => j.Metadata?.Name is not null)
                .Select(j => j.Metadata.Name)
                .ToHashSet(StringComparer.Ordinal) ?? [];
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Error(ex, "ReconciliationService: failed to list K8s Jobs");
            return null;
        }
    }

    private async Task<bool> JobExistsAsync(string jobName, CancellationToken ct)
    {
        try
        {
            await _kubeClient.BatchV1.ReadNamespacedJobAsync(jobName, _options.Namespace, cancellationToken: ct);
            return true;
        }
        catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning(ex, "ReconciliationService: error checking Job existence for {JobName}", jobName);
            return true; // Assume exists on error to avoid false orphan detection
        }
    }

    private async Task TryDeleteJobAsync(string jobName, CancellationToken ct)
    {
        try
        {
            await _kubeClient.BatchV1.DeleteNamespacedJobAsync(
                jobName, _options.Namespace,
                propagationPolicy: "Background",
                cancellationToken: ct);

            Log.Information("ReconciliationService: deleted K8s Job {JobName}", jobName);
        }
        catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already deleted
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning(ex, "ReconciliationService: failed to delete Job {JobName}", jobName);
        }
    }

    private async Task ClearPvcClaimAsync(Guid workItemId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems.FindAsync([workItemId], ct);
        if (item is null || item.ClaimedPvcName is null) return;

        item.ClaimedPvcName = null;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Ignore — another writer cleared it
        }
    }

    private static void LogTerminalTransition(Guid workItemId, WorkItemStatus status, FailureReason? reason,
        DateTimeOffset? dispatchedAt = null, string? agentId = null)
    {
        var duration = dispatchedAt.HasValue
            ? DateTimeOffset.UtcNow - dispatchedAt.Value
            : (TimeSpan?)null;

        WorkDistributionTelemetry.LogTerminalStatus(workItemId, status, duration, agentId, reason);
    }
}
