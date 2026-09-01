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
    private readonly DispatchServiceOptions _options;

    /// <summary>
    /// Tracks WorkItem IDs that have been successfully transitioned to a terminal state in the
    /// current leadership term. Prevents <see cref="HandleJobCompletedAsync"/> from re-posting
    /// status for jobs still present within the K8s retention window (default 600s).
    /// Cleared on leadership acquisition via <see cref="OnLeadershipAcquired"/>.
    /// </summary>
    // TODO: _reconciledTerminalIds is a plain HashSet<Guid> with no thread-safety guarantees.
    // In the current design ReconcileOnceAsync is only invoked once per OnPollCycleAsync (via
    // Task.WhenAll with no parallel ReconcileOnceAsync calls), and OnLeadershipAcquired is called
    // between leadership terms — so no concurrent access occurs in production. However,
    // OnLeadershipAcquired is public and tests call ReconcileOnceAsync directly; any external
    // caller that invokes these concurrently would cause undefined behaviour on HashSet.
    // Consider replacing with a ConcurrentDictionary<Guid, byte> or adding a lock if the public
    // surface of OnLeadershipAcquired is ever called from a different thread than ReconcileOnceAsync.
    private readonly HashSet<Guid> _reconciledTerminalIds = new();

    /// <summary>
    /// Called by <see cref="ReconciliationService"/> when this instance becomes the leader.
    /// Clears the in-process deduplication cache so that any completed K8s Jobs still present
    /// in the retention window are reconciled at least once by the new leadership term.
    /// </summary>
    public void OnLeadershipAcquired() => _reconciledTerminalIds.Clear();

    public ReconciliationLoop(
        IPipelineApiWorkItemClient workItemClient,
        IKubernetesJobClient k8sClient,
        DispatchServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(workItemClient);
        ArgumentNullException.ThrowIfNull(k8sClient);
        ArgumentNullException.ThrowIfNull(options);
        _workItemClient = workItemClient;
        _k8sClient = k8sClient;
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
    /// Enforces the session timeout: marks Running items that have exceeded their per-item
    /// <see cref="ActiveWorkItemDto.TimeoutSeconds"/> as Failed.
    /// Items without a stored timeout (zero) fall back to the global default
    /// (<see cref="PipelineConstants.DefaultAgentTimeout"/>).
    /// </summary>
    public async Task EnforceTimeoutsAsync(CancellationToken ct)
    {
        // Use the canary minimum as the query threshold so that items with short per-project
        // timeouts (potentially less than the global default) are still evaluated.
        // Per-item timeout enforcement happens in the loop body below.
        // TODO: PipelineConfiguration.AgentTimeout has no minimum-value enforcement at the DB layer.
        // A project configured with AgentTimeout < TimeoutCanaryMinAgeSeconds (60s) will never be
        // enforced: every cycle the canary guard fires, emitting a TimeoutCanaryViolations metric
        // but skipping the item. Consider adding a lower-bound clamp on AgentTimeout at the
        // configuration-save endpoint (similar to DispatchServiceOptions.ValidateAndClamp).
        IReadOnlyList<ActiveWorkItemDto> timedOut;
        try
        {
            timedOut = await _workItemClient.GetActiveAsync(TimeoutCanaryMinAgeSeconds, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to query active work items for timeout enforcement");
            return;
        }

        var globalDefaultSeconds = (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds;

        foreach (var item in timedOut)
        {
            if (ct.IsCancellationRequested) break;

            // Only time out Running items here; Dispatched items are handled by EnforceDispatchedTimeoutAsync
            if (item.Status != WorkItemStatus.Running) continue;

            // Resolve the effective timeout for this item.
            // TimeoutSeconds == 0 means the value was not stored (pre-dates this field) — fall back
            // to the global PipelineConfiguration.AgentTimeout default for backward compatibility.
            // TODO: The zero sentinel is not enforced at the entity/DTO layer — it is indistinguishable
            // from an explicitly set value of 0. Consider adding a DB constraint or a positive-value
            // check at the enqueue endpoint (WorkItemEndpoints) so that TimeoutSeconds > 0 is always
            // guaranteed for new rows, narrowing this fallback to truly legacy data only.
            var effectiveTimeoutSeconds = item.TimeoutSeconds > 0
                ? item.TimeoutSeconds
                : globalDefaultSeconds;

            // Compute execution age from DispatchedAt. If DispatchedAt is null (items dispatched before
            // the field was added), fall back to effectiveTimeoutSeconds — safe to enforce.
            // TODO: DispatchedAt == null with effectiveTimeoutSeconds >= TimeoutCanaryMinAgeSeconds means
            // executionAgeSeconds == effectiveTimeoutSeconds, so the subsequent guard
            // (executionAgeSeconds < effectiveTimeoutSeconds) evaluates to false and the item is
            // immediately timed out on the first reconciliation cycle after dispatch. If DispatchedAt
            // can remain null for a legitimately running item (e.g., a write failure on the claim
            // path), this creates a false-positive timeout for a healthy item. Consider returning
            // early (skip enforcement) when DispatchedAt is null and the item has been running less
            // than effectiveTimeoutSeconds according to CreatedAt, or storing DispatchedAt
            // atomically with the claim to eliminate the null window. (DotNetSpecialist review [WARNING])
            var executionAgeSeconds = item.DispatchedAt.HasValue
                ? (DateTimeOffset.UtcNow - item.DispatchedAt.Value).TotalSeconds
                : effectiveTimeoutSeconds;

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

            // Not timed out yet — skip.
            // TODO: The strict-less-than guard means executionAgeSeconds == effectiveTimeoutSeconds
            // is considered timed out (not skipped). At TimeoutSeconds == 60 (the canary minimum),
            // the canary guard (executionAgeSeconds < 60) and this guard (executionAgeSeconds < 60)
            // use the same threshold, so the canary invariant provides no protection for items at
            // exactly that boundary — the item is immediately enforced on the first cycle it is
            // returned by the query. This is a gap in the canary design that was not present when
            // the global timeout was always >> 60s. (DotNetSpecialist review [WARNING])
            if (executionAgeSeconds < effectiveTimeoutSeconds)
                continue;

            Log.Warning("WorkItem {Id} timed out (status={Status}, job={K8sJobName}, issue={IssueIdentifier}) after {Seconds}s — marking Failed",
                item.Id, item.Status, item.K8sJobName ?? "none", item.IssueIdentifier ?? "unknown", effectiveTimeoutSeconds);

            try
            {
                await _workItemClient.PostStatusAsync(item.Id, new WorkItemStatusUpdate
                {
                    Status = nameof(WorkItemStatus.Failed),
                    ErrorMessage = $"Agent timeout after {effectiveTimeoutSeconds}s",
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

            Log.Warning("WorkItem {Id} stuck in Dispatched for >{Seconds}s with no K8s Job (issue={IssueIdentifier}) — marking Failed",
                item.Id, _options.ChatPodConnectTimeoutSeconds, item.IssueIdentifier ?? "unknown");

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

        // Skip WorkItems already reconciled in this leadership term to avoid redundant
        // PostStatusAsync calls and misleading log lines during the K8s job retention window.
        if (_reconciledTerminalIds.Contains(workItemId.Value)) return;

        var phase = GetJobPhase(job);

        switch (phase)
        {
            case JobPhaseSucceeded:
                if (await HandleJobCompletedAsync(workItemId.Value, job, JobPhaseSucceeded, null, null, ct))
                    _reconciledTerminalIds.Add(workItemId.Value);
                break;
            case JobPhaseFailed:
                var errorMsg = GetFailureMessage(job);
                if (await HandleJobCompletedAsync(workItemId.Value, job, JobPhaseFailed, "AgentError", errorMsg, ct))
                    _reconciledTerminalIds.Add(workItemId.Value);
                break;
                // Active/Unknown/Pending — no action needed
                // TODO: If a JobPhaseCancelled case is ever added, remember to also add the workItemId
                // to _reconciledTerminalIds on success — the guard comment says "Succeeded, Failed,
                // Cancelled" but the current switch only covers Succeeded and Failed. Omitting it for
                // a future Cancelled case would allow duplicate PostStatusAsync calls within the K8s
                // job retention window.
        }
    }

    /// <summary>
    /// Posts a terminal status update for a completed K8s Job and records telemetry.
    /// Returns <c>true</c> if <see cref="IPipelineApiWorkItemClient.PostStatusAsync"/>
    /// succeeded (the caller should then cache the WorkItem ID to suppress duplicate posts on
    /// subsequent reconciliation cycles), or <c>false</c> if it threw (the caller must NOT cache
    /// the ID so that the next cycle retries the post).
    /// </summary>
    private async Task<bool> HandleJobCompletedAsync(
        Guid workItemId,
        V1Job job,
        string status,
        string? failureReason,
        string? errorMessage,
        CancellationToken ct)
    {
        var succeeded = false;
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
            succeeded = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to post status {Status} for WorkItem {Id}", status, workItemId);
        }

        return succeeded;
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

    private static string? GetFailureMessage(V1Job job)
    {
        var conditions = job.Status?.Conditions;
        var failedCondition = conditions?.FirstOrDefault(c => c.Type == JobPhaseFailed && c.Status == "True");
        return failedCondition?.Message;
    }
}
