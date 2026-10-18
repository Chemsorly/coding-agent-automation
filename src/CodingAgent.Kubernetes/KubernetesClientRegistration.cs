using k8s;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using KubernetesClient = k8s.Kubernetes;

namespace CodingAgent.Kubernetes;

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

    /// <summary>
    /// Attempts to create an <see cref="IKubernetes"/> client using the explicit in-cluster-first
    /// branch. Returns <see langword="null"/> when no usable configuration is found, so the caller
    /// can degrade gracefully instead of failing fast.
    /// </summary>
    /// <param name="callerLabel">
    /// A short label (e.g. "API" or "Scheduler") prepended to every log message so that
    /// originating host remains distinguishable in aggregated logs.
    /// </param>
    /// <param name="callerNote">
    /// Optional additional context appended to the catch-path Warning log. Use this to document
    /// host-specific consequences of K8s being unavailable (e.g. "DatabaseMaintenanceService sweep
    /// runs ungated."). Pass <see langword="null"/> when no additional context is needed.
    /// </param>
    /// <returns>
    /// A configured <see cref="IKubernetes"/> instance, or <see langword="null"/> when the host
    /// is empty, equals the <c>http://localhost:8080</c> fallback sentinel, or an exception is
    /// thrown during configuration resolution.
    /// </returns>
    public static IKubernetes? TryCreate(string callerLabel, string? callerNote = null)
    {
        ArgumentNullException.ThrowIfNull(callerLabel);
        try
        {
            var inCluster = KubernetesClientConfiguration.IsInCluster();
            var config = inCluster
                ? KubernetesClientConfiguration.InClusterConfig()
                : KubernetesClientConfiguration.BuildDefaultConfig();

            // "http://localhost:8080" is the exact fallback BuildDefaultConfig() returns when
            // no configuration source is found. Log the warning before returning null so the
            // Information log is only emitted for usable configurations (mirrors Scheduler semantics).
            if (string.IsNullOrEmpty(config.Host) || config.Host == "http://localhost:8080")
            {
                Log.Warning("{CallerLabel}: Kubernetes client host is empty or localhost — K8s unavailable.",
                    callerLabel);
                return null;
            }

            Log.Information("{CallerLabel}: Kubernetes client configured ({Source}) Host={Host}",
                callerLabel, inCluster ? "in-cluster" : "kubeconfig", config.Host);

            return new KubernetesClient(config);
        }
        catch (Exception ex)
        {
            var note = callerNote is not null ? $" {callerNote}" : string.Empty;
            Log.Warning(ex, "{CallerLabel}: Kubernetes client unavailable — leader election inactive.{Note}",
                callerLabel, note);
            return null;
        }
    }
}
