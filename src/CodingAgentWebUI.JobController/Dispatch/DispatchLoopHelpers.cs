using CodingAgentWebUI.Kubernetes;
using k8s.Models;
using Serilog;

namespace CodingAgentWebUI.JobController.Dispatch;

/// <summary>
/// Shared K8s query helpers used by both <see cref="DispatchLoop"/> and
/// <see cref="ConsolidationDispatchLoop"/>. Extracted to eliminate duplicate logic.
/// </summary>
internal static class DispatchLoopHelpers
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext(typeof(DispatchLoopHelpers));

    /// <summary>
    /// Queries live K8s Jobs and builds a map of agent-selector → active job count.
    /// Only counts jobs in an active execution phase — completed/failed jobs within the
    /// log-retention window are excluded so they do not inflate the concurrency count
    /// and block dispatch of new work items (issue #2176).
    /// </summary>
    internal static async Task<Dictionary<string, int>> BuildConcurrencyMapAsync(
        IKubernetesJobClient k8sClient,
        string @namespace,
        string callerName,
        CancellationToken ct)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var jobs = await k8sClient.ListJobsAsync(
                @namespace,
                "app.kubernetes.io/managed-by=caa-orchestrator",
                ct);

            foreach (var job in jobs.Items)
            {
                // Skip completed/failed jobs still present within the log-retention window
                // (CleanupOrphansAsync keeps them for 600s so kubectl logs remain accessible).
                // Counting terminal jobs as active would inflate the concurrency count and block
                // dispatch of new work items even though no agent is running (issue #2176).
                if (IsJobTerminal(job)) continue;

                var labels = job.Metadata?.Labels;
                var selectorLabel = labels is not null && labels.TryGetValue("caa/agent-selector", out var lv) ? lv : "";
                if (string.IsNullOrEmpty(selectorLabel)) continue;

                // Label stores dots (e.g. "dotnet10.kiro"); convert back to comma-separated form
                var normalizedSelector = selectorLabel.Replace('.', ',');
                var key = JobTemplateStore.NormalizeLabels(normalizedSelector);
                map[key] = (map.TryGetValue(key, out var cnt) ? cnt : 0) + 1;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Caller}: failed to build concurrency map from live Jobs; proceeding with empty map", callerName);
        }
        return map;
    }

    /// <summary>
    /// Returns <c>true</c> if the K8s Job has reached a terminal phase (Complete or Failed).
    /// Terminal jobs within the log-retention window must not count toward the concurrency limit.
    /// Mirrors the phase-detection logic in <c>ReconciliationLoop.GetJobPhase</c>.
    /// </summary>
    internal static bool IsJobTerminal(V1Job job)
    {
        var conditions = job.Status?.Conditions;
        if (conditions is not null)
        {
            // V1JobCondition.Type and V1JobCondition.Status are both nullable strings (string?)
            // in the Kubernetes C# client. The LINQ comparisons (c.Type == "Complete", c.Status == "True")
            // evaluate safely to false when null (no NullReferenceException) because string equality
            // with null returns false in C#.
            if (conditions.Any(c => (c.Type == "Complete" || c.Type == "Failed") && c.Status == "True"))
                return true;
        }
        // Fall back to counters when conditions are not yet populated.
        // NOTE: Only treat Failed > 0 as terminal when there are no active pods — a job whose
        // first pod attempt failed still has Failed=1 while Kubernetes is creating the next retry
        // pod (Active=1). The "Failed" condition type is only set once all retries are exhausted,
        // so relying on Failed > 0 alone would falsely evict a retrying job from the concurrency map.
        // Succeeded > 0 is always terminal (a succeeded job never retries).
        if ((job.Status?.Succeeded ?? 0) > 0) return true;
        return (job.Status?.Failed ?? 0) > 0 && (job.Status?.Active ?? 0) == 0;
    }

    /// <summary>
    /// Queries live K8s Jobs to find the first PVC name from the configured pool that is
    /// not already mounted by a running Job. Returns <c>null</c> if all configured PVCs
    /// are claimed or the pool is empty.
    /// Must be called under the <see cref="PvcSelectLock"/>.
    /// </summary>
    internal static async Task<string?> SelectAvailablePvcAsync(
        IKubernetesJobClient k8sClient,
        string @namespace,
        IReadOnlyList<string> kiroPvcPool,
        CancellationToken ct)
    {
        if (kiroPvcPool.Count == 0) return null;

        var jobs = await k8sClient.ListJobsAsync(
            @namespace,
            "app.kubernetes.io/managed-by=caa-orchestrator",
            ct);

        // Note: this query does not filter terminal jobs. A completed job within the K8s log-retention
        // window (~600s) still has its PVC mounted in the spec, causing this method to treat that PVC
        // as occupied and return null — starving the pool for up to 600s even though BuildConcurrencyMapAsync
        // correctly excludes the terminal job from the count. A follow-up fix should filter using
        // IsJobTerminal before building claimedNames, mirroring the approach in BuildConcurrencyMapAsync.
        var claimedNames = jobs.Items
            .SelectMany(j => j.Spec?.Template?.Spec?.Volumes ?? [])
            .Where(v => v.PersistentVolumeClaim?.ClaimName is not null)
            .Select(v => v.PersistentVolumeClaim!.ClaimName!)
            .ToHashSet(StringComparer.Ordinal);

        return kiroPvcPool.FirstOrDefault(p => !claimedNames.Contains(p));
    }
}
