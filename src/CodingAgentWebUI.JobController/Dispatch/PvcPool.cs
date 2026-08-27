using CodingAgentWebUI.Kubernetes;
using Serilog;

namespace CodingAgentWebUI.JobController.Dispatch;

/// <summary>
/// In-memory pool of PVC names for kiro agent credential pools.
/// Pool state is non-durable: rebuilt from live K8s Job labels on startup.
/// Thread-safe via lock for concurrent dispatch cycles.
/// </summary>
public sealed class PvcPool
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<PvcPool>();

    private readonly List<string> _all;
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public PvcPool(IEnumerable<string> pvcs)
    {
        _all = [.. pvcs];
    }

    /// <summary>
    /// Returns the count of PVCs in the pool.
    /// </summary>
    public int TotalCount => _all.Count;

    /// <summary>
    /// Returns the count of currently available (unclaimed) PVCs.
    /// </summary>
    public int AvailableCount
    {
        get
        {
            lock (_lock)
            {
                return _all.Count - _claimed.Count;
            }
        }
    }

    /// <summary>
    /// Claims the first available PVC. Returns null if none are available.
    /// Thread-safe.
    /// </summary>
    public string? TryClaim(Guid workItemId)
    {
        lock (_lock)
        {
            var available = _all.FirstOrDefault(p => !_claimed.Contains(p));
            if (available is null)
            {
                Log.Warning("PVC pool exhausted for workItem {WorkItemId} — no PVCs available", workItemId);
                return null;
            }
            _claimed.Add(available);
            Log.Debug("PVC {Pvc} claimed for workItem {WorkItemId}", available, workItemId);
            return available;
        }
    }

    /// <summary>
    /// Releases a previously claimed PVC back to the pool.
    /// </summary>
    public void Release(string pvcName)
    {
        if (string.IsNullOrEmpty(pvcName)) return;
        lock (_lock)
        {
            if (_claimed.Remove(pvcName))
                Log.Debug("PVC {Pvc} released", pvcName);
        }
    }

    /// <summary>
    /// Rebuilds the pool's claimed-set in-place from the PVC volumes on live K8s Jobs.
    /// Called on each leadership acquisition to re-sync after a controller restart.
    /// Thread-safe: clears and repopulates _claimed under lock.
    /// </summary>
    public async Task RebuildFromLiveJobsAsync(
        IKubernetesJobClient k8sClient,
        DispatchServiceOptions opts,
        CancellationToken ct)
    {
        if (_all.Count == 0) return;
        try
        {
            var jobs = await k8sClient.ListJobsAsync(
                opts.Namespace,
                "app.kubernetes.io/managed-by=caa-orchestrator",
                ct);

            var claimedPvcs = jobs.Items
                .SelectMany(j => j.Spec?.Template?.Spec?.Volumes ?? [])
                .Where(v => v.PersistentVolumeClaim?.ClaimName is not null)
                .Select(v => v.PersistentVolumeClaim!.ClaimName!)
                .Where(name => _all.Contains(name))
                .Distinct()
                .ToList();

            lock (_lock)
            {
                _claimed.Clear();
                foreach (var pvc in claimedPvcs)
                    _claimed.Add(pvc);
            }

            Log.Information("PvcPool rebuilt from live Jobs: total={Total}, claimed={Claimed}", _all.Count, claimedPvcs.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PvcPool rebuild from live Jobs failed; clearing claimed set to unblock dispatch (pool may briefly over-claim)");
            lock (_lock)
            {
                _claimed.Clear();
            }
        }
    }

    /// <summary>
    /// Rebuilds the pool's claimed-set from the labels on live K8s Jobs.
    /// Called once on startup to re-sync after a restart.
    /// </summary>
    public static async Task<PvcPool> BuildFromLiveJobsAsync(
        IKubernetesJobClient k8sClient,
        DispatchServiceOptions opts,
        CancellationToken ct)
    {
        var pool = new PvcPool(opts.KiroPvcPool);
        if (pool.TotalCount == 0)
            return pool;

        try
        {
            var jobs = await k8sClient.ListJobsAsync(
                opts.Namespace,
                "app.kubernetes.io/managed-by=caa-orchestrator",
                ct);

            var claimedPvcs = jobs.Items
                .SelectMany(j => j.Spec?.Template?.Spec?.Volumes ?? [])
                .Where(v => v.PersistentVolumeClaim?.ClaimName is not null)
                .Select(v => v.PersistentVolumeClaim!.ClaimName!)
                .Where(name => pool._all.Contains(name))
                .Distinct()
                .ToList();

            lock (pool._lock)
            {
                foreach (var pvc in claimedPvcs)
                    pool._claimed.Add(pvc);
            }

            Log.Information("PVC pool rebuilt from live Jobs: total={Total}, claimed={Claimed}",
                pool.TotalCount, claimedPvcs.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to rebuild PVC pool from live Jobs; starting with empty claimed set");
        }

        return pool;
    }
}
