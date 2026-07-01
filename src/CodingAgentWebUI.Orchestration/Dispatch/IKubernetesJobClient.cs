using k8s.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

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
    Task<V1PodList> ListPodsAsync(string ns, string labelSelector, CancellationToken ct = default);
}
