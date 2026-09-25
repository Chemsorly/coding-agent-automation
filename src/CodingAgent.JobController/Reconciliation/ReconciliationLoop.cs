using CodingAgent.Api.Client;
using CodingAgent.JobController.Dispatch;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using k8s.Models;
using Serilog;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;

namespace CodingAgent.JobController.Reconciliation;

// ─── Discriminated result type for execution-age classification ──────────────

/// <summary>
/// Result of <see cref="ReconciliationLoop.ResolveExecutionAge"/>.
/// Each case maps to a distinct outcome for the timeout-enforcement loop.
/// </summary>
internal abstract record ExecutionAgeResult;

/// <summary>
/// The work item's DispatchedAt is null and CreatedAt is still within the
/// grace window — timeout enforcement is deferred silently.
/// </summary>
internal sealed record WithinGrace : ExecutionAgeResult;

/// <summary>
/// The computed execution age is suspiciously low (INV-001 canary trigger).
/// Enforcement is skipped for this cycle. The canary counter and log warning
/// have already been emitted by <see cref="ReconciliationLoop.ResolveExecutionAge"/>.
/// </summary>
internal sealed record CanaryViolation : ExecutionAgeResult;

/// <summary>
/// The execution age has cleared all guard conditions and the item should be
/// evaluated for timeout enforcement.
/// </summary>
internal sealed record Enforceable(double AgeSeconds) : ExecutionAgeResult;

// ─── ReconciliationLoop ───────────────────────────────────────────────────────

/// <summary>
/// Core reconciliation logic for the Job Controller.
/// Called periodically by <see cref="ReconciliationService"/> to keep K8s Job state and
/// WorkItem state in sync. Methods are public so they can be tested independently.
/// </summary>
public sealed class ReconciliationLoop
{
    private readonly Serilog.ILogger _log;

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
    private const int TimeoutCanaryMinAgeSeconds = PipelineConstants.TimeoutCanaryMinAgeSeconds;

    private readonly IPipelineApiWorkItemClient _workItemClient;
    private readonly IKubernetesJobClient _k8sClient;
    private readonly DispatchServiceOptions _options;

    /// <summary>
    /// Tracks WorkItem IDs that have been successfully transitioned to a terminal state in the
    /// current leadership term. Prevents <see cref="HandleJobCompletedAsync"/> from re-posting
    /// status for jobs still present within the K8s retention window (default 600s).
    /// Cleared on leadership acquisition via <see cref="OnLeadershipAcquired"/>.
    /// </summary>
    // NOTE: _reconciledTerminalIds is a plain HashSet<Guid> with no thread-safety guarantees.
    // This is safe under the confirmed single-threaded invariant: ReconcileOnceAsync (the only
    // method that calls Contains and Add on this set) is invoked exactly once per OnPollCycleAsync
    // cycle — never in parallel with itself. OnLeadershipAcquired (which calls Clear) is invoked
    // only between leadership terms from RunLeadershipTermAsync, never concurrently with an
    // in-flight ReconcileOnceAsync. The other three tasks in the Task.WhenAll
    // (EnforceTimeoutsAsync, EnforceDispatchedTimeoutAsync, CleanupOrphansAsync) do not access
    // this field at all. If either OnLeadershipAcquired or ReconcileOnceAsync is ever called
    // from a different thread while the other is in flight, replace HashSet with
    // ConcurrentDictionary<Guid, byte> or guard all accesses with a lock.
    private readonly HashSet<Guid> _reconciledTerminalIds = new();
    private readonly Histogram<double> _timeoutExecutionAge;
    private readonly Counter<long> _timeoutCanaryViolations;
    private readonly Counter<long> _agentTimeouts;

    /// <summary>
    /// Called by <see cref="ReconciliationService"/> when this instance becomes the leader.
    /// Clears the in-process deduplication cache so that any completed K8s Jobs still present
    /// in the retention window are reconciled at least once by the new leadership term.
    /// </summary>
    public void OnLeadershipAcquired() => _reconciledTerminalIds.Clear();

