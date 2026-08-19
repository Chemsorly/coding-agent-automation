using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests startup behavior for WorkDistributionRegistration after Spec 041.
/// Kubernetes is the only supported mode — no mode parameter exists.
/// </summary>
[Collection("EnvironmentVariables")]
public class WorkDistributionRegistrationTests
{
    [Fact]
    public void AddWorkDistribution_WithDbHost_RegistersIKubernetesDescriptor()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Host"] = "localhost",
                ["Database:Name"] = "test",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWorkDistribution(config);

        // The IKubernetes descriptor must be present (factory-based, deferred)
        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(k8s.IKubernetes));
        descriptor.Should().NotBeNull("AddWorkDistribution must register IKubernetes");
    }

    [Fact]
    public void AddWorkDistribution_WithDbHost_RegistersKubernetesWorkDistributorDescriptor()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Host"] = "localhost",
                ["Database:Name"] = "test",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWorkDistribution(config);

        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(CodingAgentWebUI.Pipeline.Interfaces.IWorkDistributor));
        descriptor.Should().NotBeNull("AddWorkDistribution must register IWorkDistributor");
    }

    [Fact]
    public void RegisterConsolidationServices_ViaReflection_RegistersIKubernetesAndRelatedServices()
    {
        // Calls the private static RegisterConsolidationServices method directly via reflection
        // to exercise the DI registration lambdas without requiring a real Kubernetes cluster.
        // Verifies that registrations are ADDED to the container (not resolved).
        // This method replaced RegisterKubernetesMode after Spec 043 Task 9.
        var configData = new Dictionary<string, string?>
        {
            ["WorkDistribution:Namespace"] = "default",
            ["WorkDistribution:OrchestratorUrl"] = "http://orchestrator:8080",
            ["WorkDistribution:AgentApiKeySecretName"] = "agent-api-key",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services.AddLogging();

        var method = typeof(WorkDistributionRegistration)
            .GetMethod("RegisterConsolidationServices",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        method.Should().NotBeNull("RegisterConsolidationServices must exist as a private static method");
        method!.Invoke(null, [services, config]);

        var pendingWorkQueryDescriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(CodingAgentWebUI.Pipeline.Interfaces.IPendingWorkQuery));
        pendingWorkQueryDescriptor.Should().NotBeNull(
            "RegisterConsolidationServices must register IPendingWorkQuery as a singleton");
        pendingWorkQueryDescriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }
}
