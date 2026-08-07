using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

public sealed partial class ReconciliationService
{
    // ── K8s Job Watch Loop ───────────────────────────────────────────────

    private async Task RunWatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && LeaderElection.IsLeader)
        {
            try
            {
                await WatchJobsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                // 410 Gone: resourceVersion too old — re-list to rebuild state
                Log.Warning(httpEx, "ReconciliationService: Watch got 410 Gone, performing full re-list");
                _lastResourceVersion = null;
                await RelistJobsAsync(ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ReconciliationService: Watch disconnected, reconnecting in 1s");
            }

            if (!ct.IsCancellationRequested && LeaderElection.IsLeader)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task WatchJobsAsync(CancellationToken ct)
    {
        var watchStartTime = DateTimeOffset.UtcNow;
        var reconnectInterval = TimeSpan.FromMinutes(_options.WatchReconnectIntervalMinutes);

        var response = _kubeClient.BatchV1.ListNamespacedJobWithHttpMessagesAsync(
            _options.Namespace,
            labelSelector: $"{ManagedByLabel}={ManagedByValue}",
            resourceVersion: _lastResourceVersion,
            watch: true,
            cancellationToken: ct);

        var watchEnumerable = response.WatchAsync<V1Job, V1JobList>(
            onError: ex => Log.Warning(ex, "ReconciliationService: Watch stream error"),
            cancellationToken: ct);

        await foreach (var (type, job) in watchEnumerable.WithCancellation(ct))
        {
            // Track resourceVersion from each event
            if (job.Metadata?.ResourceVersion is not null)
                _lastResourceVersion = job.Metadata.ResourceVersion;

            await HandleJobEventAsync(type, job, ct);

            // Proactive reconnect every N minutes
            if (DateTimeOffset.UtcNow - watchStartTime > reconnectInterval)
            {
                Log.Debug("ReconciliationService: proactive Watch reconnect after {Minutes}m",
                    _options.WatchReconnectIntervalMinutes);
                break;
            }
        }
    }

    private async Task HandleJobEventAsync(WatchEventType type, V1Job job, CancellationToken ct)
    {
        var workItemIdStr = job.Metadata?.Labels is not null &&
            job.Metadata.Labels.TryGetValue(WorkItemIdLabel, out var labelVal) ? labelVal : null;
        if (workItemIdStr is null || !Guid.TryParse(workItemIdStr, out var workItemId))
            return;

        switch (type)
        {
            case WatchEventType.Modified:
                await HandleJobCompletionAsync(workItemId, job, ct);
                break;

            case WatchEventType.Deleted:
                // Job deleted (TTL controller or manual) — release PVC
                await ReleasePvcForWorkItemAsync(workItemId, ct);
                break;
        }
    }

    private async Task HandleJobCompletionAsync(Guid workItemId, V1Job job, CancellationToken ct)
    {
        if (job.Status is null) return;

        var isComplete = job.Status.Conditions?.Any(c =>
            c.Type == "Complete" && c.Status == "True") ?? false;
        var isFailed = job.Status.Conditions?.Any(c =>
            c.Type == "Failed" && c.Status == "True") ?? false;

        if (!isComplete && !isFailed) return;

        if (isFailed)
        {
            var reason = job.Status.Conditions?
                .FirstOrDefault(c => c.Type == "Failed")?.Reason ?? "Unknown";

            await _transitionService.TransitionAsync(workItemId, WorkItemStatus.Failed,
                item =>
                {
                    item.CompletedAt = DateTimeOffset.UtcNow;
                    item.FailureReason = FailureReason.InfrastructureFailure;
                    item.ErrorMessage = $"K8s Job failed: {reason}";
                }, ct: ct);

            LogTerminalTransition(workItemId, WorkItemStatus.Failed, FailureReason.InfrastructureFailure);

            // Cascade failure to ConsolidationRun if this is a consolidation WorkItem
            await CascadeConsolidationRunFailureIfApplicableAsync(workItemId, $"K8s Job failed: {reason}", ct);

            // Delete the failed Job immediately to release PVC faster (don't wait for TTL controller).
            // Without this, the PVC stays claimed for up to TtlSecondsAfterFinished (default 3600s).
            var jobName = job.Metadata?.Name;
            if (!string.IsNullOrEmpty(jobName))
            {
                await TryDeleteJobAsync(jobName, ct);
            }
        }
        else
        {
            // Job completed (exit 0) — verify the agent actually reported terminal status.
            // Grace period: allow 30s for the POST to arrive (network latency, agent shutdown sequence).
            // Note: isComplete is always true here — the guard `if (!isComplete && !isFailed) return`
            // above ensures at least one is true, and this else branch is reached only when isFailed == false.
            await HandleCompleteJobWithStuckWorkItemAsync(workItemId, job, ct);
        }
    }

    /// <summary>
    /// Cascades a failure to a ConsolidationRun if the WorkItem is a consolidation item.
    /// Looks up TaskType from the DB, then delegates to IConsolidationService.UpdateRunAsync.
    /// No-op for non-consolidation items or when IConsolidationService is not injected.
    /// </summary>
    private async Task CascadeConsolidationRunFailureIfApplicableAsync(Guid workItemId, string errorMessage, CancellationToken ct)
    {
        if (_consolidationService is null)
            return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var workItem = await db.WorkItems
                .AsNoTracking()
                .Where(w => w.Id == workItemId)
                .Select(w => new { w.TaskType, w.IssueIdentifier })
                .FirstOrDefaultAsync(ct);

            if (workItem?.TaskType != WorkItemTaskType.Consolidation || workItem.IssueIdentifier is null)
                return;

            await _consolidationService.UpdateRunAsync(
                workItem.IssueIdentifier,
                ConsolidationRunStatus.Failed,
                $"K8s Job failed (detected by reconciliation): {errorMessage}",
                ct);

            Log.Information("ReconciliationService: cascaded K8s Job failure to ConsolidationRun {RunId}", workItem.IssueIdentifier);
        }
        catch (OperationCanceledException ex)
        {
            Log.Debug(ex, "ReconciliationService: cascade to ConsolidationRun for WorkItem {WorkItemId} cancelled (shutdown)", workItemId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReconciliationService: failed to cascade failure to ConsolidationRun for WorkItem {WorkItemId} (non-fatal)", workItemId);
        }
    }

    /// <summary>
    /// When a K8s Job reaches Complete status, verifies the WorkItem has reached a terminal state.
    /// If still Dispatched/Running after the grace period, transitions to Failed (agent never reported back).
    /// </summary>
    private async Task HandleCompleteJobWithStuckWorkItemAsync(Guid workItemId, V1Job job, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == workItemId)
            .Select(w => new { w.Status, w.CompletedAt })
            .FirstOrDefaultAsync(ct);

        if (item is null) return; // Already cleaned up

        if (item.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
            return; // Agent reported correctly — nothing to do

        // WorkItem still non-terminal after Job completed — check grace period
        var jobCompletionTime = job.Status?.CompletionTime;
        var gracePeriod = TimeSpan.FromSeconds(CompleteJobGracePeriodSeconds);

        if (jobCompletionTime is null || DateTimeOffset.UtcNow - jobCompletionTime.Value <= gracePeriod)
            return; // Still within grace period — POST may still arrive

        Log.Warning(
            "ReconciliationService: Job {JobName} completed but WorkItem {WorkItemId} still in {Status} — agent never reported terminal status",
            job.Metadata?.Name, workItemId, item.Status);

        // TODO: Check return value of TransitionAsync — if false (e.g., item already transitioned by Watch handler), skip cleanup below for efficiency.
        await _transitionService.TransitionAsync(workItemId, WorkItemStatus.Failed,
            entity =>
            {
                entity.CompletedAt = DateTimeOffset.UtcNow;
                entity.FailureReason = FailureReason.InfrastructureFailure;
                entity.ErrorMessage = "K8s Job completed (exit 0) but agent never reported terminal status — likely startup crash or POST failure";
            }, ct: ct);

        LogTerminalTransition(workItemId, WorkItemStatus.Failed, FailureReason.InfrastructureFailure);

        // Release PVC and delete Job (same cleanup as isFailed path)
        await ReleasePvcForWorkItemAsync(workItemId, ct);
        if (!string.IsNullOrEmpty(job.Metadata?.Name))
        {
            await TryDeleteJobAsync(job.Metadata.Name, ct);
        }
    }

    /// <summary>
    /// Performs a full re-list to rebuild state after 410 Gone.
    /// Updates _lastResourceVersion from the list response.
    /// </summary>
    private async Task RelistJobsAsync(CancellationToken ct)
    {
        try
        {
            var jobList = await _kubeClient.BatchV1.ListNamespacedJobAsync(
                _options.Namespace,
                labelSelector: $"{ManagedByLabel}={ManagedByValue}",
                cancellationToken: ct);

            _lastResourceVersion = jobList.Metadata?.ResourceVersion;
            Log.Information("ReconciliationService: re-list complete, {Count} Jobs, resourceVersion={RV}",
                jobList.Items?.Count ?? 0, _lastResourceVersion);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Error(ex, "ReconciliationService: re-list failed");
        }
    }
}
