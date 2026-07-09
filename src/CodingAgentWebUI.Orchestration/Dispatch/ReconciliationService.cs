using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// K8s mode only: watches K8s Jobs for completions/failures (label selector),
/// performs periodic safety-net poll for orphan detection, timeout enforcement,
/// stale work item cleanup, PVC release, and PipelineRuns retention.
/// Runs under leader election (same Lease as DispatchService).
/// </summary>
public sealed class ReconciliationService : BackgroundService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ReconciliationService>();

    private const string ManagedByLabel = "app.kubernetes.io/managed-by";
    private const string ManagedByValue = "caa-orchestrator";
    private const string WorkItemIdLabel = "caa/work-item-id";

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly LeaderElectionService _leaderElection;
    private readonly IKubernetes _kubeClient;
    private readonly WorkItemTransitionService _transitionService;
    private readonly ILabelSwapper? _labelSwapper;
    private readonly IRunLifecycleManager? _lifecycleManager;
    private readonly IConsolidationService? _consolidationService;
    private readonly IConfigurationStore? _configStore;
    private readonly IJobDeduplicationGuard? _dedupGuard;
    private readonly ReconciliationServiceOptions _options;

    private string? _lastResourceVersion;

    public ReconciliationService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        LeaderElectionService leaderElection,
        IKubernetes kubeClient,
        WorkItemTransitionService transitionService,
        IConfiguration configuration,
        ILabelSwapper? labelSwapper = null,
        IRunLifecycleManager? lifecycleManager = null,
        IConsolidationService? consolidationService = null,
        IConfigurationStore? configStore = null,
        IJobDeduplicationGuard? dedupGuard = null)
    {
        _dbFactory = dbFactory;
        _leaderElection = leaderElection;
        _kubeClient = kubeClient;
        _transitionService = transitionService;
        _labelSwapper = labelSwapper;
        _lifecycleManager = lifecycleManager;
        _consolidationService = consolidationService;
        _configStore = configStore;
        _dedupGuard = dedupGuard;
        _options = new ReconciliationServiceOptions();
        configuration.GetSection("WorkDistribution:Reconciliation").Bind(_options);

        _options.Namespace = configuration.GetValue<string>("WorkDistribution:Namespace")
            ?? Environment.GetEnvironmentVariable("POD_NAMESPACE")
            ?? "default";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("ReconciliationService started — waiting for leader election");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for leadership
            while (!stoppingToken.IsCancellationRequested && !_leaderElection.IsLeader)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested) break;

            Log.Information("ReconciliationService: leader acquired, running startup reconciliation");

            // Reset watch state for new leadership term (avoids 410 Gone with stale resourceVersion)
            _lastResourceVersion = null;

            // Create linked token: cancels on EITHER host stop OR leadership loss
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, _leaderElection.LeaderToken);
            var ct = linked.Token;

            try
            {
                await RunStartupReconciliationAsync(ct);

                // Run Watch and Poll concurrently
                var watchTask = RunWatchLoopAsync(ct);
                var pollTask = RunPollLoopAsync(ct);

                // Exit when leadership lost or stopping
                await Task.WhenAny(watchTask, pollTask);

                // If neither stoppingToken nor LeaderToken caused the exit, cancel manually
                if (!ct.IsCancellationRequested)
                    await linked.CancelAsync();

                try { await Task.WhenAll(watchTask, pollTask); }
                catch (OperationCanceledException) { /* expected */ }
                catch (Exception ex)
                {
                    // Catch non-OCE exceptions from WhenAll to prevent BackgroundService termination.
                    // Log and re-enter the leader wait loop so reconciliation resumes on next leadership acquisition.
                    Log.Error(ex, "ReconciliationService: watch/poll loop faulted unexpectedly — will re-enter leader wait loop");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // Leadership lost during startup reconciliation or work loop — fall through to re-enter wait loop
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                Log.Information("ReconciliationService: leadership lost, re-entering wait loop");
            }
        }

        Log.Information("ReconciliationService: exiting (stopping)");
    }

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
    /// </summary>
    private async Task ReconcileStartupLabelsAsync(CancellationToken ct)
    {
        if (_labelSwapper is null) return;

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

        // Get recently terminal items that might still have in-progress labels
        var recentTerminal = await db.WorkItems
            .Where(w => (w.Status == WorkItemStatus.Succeeded ||
                         w.Status == WorkItemStatus.Failed ||
                         w.Status == WorkItemStatus.Cancelled) &&
                        w.CompletedAt > DateTimeOffset.UtcNow.AddMinutes(-5))
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
            .ToListAsync(ct);

        foreach (var item in recentTerminal)
        {
            if (activeSet.Contains((item.IssueIdentifier, item.IssueProviderConfigId)))
                continue; // Still has an active work item

            try
            {
                await _labelSwapper.SwapLabelAsync(
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

    // ── K8s Job Watch Loop ───────────────────────────────────────────────

    private async Task RunWatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _leaderElection.IsLeader)
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
                Log.Warning("ReconciliationService: Watch got 410 Gone, performing full re-list");
                _lastResourceVersion = null;
                await RelistJobsAsync(ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ReconciliationService: Watch disconnected, reconnecting in 1s");
            }

            if (!ct.IsCancellationRequested && _leaderElection.IsLeader)
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
                }, ct);

            LogTerminalTransition(workItemId, WorkItemStatus.Failed, FailureReason.InfrastructureFailure);

            // Delete the failed Job immediately to release PVC faster (don't wait for TTL controller).
            // Without this, the PVC stays claimed for up to TtlSecondsAfterFinished (default 3600s).
            var jobName = job.Metadata?.Name;
            if (!string.IsNullOrEmpty(jobName))
            {
                await TryDeleteJobAsync(jobName, ct);
            }
        }
        // For "Complete" — agent should have already POSTed terminal status.
        // If not, the poll loop will catch it as orphan/timeout.
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

    // ── Safety-Net Poll Loop ─────────────────────────────────────────────

    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _leaderElection.IsLeader)
        {
            try
            {
                await RunReconciliationCycleAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ReconciliationService: error in reconciliation cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunReconciliationCycleAsync(CancellationToken ct)
    {
        await DetectOrphansAsync(ct);
        await EnforceTimeoutsAsync(ct);
        await EnforceConsolidationTimeoutsAsync(ct);
        await DetectPodStartupFailuresAsync(ct);
        await CleanupStaleWorkItemsAsync(ct);
        await CleanupStalePipelineRunsAsync(ct);
        await ReconcilePvcsFromPollAsync(ct);
    }

    // ── Orphan Detection ─────────────────────────────────────────────────

    /// <summary>
    /// Dispatched/Running with no matching K8s Job → Failed (InfrastructureFailure).
    /// </summary>
    private async Task DetectOrphansAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var activeItems = await db.WorkItems
            .Where(w => (w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
                        && w.K8sJobName != null)
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
                    }, ct);

                LogTerminalTransition(item.Id, WorkItemStatus.Failed, FailureReason.InfrastructureFailure);
            }
        }
    }

    // ── Timeout Enforcement ──────────────────────────────────────────────

    /// <summary>
    /// Timeout enforcement with progress awareness.
    /// Uses DB-persisted LastProgressAt as the timeout anchor when available,
    /// matching the HeartbeatMonitor behavior in SignalR/Legacy mode.
    /// Falls back to DispatchedAt when LastProgressAt is null.
    /// </summary>
    internal async Task EnforceTimeoutsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var candidates = await db.WorkItems
            .Where(w => (w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
                        && w.TimeoutSeconds > 0)
            .Select(w => new { w.Id, w.DispatchedAt, w.CreatedAt, w.TimeoutSeconds, w.K8sJobName, w.LastProgressAt, w.IssueIdentifier, w.IssueProviderConfigId })
            .ToListAsync(ct);

        foreach (var item in candidates)
        {
            if (ct.IsCancellationRequested) break;

            // Progress-aware anchor: use LastProgressAt when available (updated by heartbeats/step transitions).
            // Falls back to DispatchedAt → CreatedAt for items that haven't reported progress yet.
            var anchor = item.LastProgressAt ?? item.DispatchedAt ?? item.CreatedAt;
            if (!IsTimedOut(anchor, item.TimeoutSeconds, now))
                continue;

            // Canary invariant: refuse to timeout items with suspiciously low execution age.
            var executionAge = (now - anchor).TotalSeconds;
            if (!ShouldEnforceTimeout(anchor, item.TimeoutSeconds, now))
            {
                Log.Error(
                    "CANARY VIOLATION: WorkItem {WorkItemId} appears timed out but execution age is only {ExecutionAge:F1}s " +
                    "(minimum: {Minimum}s). Queue time was {QueueTime:F0}s. Refusing to kill — likely a timestamp bug.",
                    item.Id, executionAge, MinimumExecutionAgeSeconds,
                    item.DispatchedAt.HasValue ? (item.DispatchedAt.Value - item.CreatedAt).TotalSeconds : -1);
                WorkDistributionTelemetry.TimeoutCanaryViolations.Add(1);
                continue;
            }

            // Record execution age at timeout for canary alerting
            WorkDistributionTelemetry.TimeoutExecutionAge.Record(executionAge);

            Log.Warning("ReconciliationService: timeout — WorkItem {WorkItemId} exceeded {Timeout}s",
                item.Id, item.TimeoutSeconds);

            var timeoutReason = $"Timeout exceeded: {item.TimeoutSeconds}s";

            // Try full lifecycle cleanup first (label swap, history, dedup, agent state).
            // FailRunAsync uses the in-memory PipelineRun — returns null if not in memory (e.g., different replica).
            var lifecycleHandled = false;
            if (_lifecycleManager is not null)
            {
                var result = await _lifecycleManager.FailRunAsync(item.Id.ToString(), timeoutReason, ct);
                lifecycleHandled = result is not null;
            }

            // Fallback: if no lifecycle manager or run wasn't in memory, do direct DB transition
            // plus best-effort label swap and dedup release (prevents stale labels and permanent dispatch blocking)
            if (!lifecycleHandled)
            {
                await _transitionService.TransitionAsync(item.Id, WorkItemStatus.Failed,
                    w =>
                    {
                        w.CompletedAt = DateTimeOffset.UtcNow;
                        w.FailureReason = FailureReason.Timeout;
                        w.ErrorMessage = timeoutReason;
                    }, ct);

                // Best-effort label swap to agent:error (prevents stale agent:in-progress on GitHub)
                if (_labelSwapper is not null)
                {
                    try
                    {
                        await _labelSwapper.SwapLabelAsync(
                            item.IssueProviderConfigId, item.IssueIdentifier,
                            AgentLabels.Error, LabelTargetKind.Issue, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Warning(ex, "ReconciliationService: label swap to agent:error failed for WorkItem {WorkItemId} (non-fatal)", item.Id);
                    }
                }

                // Release dedup guard (prevents issue from being permanently blocked from re-dispatch)
                _dedupGuard?.MarkIssueComplete(item.IssueIdentifier, item.IssueProviderConfigId);
            }

            LogTerminalTransition(item.Id, WorkItemStatus.Failed, FailureReason.Timeout,
                dispatchedAt: item.DispatchedAt);

            // Always delete the K8s Job (FailRunAsync does NOT handle this)
            if (!string.IsNullOrEmpty(item.K8sJobName))
            {
                await TryDeleteJobAsync(item.K8sJobName, ct);
            }
        }
    }

    /// <summary>
    /// Determines whether a work item has timed out based on its dispatch time and TimeoutSeconds.
    /// Uses DispatchedAt (when execution started) as the anchor, NOT CreatedAt.
    /// Exposed as internal static for unit testing.
    /// </summary>
    internal static bool IsTimedOut(DateTimeOffset dispatchedAt, int timeoutSeconds, DateTimeOffset now)
        => now >= dispatchedAt.AddSeconds(timeoutSeconds);

    /// <summary>
    /// Canary invariant: returns true only if the item is genuinely timed out AND the execution
    /// age passes a minimum plausibility threshold (60 seconds). If the execution age is below
    /// this floor, it indicates a timestamp/anchor bug — the item should NOT be killed.
    /// </summary>
    /// <remarks>
    /// This guards against regressions where timeout is computed from the wrong anchor
    /// (e.g., CreatedAt instead of DispatchedAt), which would cause items to be killed
    /// immediately after dispatch when they've been queued for a long time.
    /// </remarks>
    internal static bool ShouldEnforceTimeout(DateTimeOffset dispatchedAt, int timeoutSeconds, DateTimeOffset now)
    {
        if (!IsTimedOut(dispatchedAt, timeoutSeconds, now))
            return false;

        // Canary: execution age must be at least MinimumExecutionAgeSeconds.
        // If timeout fires but execution is < 60s, something is wrong with the anchor.
        var executionAge = (now - dispatchedAt).TotalSeconds;
        return executionAge >= MinimumExecutionAgeSeconds;
    }

    /// <summary>
    /// Minimum execution age (seconds) before a timeout enforcement is considered plausible.
    /// Any timeout that fires with less execution time than this is blocked as a canary violation.
    /// </summary>
    internal const int MinimumExecutionAgeSeconds = 60;

    // ── Consolidation Timeout ────────────────────────────────────────────

    /// <summary>
    /// Enforces timeout on consolidation runs (brain consolidation, refactoring, harness suggestions).
    /// Mirrors HeartbeatMonitor Phase 1.7 — consolidation runs that have been Running longer than
    /// AgentBusyProgressTimeout are marked as Failed. No-op when IConsolidationService is not injected.
    /// </summary>
    internal async Task EnforceConsolidationTimeoutsAsync(CancellationToken ct)
    {
        if (_consolidationService is null || _configStore is null)
            return;

        var pipelineConfig = await _configStore.LoadPipelineConfigAsync(ct);
        var progressTimeout = pipelineConfig.AgentBusyProgressTimeout;
        var now = DateTimeOffset.UtcNow;

        var runs = await _consolidationService.GetRunHistoryAsync(ct);
        var runningRuns = runs.Where(r => r.Status == ConsolidationRunStatus.Running).ToList();

        foreach (var run in runningRuns)
        {
            if (ct.IsCancellationRequested) break;

            var elapsed = now - run.StartedAtUtc;
            if (elapsed <= progressTimeout)
                continue;

            Log.Warning(
                "ReconciliationService: consolidation run {RunId} (type={Type}) exceeded progress timeout " +
                "({ElapsedMin:F0} min > {TimeoutMin:F0} min limit) — marking as Failed",
                run.RunId, run.Type, elapsed.TotalMinutes, progressTimeout.TotalMinutes);

            var failReason = $"Consolidation run exceeded progress timeout ({elapsed.TotalMinutes:F0} minutes > {progressTimeout.TotalMinutes:F0} minute limit)";
            await _consolidationService.UpdateRunAsync(run.RunId, ConsolidationRunStatus.Failed, failReason, ct);
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
        catch (DbUpdateConcurrencyException)
        {
            Log.Warning("ReconciliationService: concurrency conflict releasing PVC for WorkItem {WorkItemId}",
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
