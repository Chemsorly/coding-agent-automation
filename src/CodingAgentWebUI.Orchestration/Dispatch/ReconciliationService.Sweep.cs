using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

public sealed partial class ReconciliationService
{
    // ── Orphan Detection ─────────────────────────────────────────────────

    /// <summary>
    /// Dispatched/Running with no matching K8s Job → Failed (InfrastructureFailure).
    /// </summary>
    private async Task DetectOrphansAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var activeItems = await db.WorkItems
            .WhereActive()
            .Where(w => w.K8sJobName != null)
            .Select(w => new { w.Id, w.K8sJobName })
            .ToListAsync(ct);

        if (activeItems.Count == 0) return;

        // List current K8s Jobs — null means API unreachable, skip orphan detection this cycle
        var existingJobs = await GetExistingJobNamesAsync(ct);
        if (existingJobs is null) return;

        foreach (var item in activeItems)
        {
            if (ct.IsCancellationRequested) break;

            if (!existingJobs.Contains(item.K8sJobName!))
            {
                Log.Warning("ReconciliationService: orphan detected — WorkItem {WorkItemId} has no K8s Job {JobName}",
                    item.Id, item.K8sJobName);

                await _transitionService.TransitionAsync(item.Id, WorkItemStatus.Failed,
                    w =>
                    {
                        w.CompletedAt = DateTimeOffset.UtcNow;
                        w.FailureReason = FailureReason.InfrastructureFailure;
                        w.ErrorMessage = $"K8s Job '{item.K8sJobName}' no longer exists (orphan)";
                    }, ct: ct);

                LogTerminalTransition(item.Id, WorkItemStatus.Failed, FailureReason.InfrastructureFailure);
            }
        }
    }

    // ── Completed Job + Stuck WorkItem Detection (Safety Net) ──────────

    /// <summary>
    /// Poll-based safety net for #1138: detects K8s Jobs that have reached Complete status
    /// but whose WorkItems remain in Dispatched/Running (agent never reported terminal status).
    /// Applies the same 30s grace period as the Watch handler.
    /// Covers missed Watch events (API server disconnect, 410 Gone during the event window).
    /// </summary>
    internal async Task DetectCompletedJobsWithStuckWorkItemsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Find non-terminal WorkItems that have K8s Job names
        var activeItems = await db.WorkItems
            .WhereActive()
            .Where(w => w.K8sJobName != null)
            .Select(w => new { w.Id, w.K8sJobName })
            .ToListAsync(ct);

        if (activeItems.Count == 0) return;

        var completedJobs = await ListCompletedJobsDictionaryAsync(ct);
        if (completedJobs is null || completedJobs.Count == 0) return;

        var gracePeriod = TimeSpan.FromSeconds(CompleteJobGracePeriodSeconds);
        var now = DateTimeOffset.UtcNow;

        foreach (var item in activeItems)
        {
            if (ct.IsCancellationRequested) break;

            if (!completedJobs.TryGetValue(item.K8sJobName!, out var job))
                continue; // Job not in Complete state — skip

            var completionTime = job.Status?.CompletionTime;
            if (completionTime is null || now - completionTime.Value <= gracePeriod)
                continue; // Still within grace period

            Log.Warning(
                "ReconciliationService: [poll] Job {JobName} completed at {CompletionTime} but WorkItem {WorkItemId} still non-terminal — failing",
                item.K8sJobName, completionTime, item.Id);

            await FailStuckWorkItemAsync(item.Id, item.K8sJobName!, ct);
        }
    }

    private async Task<Dictionary<string, V1Job>?> ListCompletedJobsDictionaryAsync(CancellationToken ct)
    {
        V1JobList? jobList;
        try
        {
            jobList = await _kubeClient.BatchV1.ListNamespacedJobAsync(
                _options.Namespace,
                labelSelector: $"{ManagedByLabel}={ManagedByValue}",
                cancellationToken: ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Error(ex, "ReconciliationService: failed to list K8s Jobs for stuck WorkItem detection");
            return null;
        }

        if (jobList?.Items is null) return null;

        var completedJobCandidates = jobList.Items
            .Where(j => j.Metadata?.Name is not null &&
                        j.Status?.Conditions?.Any(c => c.Type == "Complete" && c.Status == "True") == true)
            .ToList();

        var completedJobs = completedJobCandidates
            .DistinctBy(j => j.Metadata!.Name)
            .ToDictionary(j => j.Metadata!.Name, StringComparer.Ordinal);

        if (completedJobs.Count != completedJobCandidates.Count)
            Log.Warning(
                "ReconciliationService: K8s API returned duplicate job names ({Total} jobs, {Unique} unique) — using first occurrence",
                completedJobCandidates.Count, completedJobs.Count);

        return completedJobs;
    }

    private async Task FailStuckWorkItemAsync(Guid workItemId, string jobName, CancellationToken ct)
    {
        // TODO: Check return value of TransitionAsync — if false (item already transitioned), skip cleanup for efficiency and log as no-op.
        await _transitionService.TransitionAsync(workItemId, WorkItemStatus.Failed,
            entity =>
            {
                entity.CompletedAt = DateTimeOffset.UtcNow;
                entity.FailureReason = FailureReason.InfrastructureFailure;
                entity.ErrorMessage = "K8s Job completed (exit 0) but agent never reported terminal status — likely startup crash or POST failure";
            }, ct: ct);

        LogTerminalTransition(workItemId, WorkItemStatus.Failed, FailureReason.InfrastructureFailure);

        // Release PVC and delete Job
        await ReleasePvcForWorkItemAsync(workItemId, ct);
        if (!string.IsNullOrEmpty(jobName))
        {
            await TryDeleteJobAsync(jobName, ct);
        }
    }

    // ── Stale Cleanup ────────────────────────────────────────────────────

    /// <summary>
    /// Terminal items older than retention period → DELETE from WorkItems.
    /// Uses server-side delete to avoid loading entities into memory.
    /// </summary>
    internal async Task CleanupStaleWorkItemsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.StaleRetentionDays);

        var deletedCount = await db.WorkItems
            .Where(w => (w.Status == WorkItemStatus.Succeeded ||
                         w.Status == WorkItemStatus.Failed ||
                         w.Status == WorkItemStatus.Cancelled) &&
                        w.CompletedAt != null &&
                        w.CompletedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deletedCount > 0)
        {
            Log.Information("ReconciliationService: cleaned up {Count} stale work items (retention={Days}d)",
                deletedCount, _options.StaleRetentionDays);
        }
    }

    /// <summary>
    /// Determines whether a terminal work item is stale based on CompletedAt and retention period.
    /// Exposed as internal static for unit testing.
    /// </summary>
    internal static bool IsStale(DateTimeOffset? completedAt, int retentionDays, DateTimeOffset now)
    {
        if (completedAt is null) return false;
        return now >= completedAt.Value.AddDays(retentionDays);
    }

    /// <summary>
    /// PipelineRuns older than retention period → DELETE.
    /// Uses server-side delete to avoid loading entities into memory.
    /// </summary>
    private async Task CleanupStalePipelineRunsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.PipelineRunRetentionDays);

        var deletedCount = await db.PipelineRuns
            .Where(r => r.CompletedAt != null && r.CompletedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deletedCount > 0)
        {
            Log.Information("ReconciliationService: cleaned up {Count} stale pipeline runs (retention={Days}d)",
                deletedCount, _options.PipelineRunRetentionDays);
        }
    }

    /// <summary>
    /// Terminal ConsolidationRuns older than retention period → DELETE via IConsolidationService.
    /// Uses client-side filtering because CompletedAtUtc is stored inside JSONB (no server-side filter).
    /// </summary>
    private async Task CleanupStaleConsolidationRunsAsync(CancellationToken ct)
    {
        if (_consolidationService is null) return;

        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.ConsolidationRunRetentionDays);
            var runs = await _consolidationService.GetRunHistoryAsync(ct);
            var deletedCount = 0;

            foreach (var run in runs)
            {
                if (ct.IsCancellationRequested) break;

                // Only delete terminal runs
                if (run.Status is not (ConsolidationRunStatus.Succeeded or ConsolidationRunStatus.Failed or ConsolidationRunStatus.Cancelled))
                    continue;

                // Use CompletedAtUtc if available, fall back to StartedAtUtc
                var anchor = run.CompletedAtUtc ?? run.StartedAtUtc;
                if (anchor >= cutoff)
                    continue;

                await _consolidationService.DeleteRunAsync(run.RunId, ct);
                deletedCount++;
            }

            if (deletedCount > 0)
            {
                Log.Information("ReconciliationService: cleaned up {Count} stale consolidation runs (retention={Days}d)",
                    deletedCount, _options.ConsolidationRunRetentionDays);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning(ex, "ReconciliationService: failed to cleanup stale consolidation runs (non-fatal)");
        }
    }

    // ── Pod Startup Failure Detection ────────────────────────────────────

    /// <summary>
    /// Dispatched >60s, pod not Ready → log warning.
    /// </summary>
    private async Task DetectPodStartupFailuresAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var warningThreshold = TimeSpan.FromSeconds(_options.PodStartupWarningSeconds);

        var dispatchedItems = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Dispatched &&
                        w.DispatchedAt != null &&
                        w.K8sJobName != null)
            .Select(w => new { w.Id, w.K8sJobName, w.DispatchedAt })
            .ToListAsync(ct);

        foreach (var item in dispatchedItems)
        {
            if (ct.IsCancellationRequested) break;
            if (now - item.DispatchedAt!.Value < warningThreshold) continue;

            try
            {
                var pods = await _kubeClient.CoreV1.ListNamespacedPodAsync(
                    _options.Namespace,
                    labelSelector: $"job-name={item.K8sJobName}",
                    cancellationToken: ct);

                var pod = pods.Items.FirstOrDefault();
                if (pod is null)
                {
                    Log.Warning(
                        "ReconciliationService: WorkItem {WorkItemId} dispatched >{Threshold}s but no pod found for Job {JobName}",
                        item.Id, _options.PodStartupWarningSeconds, item.K8sJobName);
                    continue;
                }

                var isReady = pod.Status?.Conditions?.Any(c =>
                    c.Type == "Ready" && c.Status == "True") ?? false;

                if (!isReady)
                {
                    var containerStatuses = pod.Status?.ContainerStatuses;
                    var waitingReason = containerStatuses?.FirstOrDefault()?.State?.Waiting?.Reason ?? "Unknown";

                    Log.Warning(
                        "ReconciliationService: WorkItem {WorkItemId} dispatched >{Threshold}s, pod not Ready (reason={Reason})",
                        item.Id, _options.PodStartupWarningSeconds, waitingReason);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Debug(ex, "ReconciliationService: failed pod check for Job {JobName}", item.K8sJobName);
            }
        }
    }
}
