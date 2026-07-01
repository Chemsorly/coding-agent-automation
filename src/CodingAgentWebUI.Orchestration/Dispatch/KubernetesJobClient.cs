using k8s;
using k8s.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Production implementation of <see cref="IKubernetesJobClient"/> delegating to the K8s client SDK.
/// </summary>
public sealed class KubernetesJobClient : IKubernetesJobClient
{
    private readonly IKubernetes _client;

    public KubernetesJobClient(IKubernetes client) => _client = client;

    public async Task CreateJobAsync(V1Job job, string ns, CancellationToken ct = default)
        => await _client.BatchV1.CreateNamespacedJobAsync(job, ns, cancellationToken: ct);

    public async Task DeleteJobAsync(string name, string ns, CancellationToken ct = default)
        => await _client.BatchV1.DeleteNamespacedJobAsync(name, ns, propagationPolicy: "Background", cancellationToken: ct);

    public async Task<V1Job> ReadJobAsync(string name, string ns, CancellationToken ct = default)
        => await _client.BatchV1.ReadNamespacedJobAsync(name, ns, cancellationToken: ct);

    public async Task<V1JobList> ListJobsAsync(string ns, string labelSelector, CancellationToken ct = default)
        => await _client.BatchV1.ListNamespacedJobAsync(ns, labelSelector: labelSelector, cancellationToken: ct);

    public async Task CreateSecretAsync(V1Secret secret, string ns, CancellationToken ct = default)
        => await _client.CoreV1.CreateNamespacedSecretAsync(secret, ns, cancellationToken: ct);

    public async Task<V1PodList> ListPodsAsync(string ns, string labelSelector, CancellationToken ct = default)
        => await _client.CoreV1.ListNamespacedPodAsync(ns, labelSelector: labelSelector, cancellationToken: ct);
}
