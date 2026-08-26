using k8s;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using KubernetesClient = k8s.Kubernetes;

namespace CodingAgentWebUI.Kubernetes;

/// <summary>
/// Extension methods for registering the Kubernetes client with explicit in-cluster-first logic.
/// See 041 Req 5.9: BuildDefaultConfig() precedence is KUBECONFIG → ~/.kube/config → in-cluster → localhost,
/// so a stray kubeconfig in a pod would silently redirect Job creation to the wrong cluster.
/// We force the in-cluster path when the service account token is present.
/// </summary>
public static class KubernetesClientRegistration
{
    /// <summary>
    /// Registers <see cref="IKubernetes"/> as a singleton using the explicit in-cluster-first branch.
    /// Fails fast (throws <see cref="InvalidOperationException"/>) when no usable configuration is found.
    /// </summary>
    public static IServiceCollection AddKubernetesClient(this IServiceCollection services)
    {
        services.AddSingleton<IKubernetes>(_ =>
        {
            var inCluster = KubernetesClientConfiguration.IsInCluster();
            var config = inCluster
                ? KubernetesClientConfiguration.InClusterConfig()
                : KubernetesClientConfiguration.BuildDefaultConfig();

            // Unconditional, Information level. This line is the only thing that would ever reveal
            // an accidental kubeconfig taking over in a cluster deployment.
            Log.Information("Kubernetes client configured: Source={Source} Host={Host}",
                inCluster ? "in-cluster" : "kubeconfig", config.Host);

            // "http://localhost:8080" is the exact fallback BuildDefaultConfig() returns when
            // no configuration source is found (no KUBECONFIG, no ~/.kube/config, not in-cluster).
            // It is NOT a general localhost filter — variant forms like http://localhost or
            // 127.0.0.1 would slip through and fail at first API call instead of here.
            if (string.IsNullOrEmpty(config.Host) || config.Host == "http://localhost:8080")
            {
                Log.Fatal("No usable Kubernetes configuration. In-cluster: ensure the service account " +
                          "token is mounted. Outside a cluster: set KUBECONFIG or provide ~/.kube/config.");
                throw new InvalidOperationException("No usable Kubernetes configuration.");
            }

            return new KubernetesClient(config);
        });

        return services;
    }
}