    public ReconciliationLoop(
        IPipelineApiWorkItemClient workItemClient,
        IKubernetesJobClient k8sClient,
        DispatchServiceOptions options,
        IMeterFactory? pipelineMeterFactory = null,
        IMeterFactory? workDistMeterFactory = null,
        Serilog.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(workItemClient);
        ArgumentNullException.ThrowIfNull(k8sClient);
        ArgumentNullException.ThrowIfNull(options);
        _workItemClient = workItemClient;
        _k8sClient = k8sClient;
        _options = options;
        _log = (logger ?? Serilog.Log.Logger).ForContext<ReconciliationLoop>();

        if (workDistMeterFactory is not null)
        {
            var meter = workDistMeterFactory.Create(new MeterOptions(WorkDistributionTelemetry.MeterName));
            _timeoutExecutionAge = meter.CreateHistogram<double>("workdistribution.timeout_execution_age_seconds", "s",
                "Execution age at timeout enforcement — canary for anchor correctness",
                advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600] });
            _timeoutCanaryViolations = meter.CreateCounter<long>("workdistribution.timeout_canary_violations", "{violation}",
                "Timeout enforcement blocked by canary invariant — indicates timestamp bug");
            _agentTimeouts = meter.CreateCounter<long>("workdistribution.agent_timeouts", "{job}",
                "Agent jobs killed by session timeout enforcer");
        }
        else
        {
            _timeoutExecutionAge = WorkDistributionTelemetry.TimeoutExecutionAge;
            _timeoutCanaryViolations = WorkDistributionTelemetry.TimeoutCanaryViolations;
            _agentTimeouts = WorkDistributionTelemetry.AgentTimeouts;
        }
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
            _log.Warning(ex, "Failed to list K8s Jobs; skipping reconciliation cycle");
            return;
        }

        foreach (var job in jobs.Items ?? [])
        {
            if (ct.IsCancellationRequested) break;
            await HandleJobAsync(job, ct);
        }
    }

    /// <summary>
    /// Enforces the session timeout: marks Running items that have exceeded their per-item
    /// <see cref="ActiveWorkItemDto.TimeoutSeconds"/> as Failed.
    /// Rows written post-migration #2405 and post-insertion-guard (issue #2745) have a positive
    /// <see cref="ActiveWorkItemDto.TimeoutSeconds"/> value. Items with TimeoutSeconds ≤ 0
    /// (pre-migration rows or rows written before the insert-path guard was deployed) are
    /// silently skipped rather than force-failed.
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
            timedOut = await _workItemClient.GetActiveAsync(TimeoutCanaryMinAgeSeconds, ct: ct);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to query active work items for timeout enforcement");
            return;
        }

        foreach (var item in timedOut)
        {
            if (ct.IsCancellationRequested) break;

            // Only time out Running items here; Dispatched items are handled by EnforceDispatchedTimeoutAsync
            if (item.Status != WorkItemStatus.Running) continue;

            // Guard: skip items with a zero or negative TimeoutSeconds.
            // Post-migration #2405 and post-insert-guard (issue #2745) all rows have a positive value.
            // Items with TimeoutSeconds <= 0 are pre-migration rows or rows written before the guard
            // was deployed. With effectiveTimeoutSeconds=0, any Running item older than
            // TimeoutCanaryMinAgeSeconds (60s) would be immediately force-failed (executionAge >= 0
            // is always true). Skip instead — these items rely on orphan cleanup and
            // EnforceDispatchedTimeoutAsync for recovery.
            if (item.TimeoutSeconds <= 0) continue;

            var ageResult = ResolveExecutionAge(item);
            if (ageResult is WithinGrace or CanaryViolation) continue;

            if (ageResult is not Enforceable enforceable) continue;

            // Not timed out yet — skip.
            // TODO: [WARNING] The strict-less-than guard means executionAgeSeconds == effectiveTimeoutSeconds
            // is considered timed out (not skipped). At TimeoutSeconds == 60 (the canary minimum),
            // the canary guard (executionAgeSeconds < 60) and this guard (executionAgeSeconds < 60)
            // use the same threshold, so the canary invariant provides no protection for items at
            // exactly that boundary — the item is immediately enforced on the first cycle it is
            // returned by the query. This is a gap in the canary design that was not present when
            // the global timeout was always >> 60s. (DotNetSpecialist review [WARNING])
            if (enforceable.AgeSeconds < item.TimeoutSeconds) continue;

            _log.Warning("WorkItem {Id} timed out (status={Status}, job={K8sJobName}, issue={IssueIdentifier}) after {Seconds}s — marking Failed",
                item.Id, item.Status, item.K8sJobName ?? "none", item.IssueIdentifier ?? "unknown", item.TimeoutSeconds);

            try
            {
                // Emit a Reconcile.Timeout span for each item that is actually timed out.
                // Spans only fire when enforcement happens — idle cycles with no timed-out items
                // never reach this path (all items are skipped by the guards above).
                using var timeoutActivity = PipelineTelemetry.ActivitySource.StartActivity("Reconcile.Timeout");
                timeoutActivity?.SetTag("work_item_id", item.Id);
                timeoutActivity?.SetTag("agent_selector", item.AgentSelector ?? "");
                timeoutActivity?.SetTag("timeout_seconds", item.TimeoutSeconds);

                await _workItemClient.PostStatusAsync(item.Id, new WorkItemStatusUpdate
                {
                    Status = nameof(WorkItemStatus.Failed),
                    ErrorMessage = $"Agent timeout after {item.TimeoutSeconds}s",
                    FailureReason = "Timeout"
                }, ct);

                var jobName = await ResolveJobNameAsync(item, ct);

                _agentTimeouts.Add(1,
                    new KeyValuePair<string, object?>("agent_selector", item.AgentSelector ?? ""));

                if (jobName is not null)
                    await SafeDeleteJobAsync(jobName, ct);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to process timeout for WorkItem {Id}", item.Id);
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
            items = await _workItemClient.GetActiveAsync(_options.ChatPodConnectTimeoutSeconds, ct: ct);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to query active work items for dispatched timeout enforcement");
            return;
        }

        // Filter client-side: only Dispatched items (no K8s Job yet)
        var dispatched = items.Where(i => i.Status == WorkItemStatus.Dispatched).ToList();
        if (dispatched.Count == 0) return;

        // Build the live job list. Hoist V1JobList outside the try block so it is accessible
        // in the foreach loop for label-based resolution when K8sJobName is null (Issue #2474).
        V1JobList liveJobs;
        HashSet<string> liveJobNames;
        try
        {
            liveJobs = await _k8sClient.ListJobsAsync(
                _options.Namespace,
                "app.kubernetes.io/managed-by=caa-orchestrator",
                ct);
            // V1JobList.Items can be null when the K8s API omits the "items" field for an
            // empty result set. Use (liveJobs.Items ?? []).Select(...) here to guard against a
            // NullReferenceException on a successful API call that returns null Items. If Items is
            // null the NRE was previously caught by the catch below, which returned early — a spurious
            // skip on an otherwise-successful API call. (Same null-guard is applied at the liveJobs.Items
            // dereference further below in the foreach for the label-based null-K8sJobName branch.)
            liveJobNames = (liveJobs.Items ?? []).Select(j => j.Metadata?.Name ?? "").ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to list Jobs for dispatched timeout check; skipping");
            return;
        }

        foreach (var item in dispatched)
        {
            if (ct.IsCancellationRequested) break;

            // Determine whether a live K8s Job exists for this WorkItem.
            //
            // When K8sJobName is stored, use it for a direct name lookup in liveJobNames —
            // the API's DispatchLifecycleService uses "caa-{first8hex}" (ForBrain) while the old
            // job-controller path used "caa-agent-{first11hex}" (ForWorkItem, removed in #2322).
            //
            // When K8sJobName is null (legacy WorkItems created before the field was persisted),
            // resolve via caa/work-item-id label from the already-fetched liveJobs list — no extra
            // API call. JobNameFactory.ForWorkItem would produce the wrong name for API-path items
            // and was the root cause of false DispatchTimeout transitions (Issue #2474).
            bool isLive;
            if (item.K8sJobName is not null)
            {
                isLive = liveJobNames.Contains(item.K8sJobName);
            }
            else
            {
                // The null-guard (liveJobs.Items ?? []).Select(...) applied above ensures
                // liveJobNames is always a valid empty set rather than causing an NRE when
                // Items is null. Apply the same null-guard here for consistency:
                // (liveJobs.Items ?? []).FirstOrDefault(j => ...) covers the case where Items
                // is null on a successful K8s API call (Items null → empty → no match → isLive=false).
                var resolvedJob = (liveJobs.Items ?? []).FirstOrDefault(j =>
                {
                    var labels = j.Metadata?.Labels;
                    return labels is not null
                        && labels.TryGetValue("caa/work-item-id", out var idStr)
                        && idStr == item.Id.ToString();
                });
                isLive = resolvedJob is not null;
            }

            if (isLive) continue; // job exists — item is not orphaned

            _log.Warning("WorkItem {Id} stuck in Dispatched for >{Seconds}s with no K8s Job (issue={IssueIdentifier}) — marking Failed",
                item.Id, _options.ChatPodConnectTimeoutSeconds, item.IssueIdentifier ?? "unknown");

            try
            {
                // Emit a Reconcile.DispatchedTimeout span for each Dispatched item with no live Job.
                // Distinct from Reconcile.Timeout (Running items) to avoid ambiguity in Tempo queries.
                // Spans only fire when actual work happens — idle cycles return early above.
                using var dispatchedTimeoutActivity = PipelineTelemetry.ActivitySource.StartActivity("Reconcile.DispatchedTimeout");
                dispatchedTimeoutActivity?.SetTag("work_item_id", item.Id);
                dispatchedTimeoutActivity?.SetTag("agent_selector", item.AgentSelector ?? "");

                await _workItemClient.PostStatusAsync(item.Id, new WorkItemStatusUpdate
                {
                    Status = nameof(WorkItemStatus.Failed),
                    ErrorMessage = $"No live K8s Job found {_options.ChatPodConnectTimeoutSeconds}s after dispatch",
                    FailureReason = nameof(FailureReason.Timeout)
                }, ct);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to post Failed status for orphaned Dispatched WorkItem {Id}", item.Id);
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
            activeItems = await _workItemClient.GetActiveAsync(0, ct: ct);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to fetch data for orphan cleanup; skipping");
            return;
        }

        var activeIds = activeItems.Select(i => i.Id).ToHashSet();

        foreach (var job in jobs.Items ?? [])
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

            // Respect a minimum retention window before deleting orphaned/stale jobs.
            // This lets kubectl logs remain readable after a job completes/fails
            // and prevents the orphan sweep from racing with the K8s TTL controller.
            // A job with no StartTime yet is brand-new (the K8s job controller has not had its
            // first sync), NOT "never started properly" — its WorkItem may still be committing
            // Pending → Dispatched. CreationTimestamp covers this window: it is set by the API
            // server at object-creation time (1s resolution), providing the same 600s protection
            // as StartTime. Jobs with no timestamps at all (hand-built objects) have no anchor
            // and are deleted immediately.
            var completionTime = job.Status?.CompletionTime
                ?? job.Status?.StartTime              // fallback: use start time if no completion recorded
                ?? job.Metadata?.CreationTimestamp;   // fallback: brand-new job whose startTime not yet set
            const int LogRetentionSeconds = 600; // 10 minutes
            if (completionTime.HasValue &&
                (DateTimeOffset.UtcNow - new DateTimeOffset(completionTime.Value, TimeSpan.Zero)).TotalSeconds < LogRetentionSeconds)
            {
                _log.Debug("Skipping orphan/stale K8s Job {JobName} — completed/created {Age}s ago, within {Retention}s retention window",
                    jobName,
                    (int)(DateTimeOffset.UtcNow - new DateTimeOffset(completionTime.Value, TimeSpan.Zero)).TotalSeconds,
                    LogRetentionSeconds);
                continue;
            }

            _log.Information("Deleting orphan/stale K8s Job {JobName} (reason={OrphanReason})", jobName, orphanReason);
            // Emit Reconcile.OrphanCleanup per job actually deleted (not per idle cycle iteration).
            // TODO: The span is currently closed before SafeDeleteJobAsync runs (using block ends before the await).
            // The span therefore records ~0 duration and cannot reflect a deletion failure.
            // Fix: move SafeDeleteJobAsync inside the using block, or switch to `using var` statement form.
            using (var orphanActivity = PipelineTelemetry.ActivitySource.StartActivity("Reconcile.OrphanCleanup"))
            {
                orphanActivity?.SetTag("job_name", jobName);
                orphanActivity?.SetTag("orphan_reason", orphanReason);
                if (workItemId.HasValue)
                    orphanActivity?.SetTag("work_item_id", workItemId.Value);
            }
            await SafeDeleteJobAsync(jobName, ct);
        }
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Computes the execution age for <paramref name="item"/> and classifies it as
    /// <see cref="WithinGrace"/>, <see cref="CanaryViolation"/>, or <see cref="Enforceable"/>.
    /// <para>
    /// <b>Side effects (intentional):</b> this method records
    /// <c>_timeoutExecutionAge</c> (histogram) and, on a canary violation,
    /// <c>_timeoutCanaryViolations</c> (counter). These side effects are deliberately
    /// placed here — not at the call site — to preserve the invariant that the
    /// grace-window skip path records <em>no</em> metrics (an item silently deferred
    /// within the grace window must not pollute the INV-001 canary signal).
    /// Do NOT move the metric calls to the caller.
    /// </para>
    /// </summary>
    private ExecutionAgeResult ResolveExecutionAge(ActiveWorkItemDto item)
    {
        double executionAgeSeconds;

        if (item.DispatchedAt.HasValue)
        {
            executionAgeSeconds = (DateTimeOffset.UtcNow - item.DispatchedAt.Value).TotalSeconds;
        }
        else
        {
            // DispatchedAt is null (e.g. a write failure on the claim path).
            // Use CreatedAt as a fallback timeout anchor after a configurable grace window.
            // Items within the grace window are silently skipped (return WithinGrace) WITHOUT
            // recording metrics or incrementing the canary counter — the canary metric signals
            // INV-001 (wrong timestamp anchor bugs), not expected grace-window deferrals. Firing
            // it here would pollute the signal and mask real bugs.
            // Items beyond the grace window are escalated: CreatedAt becomes the age anchor
            // and a Warning is emitted so operators can identify stuck items.
            var createdAgeSeconds = item.CreatedAt.HasValue
                ? (DateTimeOffset.UtcNow - item.CreatedAt.Value).TotalSeconds
                // TODO [WARNING]: null CreatedAt silently defers enforcement indefinitely — same
                // class of bug as the original null DispatchedAt issue. The 0.0 fallback causes
                // the grace-window check below to always fire and the item is permanently skipped
                // with no log or metric. In production, CreatedAt should always be non-null
                // (WorkItemEntity.CreatedAt is a non-null column), but if a backfill is missed or
                // the DTO is constructed without the field the item becomes permanently stuck.
                // At minimum emit a Log.Warning here so operators can detect the condition.
                // (Correctness review [WARNING])
                : 0.0; // null CreatedAt (pre-dates this field in test code) → treat as just created

            // TODO [WARNING]: strict less-than (<) means an item with createdAgeSeconds exactly
            // equal to NullDispatchedAtGraceWindowSeconds is skipped for another full cycle.
            // The requirement states "older than the grace window is force-failed", so the
            // boundary condition (age == graceWindow) should proceed to enforcement. Because
            // createdAgeSeconds is a double, exact equality is extremely rare in practice, but
            // semantically the condition should be <= to match the stated requirement boundary.
            // (Correctness review [WARNING])
            if (createdAgeSeconds < _options.NullDispatchedAtGraceWindowSeconds)
            {
                // Within grace window — skip without recording any metrics.
                return new WithinGrace();
            }

            // Grace window expired — use CreatedAt as fallback timeout anchor.
            _log.Warning(
                "WorkItem {Id} has null DispatchedAt and CreatedAt is {Age:F0}s old (>{Grace}s grace window) — using CreatedAt as timeout anchor",
                item.Id, createdAgeSeconds, _options.NullDispatchedAtGraceWindowSeconds);
            executionAgeSeconds = createdAgeSeconds;
        }

        _timeoutExecutionAge.Record(executionAgeSeconds,
            new KeyValuePair<string, object?>("agent_selector", item.AgentSelector ?? ""));

        // Canary guard: if age is suspiciously low the timeout anchor is wrong (INV-001).
        // Skip enforcement for this sweep — the item will be re-evaluated next cycle.
        if (executionAgeSeconds < TimeoutCanaryMinAgeSeconds)
        {
            _log.Warning("WorkItem {Id} timeout canary violation: execution age {AgeSeconds:F1}s < {MinAge}s — skipping enforcement",
                item.Id, executionAgeSeconds, TimeoutCanaryMinAgeSeconds);
            _timeoutCanaryViolations.Add(1,
                new KeyValuePair<string, object?>("agent_selector", item.AgentSelector ?? ""));
            return new CanaryViolation();
        }

        return new Enforceable(executionAgeSeconds);
    }

    /// <summary>
    /// Resolves the K8s Job name for a work item that is being timed out.
    /// <para>
    /// When <see cref="ActiveWorkItemDto.K8sJobName"/> is stored, it is returned directly.
    /// When it is <c>null</c> (legacy work items created before the field was persisted),
    /// the Job is resolved by querying the label selector <c>caa/work-item-id={item.Id}</c>.
    /// If no Job is found, returns <c>null</c> — the caller must skip deletion.
    /// </para>
    /// </summary>
    private async Task<string?> ResolveJobNameAsync(ActiveWorkItemDto item, CancellationToken ct)
    {
        // Fast path: stored name is known — use it directly.
        // The API's DispatchLifecycleService uses "caa-{first8hex}" (ForBrain) while the old
        // job-controller path used "caa-agent-{first11hex}" (ForWorkItem, removed in #2322).
        // Using the stored name avoids recomputing the wrong format.
        if (item.K8sJobName is not null)
            return item.K8sJobName;

        // Legacy path: K8sJobName was not persisted at dispatch time.
        // Resolve the actual running Job via label selector — the same approach CleanupOrphansAsync
        // uses. If no Job is found (already cleaned up or never started), return null to skip deletion.
        V1JobList labelJobs;
        try
        {
            labelJobs = await _k8sClient.ListJobsAsync(
                _options.Namespace,
                $"caa/work-item-id={item.Id}",
                ct);
        }
        catch (Exception labelEx)
        {
            _log.Warning(labelEx, "Failed to resolve K8s Job name via label selector for WorkItem {Id} — skipping deletion", item.Id);
            labelJobs = new V1JobList { Items = [] };
        }

        // V1JobList.Items can be null when the K8s API returns an empty result
        // without the "items" field in the JSON body (the KubernetesClient deserialiser
        // leaves Items null rather than an empty list in that case). Use
        // (labelJobs.Items ?? []).FirstOrDefault() here to avoid a NullReferenceException
        // on a successful call that returns null Items. The catch above only guards the
        // exception path; a null Items on a successful response bypasses it entirely.
        var resolved = (labelJobs.Items ?? []).FirstOrDefault();
        if (resolved?.Metadata?.Name is null)
        {
            _log.Warning("WorkItem {Id} timed out but no K8s Job found via label selector caa/work-item-id={WorkItemId} — job already deleted or never started",
                item.Id, item.Id);
            return null;
        }

        return resolved.Metadata.Name;
    }

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
                // Classify the failure reason: if the WorkItem was never claimed (still Dispatched),
                // no agent ran — record as InfrastructureFailure, not AgentError (issue #2956).
                var failureReason = await ClassifyJobFailureReasonAsync(workItemId.Value, ct);
                // Emit Reconcile.JobFailed ONLY for the failed path (not for Succeeded).
                // Placed here in case JobPhaseFailed: rather than inside HandleJobCompletedAsync
                // because HandleJobCompletedAsync is called for both Succeeded and Failed phases.
                // TODO: The span is currently closed before HandleJobCompletedAsync runs (using block ends
                // after the two SetTag calls). The span records ~0 duration and cannot reflect a failure in
                // HandleJobCompletedAsync. Fix: switch to `using var jobFailedActivity = ...` statement form
                // so the span stays active until the end of the case arm (including HandleJobCompletedAsync).
                using (var jobFailedActivity = PipelineTelemetry.ActivitySource.StartActivity("Reconcile.JobFailed"))
                {
                    jobFailedActivity?.SetTag("work_item_id", workItemId.Value);
                    jobFailedActivity?.SetTag("failure_reason", failureReason);
                }
                if (await HandleJobCompletedAsync(workItemId.Value, job, JobPhaseFailed, failureReason, errorMsg, ct))
                    _reconciledTerminalIds.Add(workItemId.Value);
                break;
                // Active/Unknown/Pending — no action needed
        }
    }

    /// <summary>
    /// Returns the appropriate failure reason string for a failed K8s Job.
    /// Calls <see cref="IPipelineApiWorkItemClient.GetStatusAsync"/> to determine whether the
    /// WorkItem was ever claimed by an agent. A WorkItem still in <c>Dispatched</c> state means
    /// no agent successfully accepted it — the failure is infrastructure-level.
    /// A claimed (<c>Running</c> or later) WorkItem is an agent error.
    /// Returns <c>AgentError</c> when the status is null (item not found or already cleaned up).
    /// </summary>
    private async Task<string> ClassifyJobFailureReasonAsync(Guid workItemId, CancellationToken ct)
    {
        try
        {
            var status = await _workItemClient.GetStatusAsync(workItemId, ct);
            // Dispatched means the K8s Job was created but no agent ever called JobAccepted
            // (which transitions the WorkItem to Running). The failure happened before any agent ran.
            if (status == WorkItemStatus.Dispatched)
                return nameof(FailureReason.InfrastructureFailure);

            // TODO: [WARNING] When status is null (item not found or already cleaned up) the method
            // silently falls through to AgentError with no log entry. Operators diagnosing a failed
            // Job whose WorkItem has already been deleted will see AgentError with no indication it
            // is a fallback due to a missing item. Consider adding a Debug-level log here:
            // if (status is null) _log.Debug("ClassifyJobFailureReasonAsync: status null for {WorkItemId}, defaulting to AgentError", workItemId);
        }
        catch (Exception ex)
        {
            // TODO: [WARNING] This catch is too broad — it swallows OperationCanceledException from the
            // reconciliation loop's own CancellationToken, causing the loop to mark the WorkItem as
            // AgentError and return silently instead of propagating the cancellation. Fix:
            //   catch (Exception ex) when (ex is not OperationCanceledException)
            // All current callers pass the loop's CancellationToken, so a mid-flight cancellation
            // would silently emit AgentError. (Review finding: DotNetSpecialist [WARNING] — issue #2956)
            _log.Warning(ex, "ClassifyJobFailureReasonAsync: failed to query status for WorkItem {WorkItemId}, defaulting to AgentError", workItemId);
        }

        return nameof(FailureReason.AgentError);
    }

    /// <summary>
    /// Posts a terminal status update for a completed K8s Job and records telemetry.
    /// Returns <c>true</c> if <see cref="IPipelineApiWorkItemClient.PostStatusAsync"/>
    /// succeeded (the caller should then cache the WorkItem ID to suppress duplicate posts on
    /// subsequent reconciliation cycles), or <c>false</c> if it threw.
    /// <para>
    /// NOTE: a <c>false</c> return does NOT always mean the caller must retry. If PostStatusAsync
    /// returned 400 (rejected transition), this method caches the WorkItem ID internally in
    /// <c>_reconciledTerminalIds</c> before returning <c>false</c>, so the caller must NOT
    /// double-cache it. Only non-400 exceptions leave the ID uncached (so the caller's retry
    /// on the next cycle is correct for those cases).
    /// </para>
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
            var transitioned = await _workItemClient.PostStatusAsync(workItemId, new WorkItemStatusUpdate
            {
                Status = status,
                FailureReason = failureReason,
                ErrorMessage = errorMessage
            }, ct);

            if (transitioned)
            {
                // Record terminal metrics — only when a real state transition occurred.
                // Skip for idempotent no-ops (already-terminal items, e.g. Cancelled→Failed late
                // callback) to avoid double-counting. PostStatus returns HTTP 204 No Content for
                // no-ops and HTTP 200 for real transitions; PipelineApiWorkItemClient maps these
                // to false/true respectively. (Issue #2802)
                // Metrics are recorded by the API's WorkItemStatusTransitionService when it
                // processes the POST above — no metric recording needed here. (Issue #2967)
            }

            // Log and mark as succeeded for BOTH transitioned and no-op paths: both represent
            // "this item is handled, don't retry" and neither is a transient error.
            _log.Information("WorkItem {Id} marked {Status} from K8s Job {Job}", workItemId, status, job.Metadata?.Name);
            succeeded = true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // A 400 response means the API definitively rejected this transition
            // (e.g. WorkItem is Pending, not yet Running — Pending→Succeeded is invalid).
            // This is not a transient error; retrying will always produce the same result.
            // Cache the WorkItem ID to suppress the retry loop and log at Warning level
            // (expected edge case, not a system error).
            _log.Warning(
                "HandleJobCompletedAsync: completion POST for WorkItem {WorkItemId} rejected (400 — " +
                "WorkItem not in a transitionable state). Caching as processed to prevent retry.",
                workItemId);
            _reconciledTerminalIds.Add(workItemId);
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to post status {Status} for WorkItem {Id}", status, workItemId);
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
            _log.Warning(ex, "Failed to delete K8s Job {JobName}", jobName);
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
        // Guard Active == 0: a retrying job has Failed=1 while Active=1 (Kubernetes creates a
        // retry pod after each failed attempt). The "Failed" condition type is only set once all
        // retries are exhausted. Returning JobPhaseFailed while Active > 0 would prematurely mark
        // a running work item as failed and cancel the live K8s Job (data-corruption under the
        // default backoffLimit >= 1). This guard matches the equivalent counter-fallback check
        // that was previously in DispatchLoopHelpers.IsJobTerminal (deleted in issue #2323).
        if (job.Status?.Failed > 0 && (job.Status?.Active ?? 0) == 0) return JobPhaseFailed;
        return "Active";
    }

    private static string? GetFailureMessage(V1Job job)
    {
        var conditions = job.Status?.Conditions;
        var failedCondition = conditions?.FirstOrDefault(c => c.Type == JobPhaseFailed && c.Status == "True");
        return failedCondition?.Message;
    }
}
