using AwesomeAssertions;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Regression tests for the IKubernetes factory registered by RegisterKubernetesMode.
/// These tests invoke the factory descriptor directly (ImplementationFactory!(provider))
/// to bypass the AddWorkDistribution in-cluster guard, which is removed in Task 7.1.
///
/// Test (a) — no token, no kubeconfig → factory throws InvalidOperationException (Req 5.9b).
/// Test (b) — KUBECONFIG set AND in-cluster token present → in-cluster wins (Req 5.9).
///
/// Validates: Requirements 5.9, 5.9a
/// </summary>
[Collection("EnvironmentVariables")]
public class KubernetesClientConfigurationTests
{
    private static ServiceDescriptor GetKubernetesDescriptor(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Call RegisterConsolidationServices directly to bypass the startup guard.
        // This method replaced RegisterKubernetesMode after Spec 043 Task 9.
        var method = typeof(WorkDistributionRegistration)
            .GetMethod("RegisterConsolidationServices",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("RegisterConsolidationServices must exist");
        method!.Invoke(null, [services, config]);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IKubernetes));
        descriptor.Should().NotBeNull("RegisterConsolidationServices must register IKubernetes");
        descriptor!.ImplementationFactory.Should().NotBeNull(
            "IKubernetes must be registered via a factory (deferred, so it runs at resolve-time)");

        return descriptor;
    }

    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Namespace"] = "default",
                ["WorkDistribution:OrchestratorUrl"] = "http://orchestrator:8080",
                ["WorkDistribution:AgentApiKeySecretName"] = "agent-api-key",
            })
            .Build();

    /// <summary>
    /// (a) When no service-account token is present and no kubeconfig is available,
    /// resolving IKubernetes must throw InvalidOperationException with a clear message —
    /// NOT return a client pointed at localhost:8080 (Req 5.9b).
    /// </summary>
    [Fact]
    [Trait("Category", "RequiresNonK8sEnvironment")]
    public void IKubernetesFactory_NoTokenNoKubeconfig_ThrowsWithClearMessage()
    {
        // Skip when running inside a real cluster (service account token is mounted)
        if (KubernetesClientConfiguration.IsInCluster())
            return;

        var originalKubeconfig = Environment.GetEnvironmentVariable("KUBECONFIG");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");

        try
        {
            // Point HOME and USERPROFILE to a temp directory that has no .kube/config,
            // and clear KUBECONFIG so BuildDefaultConfig() finds nothing.
            var emptyHome = Path.Combine(Path.GetTempPath(), $"kube-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(emptyHome);

            Environment.SetEnvironmentVariable("KUBECONFIG", null);
            Environment.SetEnvironmentVariable("HOME", emptyHome);
            Environment.SetEnvironmentVariable("USERPROFILE", emptyHome);

            var descriptor = GetKubernetesDescriptor(BuildConfig());

            using var sp = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider();

            // Act — invoke the factory directly
            var act = () => descriptor.ImplementationFactory!(sp);

            // Assert — must throw, not return a localhost-pointed client
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*No usable Kubernetes configuration*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUBECONFIG", originalKubeconfig);
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
        }
    }

    /// <summary>
    /// (b) When KUBECONFIG is set AND an in-cluster token is present,
    /// the in-cluster path must win — kubeconfig must NOT take precedence.
    ///
    /// This test pins the precedence correction in Req 5.9: our explicit IsInCluster()
    /// check means in-cluster wins over KUBECONFIG, the opposite of BuildDefaultConfig() bare.
    ///
    /// Only meaningful inside a Kubernetes cluster — skipped otherwise.
    /// </summary>
    [Fact]
    [Trait("Category", "RequiresK8sEnvironment")]
    public void IKubernetesFactory_KubeconfigSetAndInClusterToken_InClusterWins()
    {
        // Only runs inside a cluster where the service account token is mounted
        if (!KubernetesClientConfiguration.IsInCluster())
            return;

        // Arrange: set a KUBECONFIG pointing at a non-existent / wrong-cluster file
        // so that if BuildDefaultConfig() bare were called it would pick the wrong cluster
        var fakeKubeconfig = Path.Combine(Path.GetTempPath(), $"fake-kubeconfig-{Guid.NewGuid():N}");
        File.WriteAllText(fakeKubeconfig, """
            apiVersion: v1
            kind: Config
            clusters:
            - cluster:
                server: https://wrong-cluster.example.com:6443
              name: wrong
            contexts:
            - context:
                cluster: wrong
                user: wrong-user
              name: wrong-context
            current-context: wrong-context
            users:
            - name: wrong-user
              user:
                token: fake-token
            """);

        var originalKubeconfig = Environment.GetEnvironmentVariable("KUBECONFIG");
        try
        {
            Environment.SetEnvironmentVariable("KUBECONFIG", fakeKubeconfig);

            var descriptor = GetKubernetesDescriptor(BuildConfig());
            using var sp = new ServiceCollection().AddLogging().BuildServiceProvider();

            // Act — invoke the factory with KUBECONFIG set to a wrong-cluster file
            var kubernetes = (IKubernetes)descriptor.ImplementationFactory!(sp);

            // Assert — the resolved client must use the in-cluster host, not the fake kubeconfig host
            kubernetes.Should().NotBeNull();
            // The in-cluster host is the K8s API server (not wrong-cluster.example.com or localhost)
            var host = kubernetes.BaseUri.Host;
            host.Should().NotBe("wrong-cluster.example.com",
                "in-cluster must win over KUBECONFIG when both are present");
            host.Should().NotBe("localhost",
                "factory must not fall back to localhost when in-cluster token is present");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUBECONFIG", originalKubeconfig);
            if (File.Exists(fakeKubeconfig))
                File.Delete(fakeKubeconfig);
        }
    }
}
