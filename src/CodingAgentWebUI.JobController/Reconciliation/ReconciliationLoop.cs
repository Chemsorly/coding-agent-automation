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

    // Kubernetes Job phases, distinct from WorkItem statuses despite two of them sharing their
    // text. These are read from the Job's own conditions and counters; a WorkItem status is what
    // we post back to the API afterwards. Collapsing the two into one set of constants would tie
    // an external API's vocabulary to ours.
    private const string JobPhaseSucceeded = "Succeeded";
    private const string JobPhaseFailed = "Failed";
    private const string JobPhaseComplete = "Complete"; // the condition type Kubernetes sets on success

    /// <summary>
    /// Minimum execution age (seconds) before timeout is enforced.
    /// Values below this threshold indicate a possible timestamp anchor bug (INV-001).
    /// Canary increments signal that <c>CreatedAt</c> or another wrong anchor is being used
    /// instead of <c>DispatchedAt</c>.
    /// </summary>
    private const int TimeoutCanaryMinAgeSeconds = 60;

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
    /// <see cref="DispatchServiceOptions.AgentJobTimeoutSeconds"/> as Failed.
    /// </summary>
    public async Task EnforceTimeoutsAsync(CancellationToken ct)
    {
        IReadOnlyList<ActiveWorkItemDto> timedOut;
        try
        {
            timedOut = await _workItemClient.GetActiveAsync(_options.AgentJobTimeoutSeconds, ct);
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

            // Compute execution age from DispatchedAt. If DispatchedAt is null (items dispatched before
            // the field was added), fall back to AgentJobTimeoutSeconds — safe to enforce.
            var executionAgeSeconds = item.DispatchedAt.HasValue
                ? (DateTimeOffset.UtcNow - item.DispatchedAt.Value).TotalSeconds
                : _options.AgentJobTimeoutSeconds;

            WorkDistributionTelemetry.TimeoutExecutionAge.Record(executionAgeSeconds,
                new KeyValuePair<string, object?>("agent_selector", item.AgentSelector ?? ""));

            // Canary guard: if age is suspiciously low the timeout anchor is wrong (INV-001).
            // Skip enforcement for this sweep — the item will be re-evaluated next cycle.
            if (executionAgeSeconds < TimeoutCanaryMinAgeSeconds)
            {
                Log.Warning("WorkItem {Id} timeout canary violation: execution age {AgeSeconds:F1}s < {MinAge}s — skipping enforcement",
                    item.Id, executionAgeSeconds, TimeoutCanaryMinAgeSeconds);
                WorkDistributionTelemetry.TimeoutCanaryViolations.Add(1,
                    new KeyValuePair<string, object?>("agent_selector", item.AgentSelector ?? ""));
                continue;
            }

            Log.Warning("WorkItem {Id} timed out (status={Status}, job={K8sJobName}) after {Seconds}s — marking Failed",
                item.Id, item.Status, item.K8sJobName ?? "none", _options.AgentJobTimeoutSeconds);

            try
            {
                await _workItemClient.PostStatusAsync(item.Id, new WorkItemStatusUpdate
                {
                    Status = nameof(WorkItemStatus.Failed),
                    ErrorMessage = $"Agent timeout after {_options.AgentJobTimeoutSeconds}s",
                    FailureReason = "Timeout"
                }, ct);

                // Use the stored K8sJobName when available — the API's DispatchLifecycleService uses
                // a different naming format ("caa-{first8hex}") than the job controller's DispatchLoop
                // ("caa-agent-{first11hex}"). Recomputing via DispatchLoop.GenerateJobName would miss
                // jobs created by the API path (consolidation/brain runs) and leave live pods running.
                var jobName = item.K8sJobName ?? DispatchLoop.GenerateJobName(item.Id);

                WorkDistributionTelemetry.LogTerminalStatus(
                    item.Id, WorkItemStatus.Failed,
                    duration: null, agentId: jobName,
                    failureReason: FailureReason.Timeout);

                WorkDistributionTelemetry.AgentTimeouts.Add(1,
                    new KeyValuePair<string, object?>("agent_selector", item.AgentSelector ?? ""));

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

            // Use the stored K8sJobName when available — the API's DispatchLifecycleService uses
            // a different naming format ("caa-{first8hex}") than the job controller's DispatchLoop
            // ("caa-agent-{first11hex}"). Recomputing via DispatchLoop.GenerateJobName would miss
            // jobs created by the API path (consolidation/brain runs) and kill live pods.
            var expectedJobName = item.K8sJobName ?? DispatchLoop.GenerateJobName(item.Id);
            if (liveJobNames.Contains(expectedJobName)) continue; // job exists, not orphaned

            Log.Warning("WorkItem {Id} stuck in Dispatched for >{Seconds}s with no K8s Job — marking Failed",
                item.Id, _options.ChatPodConnectTimeoutSeconds);

            try
            {
                await _workItemClient.PostStatusAsync(item.Id, new WorkItemStatusUpdate
                {
                    Status = nameof(WorkItemStatus.Failed),
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

            // Chat jobs are managed by ChatJobDispatcher, not by work items.
            // They carry caa/chat-session-id and must never be deleted by orphan cleanup.
            if (job.Metadata?.Labels?.ContainsKey("caa/chat-session-id") == true)
                continue;

            var workItemId = ParseWorkItemId(job);
            if (workItemId.HasValue && activeIds.Contains(workItemId.Value))
                continue; // job has a live work item — skip

            var jobName = job.Metadata?.Name;
            if (string.IsNullOrEmpty(jobName)) continue;

            // Determine orphan reason for the log so debugging doesn't require re-running
            var orphanReason = !workItemId.HasValue
                ? "no caa/work-item-id label"
                : $"workItem {workItemId.Value} not in active set (terminal or missing)";

            // Respect a minimum retention window before deleting terminal jobs.
            // This lets kubectl logs remain readable after a job completes/fails
            // and prevents the orphan sweep from racing with the K8s TTL controller.
            // Only delete if the job finished more than LogRetentionSeconds ago (default 10 min),
            // or if it has no completion time (truly orphaned / never started properly).
            var completionTime = job.Status?.CompletionTime
                ?? job.Status?.StartTime; // fallback: use start time if no completion recorded
            const int LogRetentionSeconds = 600; // 10 minutes
            if (completionTime.HasValue &&
                (DateTimeOffset.UtcNow - new DateTimeOffset(completionTime.Value, TimeSpan.Zero)).TotalSeconds < LogRetentionSeconds)
            {
                Log.Debug("Skipping orphan/stale K8s Job {JobName} — completed {Age}s ago, within {Retention}s retention window",
                    jobName,
                    (int)(DateTimeOffset.UtcNow - new DateTimeOffset(completionTime.Value, TimeSpan.Zero)).TotalSeconds,
                    LogRetentionSeconds);
                continue;
            }

            Log.Information("Deleting orphan/stale K8s Job {JobName} (reason={OrphanReason})", jobName, orphanReason);
            await SafeDeleteJobAsync(jobName, ct);
        }
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private async Task HandleJobAsync(V1Job job, CancellationToken ct)
    {
        var workItemId = ParseWorkItemId(job);
        if (!workItemId.HasValue) return;

        var phase = GetJobPhase(job);

        switch (phase)
        {
            case JobPhaseSucceeded:
                await HandleJobCompletedAsync(workItemId.Value, job, JobPhaseSucceeded, null, null, ct);
                break;
            case JobPhaseFailed:
                var errorMsg = GetFailureMessage(job);
                await HandleJobCompletedAsync(workItemId.Value, job, JobPhaseFailed, "AgentError", errorMsg, ct);
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
        // Release the PVC outside the try block so a transient PostStatusAsync failure
        // does not leak the claim until the next leadership acquisition rebuilds the pool.
        var pvcName = GetPvcFromJob(job);
        try
        {
            await _workItemClient.PostStatusAsync(workItemId, new WorkItemStatusUpdate
            {
                Status = status,
                FailureReason = failureReason,
                ErrorMessage = errorMessage
            }, ct);

            // Record terminal metrics
            var workItemStatus = status == JobPhaseSucceeded ? WorkItemStatus.Succeeded : WorkItemStatus.Failed;
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
        finally
        {
            // Release PVC unconditionally — idempotent on unknown names, ensures the pool slot
            // is freed even when PostStatusAsync throws a transient error.
            if (pvcName is not null)
                _pvcPool.Release(pvcName);
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
            if (conditions.Any(c => c.Type == JobPhaseComplete && c.Status == "True"))
                return JobPhaseSucceeded;
            if (conditions.Any(c => c.Type == JobPhaseFailed && c.Status == "True"))
                return JobPhaseFailed;
        }

        // Fall back to counters
        if (job.Status?.Succeeded > 0) return JobPhaseSucceeded;
        if (job.Status?.Failed > 0) return JobPhaseFailed;
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
        var failedCondition = conditions?.FirstOrDefault(c => c.Type == JobPhaseFailed && c.Status == "True");
        return failedCondition?.Message;
    }
}
