using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using k8s.Models;
using Serilog;

namespace CodingAgentWebUI.JobController.Reconciliation;

/// <summary>
/// Core reconciliation logic for the Job Controller.
/// Called periodically by <see cref="ReconciliationService"/> to keep K8s Job state and
/// WorkItem state in sync. Methods are public so they can be tested independently.
/// </summary>
public sealed class ReconciliationLoop
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<ReconciliationLoop>();

    private readonly IPipelineApiWorkItemClient _workItemClient;
    private readonly IKubernetesJobClient _k8sClient;
    private readonly PvcPool _pvcPool;
    private readonly DispatchServiceOptions _options;

    public ReconciliationLoop(
        IPipelineApiWorkItemClient workItemClient,
        IKubernetesJobClient k8sClient,
        PvcPool pvcPool,
        DispatchServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(workItemClient);
        ArgumentNullException.ThrowIfNull(k8sClient);
        ArgumentNullException.ThrowIfNull(pvcPool);
        ArgumentNullException.ThrowIfNull(options);
        _workItemClient = workItemClient;
        _k8sClient = k8sClient;
        _pvcPool = pvcPool;
        _options = options;
    }

    /// <summary>
    /// Full reconciliation cycle:
    /// 1. List all managed K8s Jobs
    /// 2. For each completed/failed job: post status update and release PVC
    /// </summary>
    public async Task ReconcileOnceAsync(CancellationToken ct)
    {
        V1JobList jobs;
        try
        {
            jobs = await _k8sClient.ListJobsAsync(
                _options.Namespace,
                "app.kubernetes.io/managed-by=caa-orchestrator",
                ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to list K8s Jobs; skipping reconciliation cycle");
            return;
        }

        foreach (var job in jobs.Items)
        {
            if (ct.IsCancellationRequested) break;
            await HandleJobAsync(job, ct);
        }
    }

    /// <summary>
    /// Enforces the session timeout: marks Running items older than
    /// <see cref="DispatchServiceOptions.ChatSessionMaxDurationSeconds"/> as Failed.
    /// </summary>
    public async Task EnforceTimeoutsAsync(CancellationToken ct)
    {
        IReadOnlyList<ActiveWorkItemDto> timedOut;
        try
        {
            timedOut = await _workItemClient.GetActiveAsync(_options.ChatSessionMaxDurationSeconds, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to query active work items for timeout enforcement");
            return;
        }

        foreach (var item in timedOut)
        {
            if (ct.IsCancellationRequested) break;

            // Only time out Running items here; Dispatched items are handled by EnforceDispatchedTimeoutAsync
            if (item.Status != WorkItemStatus.Running) continue;

            Log.Warning("WorkItem {Id} timed out after {Seconds}s, marking Failed", item.Id, _options.ChatSessionMaxDurationSeconds);

            try
            {
                await _workItemClient.PostStatusAsync(item.Id, new WorkItemStatusUpdate
                {
                    Status = "Failed",
                    ErrorMessage = $"Agent timeout after {_options.ChatSessionMaxDurationSeconds}s",
                    FailureReason = "Timeout"
                }, ct);

                WorkDistributionTelemetry.LogTerminalStatus(
                    item.Id, WorkItemStatus.Failed,
                    duration: null, agentId: DispatchLoop.GenerateJobName(item.Id),
                    failureReason: FailureReason.Timeout);

                var jobName = DispatchLoop.GenerateJobName(item.Id);
                await SafeDeleteJobAsync(jobName, ct);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to process timeout for WorkItem {Id}", item.Id);
            }
        }
    }

    /// <summary>
    /// Short-circuit sweep: marks Dispatched items older than
    /// <see cref="DispatchServiceOptions.ChatPodConnectTimeoutSeconds"/> as Failed
    /// when no K8s Job exists for them. This recovers items where ClaimAsync succeeded
    /// but Job creation failed AND RequeueAsync also failed.
    /// </summary>
    public async Task EnforceDispatchedTimeoutAsync(CancellationToken ct)
    {
        IReadOnlyList<ActiveWorkItemDto> items;
        try
        {
            items = await _workItemClient.GetActiveAsync(_options.ChatPodConnectTimeoutSeconds, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to query active work items for dispatched timeout enforcement");
            return;
        }

        // Filter client-side: only Dispatched items (no K8s Job yet)
        var dispatched = items.Where(i => i.Status == WorkItemStatus.Dispatched).ToList();
        if (dispatched.Count == 0) return;

        // Build set of known job names from live K8s Jobs
        HashSet<string> liveJobNames;
        try
        {
            var jobs = await _k8sClient.ListJobsAsync(
                _options.Namespace,
                "app.kubernetes.io/managed-by=caa-orchestrator",
                ct);
            liveJobNames = jobs.Items.Select(j => j.Metadata?.Name ?? "").ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to list Jobs for dispatched timeout check; skipping");
            return;
        }

        foreach (var item in dispatched)
        {
            if (ct.IsCancellationRequested) break;

            var expectedJobName = DispatchLoop.GenerateJobName(item.Id);
            if (liveJobNames.Contains(expectedJobName)) continue; // job exists, not orphaned

            Log.Warning("WorkItem {Id} stuck in Dispatched for >{Seconds}s with no K8s Job — marking Failed",
                item.Id, _options.ChatPodConnectTimeoutSeconds);

            try
            {
                await _workItemClient.PostStatusAsync(item.Id, new WorkItemStatusUpdate
                {
                    Status = "Failed",
                    ErrorMessage = $"No K8s Job created within {_options.ChatPodConnectTimeoutSeconds}s of dispatch",
                    FailureReason = "DispatchTimeout"
                }, ct);

                WorkDistributionTelemetry.LogTerminalStatus(
                    item.Id, WorkItemStatus.Failed,
                    duration: null, agentId: null,
                    failureReason: FailureReason.Timeout);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to post Failed status for orphaned Dispatched WorkItem {Id}", item.Id);
            }
        }
    }

    /// <summary>
    /// Orphan cleanup: deletes K8s Jobs that have no matching active WorkItem.
    /// This handles stale terminal jobs and any other orphaned jobs.
    /// Does NOT post a status update (work item is already in terminal state or never existed).
    /// </summary>
    public async Task CleanupOrphansAsync(CancellationToken ct)
    {
        V1JobList jobs;
        IReadOnlyList<ActiveWorkItemDto> activeItems;

        try
        {
            jobs = await _k8sClient.ListJobsAsync(
                _options.Namespace,
                "app.kubernetes.io/managed-by=caa-orchestrator",
                ct);
            // Pass olderThanSeconds=0 to get ALL active (Dispatched+Running) work items regardless of age.
            // This builds the "active" set used to exclude Jobs from orphan deletion — we never want to
            // delete a Job that has a corresponding active WorkItem, even a brand-new one.
            activeItems = await _workItemClient.GetActiveAsync(0, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch data for orphan cleanup; skipping");
            return;
        }

        var activeIds = activeItems.Select(i => i.Id).ToHashSet();

        foreach (var job in jobs.Items)
        {
            if (ct.IsCancellationRequested) break;

            var workItemId = ParseWorkItemId(job);
            if (workItemId.HasValue && activeIds.Contains(workItemId.Value))
                continue; // job has a live work item — skip

            var jobName = job.Metadata?.Name;
            if (string.IsNullOrEmpty(jobName)) continue;

            Log.Information("Deleting orphan/stale K8s Job {JobName}", jobName);
            await SafeDeleteJobAsync(jobName, ct);
        }
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private async Task HandleJobAsync(V1Job job, CancellationToken ct)
    {
        var workItemId = ParseWorkItemId(job);
        if (!workItemId.HasValue) return;

        var phase = GetJobPhase(job);
        var jobName = job.Metadata?.Name ?? "";

        switch (phase)
        {
            case "Succeeded":
                await HandleJobCompletedAsync(workItemId.Value, job, "Succeeded", null, null, ct);
                break;
            case "Failed":
                var errorMsg = GetFailureMessage(job);
                await HandleJobCompletedAsync(workItemId.Value, job, "Failed", "AgentError", errorMsg, ct);
                break;
            // Active/Unknown/Pending — no action needed
        }
    }

    private async Task HandleJobCompletedAsync(
        Guid workItemId,
        V1Job job,
        string status,
        string? failureReason,
        string? errorMessage,
        CancellationToken ct)
    {
        try
        {
            await _workItemClient.PostStatusAsync(workItemId, new WorkItemStatusUpdate
            {
                Status = status,
                FailureReason = failureReason,
                ErrorMessage = errorMessage
            }, ct);

            // Release PVC if any
            var pvcName = GetPvcFromJob(job);
            if (pvcName is not null)
                _pvcPool.Release(pvcName);

            // Record terminal metrics
            var workItemStatus = status == "Succeeded" ? WorkItemStatus.Succeeded : WorkItemStatus.Failed;
            var failureReasonEnum = failureReason == "AgentError" ? (FailureReason?)FailureReason.AgentError : null;
            var dispatchedAt = job.Status?.StartTime is not null
                ? new DateTimeOffset(job.Status.StartTime.Value, TimeSpan.Zero)
                : (DateTimeOffset?)null;
            var completedAt = job.Status?.CompletionTime is not null
                ? new DateTimeOffset(job.Status.CompletionTime.Value, TimeSpan.Zero)
                : DateTimeOffset.UtcNow;
            var duration = dispatchedAt.HasValue ? completedAt - dispatchedAt.Value : (TimeSpan?)null;
            var agentId = job.Metadata?.Name;
            WorkDistributionTelemetry.LogTerminalStatus(workItemId, workItemStatus, duration, agentId, failureReasonEnum);

            Log.Information("WorkItem {Id} marked {Status} from K8s Job {Job}", workItemId, status, job.Metadata?.Name);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to post status {Status} for WorkItem {Id}", status, workItemId);
        }
    }

    private async Task SafeDeleteJobAsync(string jobName, CancellationToken ct)
    {
        try
        {
            await _k8sClient.DeleteJobAsync(jobName, _options.Namespace, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete K8s Job {JobName}", jobName);
        }
    }

    private static Guid? ParseWorkItemId(V1Job job)
    {
        var labels = job.Metadata?.Labels;
        if (labels is null || !labels.TryGetValue("caa/work-item-id", out var idStr))
            return null;

        return Guid.TryParse(idStr, out var id) ? id : null;
    }

    private static string GetJobPhase(V1Job job)
    {
        var conditions = job.Status?.Conditions;
        if (conditions is not null)
        {
            if (conditions.Any(c => c.Type == "Complete" && c.Status == "True"))
                return "Succeeded";
            if (conditions.Any(c => c.Type == "Failed" && c.Status == "True"))
                return "Failed";
        }

        // Fall back to counters
        if (job.Status?.Succeeded > 0) return "Succeeded";
        if (job.Status?.Failed > 0) return "Failed";
        return "Active";
    }

    private static string? GetPvcFromJob(V1Job job)
    {
        return job.Spec?.Template?.Spec?.Volumes?
            .FirstOrDefault(v => v.PersistentVolumeClaim?.ClaimName is not null)
            ?.PersistentVolumeClaim?.ClaimName;
    }

    private static string? GetFailureMessage(V1Job job)
    {
        var conditions = job.Status?.Conditions;
        var failedCondition = conditions?.FirstOrDefault(c => c.Type == "Failed" && c.Status == "True");
        return failedCondition?.Message;
    }
}
