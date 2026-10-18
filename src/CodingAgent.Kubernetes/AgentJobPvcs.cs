using k8s.Models;

namespace CodingAgent.Kubernetes;

/// <summary>
/// The credential PVCs (<see cref="DispatchServiceOptions.KiroPvcPool"/>) that agent Jobs hold. A pool PVC is
/// ReadWriteOnce: while an agent Job that mounts it has not ended, a pod of another Job that mounts it cannot
/// start. Pipeline, consolidation, triage and chat Jobs all mount it the same way (<see cref="JobSpecBuilder"/>),
/// so the Jobs themselves tell every dispatch path which PVCs are taken, whichever path created them.
/// </summary>
public static class AgentJobPvcs
{
    /// <summary>The label selector of every agent Job (<see cref="JobSpecBuilder"/>).</summary>
    public const string AgentJobSelector = "app.kubernetes.io/component=agent-job";

    /// <summary>Lists the PVCs that the agent Jobs of <paramref name="ns"/> that have not ended mount.</summary>
    public static async Task<IReadOnlySet<string>> ListHeldAsync(IKubernetesJobClient client, string ns, CancellationToken ct)
    {
        var jobs = await client.ListJobsAsync(ns, AgentJobSelector, ct);
        return HeldBy(jobs?.Items ?? []);
    }

    private static HashSet<string> HeldBy(IEnumerable<V1Job> jobs) =>
        jobs.Where(j => !KubernetesJobConditions.IsTerminal(j))
            .SelectMany(j => j.Spec?.Template?.Spec?.Volumes ?? [])
            .Select(v => v.PersistentVolumeClaim?.ClaimName)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
}
