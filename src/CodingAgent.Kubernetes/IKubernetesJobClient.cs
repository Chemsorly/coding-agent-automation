using k8s.Models;

namespace CodingAgent.Kubernetes;

/// <summary>
/// Thin abstraction over K8s API calls used by DispatchService and ReconciliationService.
/// Enables unit testing without mocking the version-dependent KubernetesClient method signatures.
/// </summary>
public interface IKubernetesJobClient
{
    Task CreateJobAsync(V1Job job, string ns, CancellationToken ct = default);
    Task DeleteJobAsync(string name, string ns, CancellationToken ct = default);
    Task<V1Job> ReadJobAsync(string name, string ns, CancellationToken ct = default);
    Task<V1JobList> ListJobsAsync(string ns, string labelSelector, CancellationToken ct = default);
    Task CreateSecretAsync(V1Secret secret, string ns, CancellationToken ct = default);
    Task DeleteSecretAsync(string name, string ns, CancellationToken ct = default);
    /// <summary>
    /// Patches the <c>ownerReferences</c> metadata field of an existing K8s Secret using a JSON merge patch.
    /// Used to set the <c>OwnerReference</c> on the per-job Secret after the Job is created.
    /// </summary>
    Task PatchSecretOwnerReferenceAsync(string name, string ns, V1OwnerReference ownerReference, CancellationToken ct = default);
    Task<V1PodList> ListPodsAsync(string ns, string labelSelector, CancellationToken ct = default);
    Task<string> ReadPodLogsAsync(string podName, string ns, CancellationToken ct = default);
}
