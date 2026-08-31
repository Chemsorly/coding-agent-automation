using CodingAgentWebUI.Kubernetes;
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

        var claimedNames = jobs.Items
            .SelectMany(j => j.Spec?.Template?.Spec?.Volumes ?? [])
            .Where(v => v.PersistentVolumeClaim?.ClaimName is not null)
            .Select(v => v.PersistentVolumeClaim!.ClaimName!)
            .ToHashSet(StringComparer.Ordinal);

        return kiroPvcPool.FirstOrDefault(p => !claimedNames.Contains(p));
    }
}
