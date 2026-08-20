using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

public sealed partial class ReconciliationService
{
    // ── Timeout Enforcement ──────────────────────────────────────────────

    /// <summary>
    /// Grace period (seconds) after a K8s Job completes before assuming the agent never reported back.
    /// Prevents false positives during normal agent shutdown (agent exits → K8s marks Complete → POST arrives 1-2s later).
    /// Declared here (Timeout) so timeout-related constants are co-located. Both Watch and Sweep read this
    /// constant via partial class member sharing.
    /// </summary>
    internal const int CompleteJobGracePeriodSeconds = 30;

    /// <summary>
    /// Minimum execution age (seconds) before a timeout enforcement is considered plausible.
    /// Any timeout that fires with less execution time than this is blocked as a canary violation.
    /// </summary>
    internal const int MinimumExecutionAgeSeconds = 60;

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
            .WhereActive()
            .Where(w => w.TimeoutSeconds > 0)
            .Select(w => new { w.Id, w.DispatchedAt, w.CreatedAt, w.TimeoutSeconds, w.K8sJobName, w.LastProgressAt, w.IssueIdentifier, w.IssueProviderConfigId })
            .ToListAsync(ct);

        foreach (var item in candidates)
        {
            if (ct.IsCancellationRequested) break;

            var anchor = item.LastProgressAt ?? item.DispatchedAt ?? item.CreatedAt;
            if (!IsTimedOut(anchor, item.TimeoutSeconds, now))
                continue;

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

            WorkDistributionTelemetry.TimeoutExecutionAge.Record(executionAge);

            Log.Warning("ReconciliationService: timeout — WorkItem {WorkItemId} exceeded {Timeout}s",
                item.Id, item.TimeoutSeconds);

            await TimeoutSingleWorkItemAsync(item.Id, item.DispatchedAt, item.IssueIdentifier, item.IssueProviderConfigId, item.K8sJobName, item.TimeoutSeconds, ct);
        }
    }

    private async Task TimeoutSingleWorkItemAsync(
        Guid workItemId, DateTimeOffset? dispatchedAt,
        string issueIdentifier, string issueProviderConfigId,
        string? k8sJobName, int timeoutSeconds,
        CancellationToken ct)
    {
        var timeoutReason = $"Timeout exceeded: {timeoutSeconds}s";

        var lifecycleHandled = await TryLifecycleFailAsync(workItemId.ToString(), timeoutReason, ct);

        if (!lifecycleHandled)
        {
            await DirectTimeoutFallbackAsync(workItemId, issueIdentifier, issueProviderConfigId, timeoutReason, ct);
        }

        LogTerminalTransition(workItemId, WorkItemStatus.Failed, FailureReason.Timeout,
            dispatchedAt: dispatchedAt);

        // Always delete the K8s Job (FailRunAsync does NOT handle this)
        if (!string.IsNullOrEmpty(k8sJobName))
        {
            await TryDeleteJobAsync(k8sJobName, ct);
        }
    }

    private async Task<bool> TryLifecycleFailAsync(string jobId, string timeoutReason, CancellationToken ct)
    {
        // Try full lifecycle cleanup first (label swap, history, dedup, agent state).
        // FailRunAsync uses the in-memory PipelineRun — returns null if not in memory (e.g., different replica).
        if (_lifecycleManager is null)
            return false;

        var result = await _lifecycleManager.FailRunAsync(jobId, timeoutReason, ct, FailureReason.Timeout);
        return result is not null;
    }

    private async Task DirectTimeoutFallbackAsync(
        Guid workItemId, string issueIdentifier, string issueProviderConfigId,
        string timeoutReason, CancellationToken ct)
    {
        // Fallback: direct DB transition + best-effort label swap + dedup release
        await _transitionService.TransitionAsync(workItemId, WorkItemStatus.Failed,
            WorkItemMutationFactory.Failed(
                errorMessage: timeoutReason,
                failureReason: FailureReason.Timeout),
            ct: ct);

        // Best-effort label swap to agent:error (prevents stale agent:in-progress on GitHub)
        if (_labelService is not null)
        {
            try
            {
                await _labelService.SwapLabelAsync(
                    issueProviderConfigId, issueIdentifier,
                    AgentLabels.Error, LabelTargetKind.Issue, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "ReconciliationService: label swap to agent:error failed for WorkItem {WorkItemId} (non-fatal)", workItemId);
            }
        }

        // Release dedup guard (prevents issue from being permanently blocked from re-dispatch)
        _dedupGuard?.MarkIssueComplete(issueIdentifier, issueProviderConfigId);
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
}
