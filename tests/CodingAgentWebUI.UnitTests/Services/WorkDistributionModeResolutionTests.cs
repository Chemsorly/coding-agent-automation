using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests verifying that AddWorkDistribution registers the correct K8s services.
/// After Spec 041, Kubernetes is the only supported work distribution mode.
/// </summary>
public class WorkDistributionModeResolutionTests
{
    // ── Kubernetes Mode ─────────────────────────────────────────────────

    [Fact]
    public void KubernetesMode_Registers_KubernetesWorkDistributor_Descriptor()
    {
        // Arrange: DB host set (K8s is the only mode)
        var config = BuildConfig("localhost");
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddWorkDistribution(config);

        // Assert: IWorkDistributor descriptor exists
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IWorkDistributor));
        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void KubernetesMode_WorkItemTransitionService_RegisteredInApiOnly()
    {
        // T8 (arch-audit 2026-08-22): WorkItemTransitionService was moved out of the monolith's
        // AddWorkDistribution and into the API host (ApiServiceCollectionExtensions.AddApiInfrastructure).
        // The monolith no longer registers it — verified.
        var config = BuildConfig("localhost");
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWorkDistribution(config);

        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(CodingAgentWebUI.Infrastructure.Persistence.Services.WorkItemTransitionService));
        descriptor.Should().BeNull("WorkItemTransitionService is now registered in the API host only");
    }

    [Fact]
    public void KubernetesMode_IPendingWorkQuery_WasRemoved()
    {
        // Spec 045 Req 1.2 (M1 gauge audit): IPendingWorkQuery (DbPendingWorkQuery) was
        // removed from AddWorkDistribution because dispatch.queue.depth gauge was backed by
        // IDbContextFactory. No PrometheusRule alerts reference this metric, so removal is safe.
        // ObservableGaugeRegistrationExtensions no longer registers dispatch.queue.depth.
        var configData = new Dictionary<string, string?>
        {
            ["Database:Host"] = "localhost",
            ["Database:Name"] = "testdb",
            ["WorkDistribution:Namespace"] = "default",
            ["WorkDistribution:OrchestratorUrl"] = "http://orchestrator:8080",
            ["WorkDistribution:AgentApiKeySecretName"] = "agent-api-key",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWorkDistribution(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IPendingWorkQuery));
        descriptor.Should().BeNull("IPendingWorkQuery was removed in Spec 045 Req 1.2 (M1 gauge audit)");
    }

    // ── Order-independence (IPipelineRunHistoryService) ─────────────────

    [Fact]
    public void DbMode_OnlyPostgresHistoryDescriptor_RegardlessOfRegistrationOrder()
    {
        var config = BuildConfig("localhost");
        var services = new ServiceCollection();
        services.AddLogging();

        // Reversed order
        services.AddWorkDistribution(config);
        services.AddPipelineCoreServices();

        var descriptors = services.Where(d => d.ServiceType == typeof(IPipelineRunHistoryService)).ToList();
        descriptors.Should().HaveCount(1,
            "Only Postgres registration should exist — AddPipelineCoreServices must skip the in-memory registration");
    }

    [Fact]
    public void DbMode_OnlyPostgresHistoryDescriptor_NormalOrder()
    {
        var config = BuildConfig("localhost");
        var services = new ServiceCollection();
        services.AddLogging();

        // Normal order
        services.AddPipelineCoreServices();
        services.AddWorkDistribution(config);

        var descriptors = services.Where(d => d.ServiceType == typeof(IPipelineRunHistoryService)).ToList();
        descriptors.Should().HaveCount(1,
            "Only Postgres registration should exist — AddPipelineCoreServices must skip the in-memory registration");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static IConfiguration BuildConfig(string dbHost)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Host"] = dbHost,
                ["Database:Name"] = "testdb",
            })
            .Build();
    }
}
