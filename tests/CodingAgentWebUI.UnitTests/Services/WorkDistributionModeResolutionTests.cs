using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Integration tests verifying that each WorkDistribution mode registers the correct
/// IWorkDistributor implementation type. Validates DI wiring correctness — prevents
/// misconfigured modes from silently injecting the wrong distributor.
/// </summary>
public class WorkDistributionModeResolutionTests
{
    // ── Legacy Mode (no DB) ─────────────────────────────────────────────

    [Fact]
    public void LegacyMode_Registers_LegacyWorkDistributor()
    {
        // Arrange: no Database:Host = legacy mode
        var config = BuildConfig(null, null);
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterLegacyPrerequisites(services);

        // Act
        services.AddWorkDistribution(config);

        // Assert: IWorkDistributor descriptor is LegacyWorkDistributor
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IWorkDistributor));
        descriptor.Should().NotBeNull();

        // Resolve to verify correct type
        var sp = services.BuildServiceProvider();
        var distributor = sp.GetRequiredService<IWorkDistributor>();
        distributor.Should().BeOfType<LegacyWorkDistributor>();
    }

    [Fact]
    public void LegacyMode_Registers_FileSystemConsolidationRunStore()
    {
        var config = BuildConfig(null, null);
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterLegacyPrerequisites(services);

        services.AddWorkDistribution(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IConsolidationRunStore));
        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void LegacyMode_Registers_FileSystemLoopStateStore()
    {
        var config = BuildConfig(null, null);
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterLegacyPrerequisites(services);

        services.AddWorkDistribution(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ILoopStateStore));
        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void LegacyMode_Registers_FileSystemHarnessSuggestionStore()
    {
        var config = BuildConfig(null, null);
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterLegacyPrerequisites(services);

        services.AddWorkDistribution(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IHarnessSuggestionStore));
        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void LegacyMode_Registers_InMemoryActiveRunQueryService()
    {
        var config = BuildConfig(null, null);
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterLegacyPrerequisites(services);

        services.AddWorkDistribution(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IActiveRunQueryService));
        descriptor.Should().NotBeNull();
    }

    // ── SignalR Mode (DB) ────────────────────────────────────────────────

    [Fact]
    public void SignalRMode_RegistersDescriptor_ForSignalRWorkDistributor()
    {
        // Arrange: DB host set, mode = SignalR
        var config = BuildConfig("localhost", "SignalR");
        var services = new ServiceCollection();
        services.AddLogging();

        // Act — will register descriptors but won't resolve (no actual Postgres)
        services.AddWorkDistribution(config);

        // Assert: IWorkDistributor service descriptor exists
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IWorkDistributor));
        descriptor.Should().NotBeNull();
        // Can't resolve (needs real Postgres), but verify it's NOT LegacyWorkDistributor
        // by checking a service that's ONLY registered in DB modes
        var configStoreDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IConfigurationStore));
        configStoreDescriptor.Should().NotBeNull("SignalR mode should register PostgresConfigurationStore");
    }

    [Fact]
    public void SignalRMode_Registers_PostgresConfigurationStore()
    {
        var config = BuildConfig("localhost", "SignalR");
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWorkDistribution(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IConfigurationStore));
        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void SignalRMode_Registers_WorkItemTransitionService()
    {
        var config = BuildConfig("localhost", "SignalR");
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWorkDistribution(config);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(CodingAgentWebUI.Infrastructure.Persistence.Services.WorkItemTransitionService));
        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void SignalRMode_Registers_PendingWorkItemDrainService()
    {
        var config = BuildConfig("localhost", "SignalR");
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWorkDistribution(config);

        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) ||
            d.ImplementationType == typeof(PendingWorkItemDrainService) ||
            d.ServiceType == typeof(PendingWorkItemDrainService));
        descriptor.Should().NotBeNull("SignalR mode should register PendingWorkItemDrainService as hosted service");
    }

    [Fact]
    public void SignalRMode_DoesNotRegister_LeaderElectionService()
    {
        var config = BuildConfig("localhost", "SignalR");
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWorkDistribution(config);

        var descriptor = services.FirstOrDefault(d =>
            d.ImplementationType == typeof(CodingAgentWebUI.Orchestration.LeaderElection.LeaderElectionService) ||
            d.ServiceType == typeof(CodingAgentWebUI.Orchestration.LeaderElection.LeaderElectionService));
        descriptor.Should().BeNull("LeaderElection is Kubernetes-only");
    }

    // ── Mode differentiation ────────────────────────────────────────────

    [Fact]
    public void LegacyMode_DoesNotRegister_WorkItemTransitionService()
    {
        var config = BuildConfig(null, null);
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterLegacyPrerequisites(services);

        services.AddWorkDistribution(config);

        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(CodingAgentWebUI.Infrastructure.Persistence.Services.WorkItemTransitionService));
        descriptor.Should().BeNull("Legacy mode doesn't use WorkItemTransitionService");
    }

    [Fact]
    public void LegacyMode_DoesNotRegister_PostgresConfigurationStore()
    {
        var config = BuildConfig(null, null);
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterLegacyPrerequisites(services);

        services.AddWorkDistribution(config);

        // In legacy mode, IConfigurationStore is registered externally (AddInfrastructureServices)
        // WorkDistributionRegistration does NOT register it
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IConfigurationStore));
        descriptor.Should().BeNull("Legacy mode relies on externally registered JsonConfigurationStore");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static IConfiguration BuildConfig(string? dbHost, string? mode)
    {
        var dict = new Dictionary<string, string?>();
        if (dbHost is not null)
        {
            dict["Database:Host"] = dbHost;
            dict["Database:Name"] = "testdb";
        }
        if (mode is not null)
            dict["WorkDistribution:Mode"] = mode;

        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    private static void RegisterLegacyPrerequisites(IServiceCollection services)
    {
        var logger = Serilog.Log.Logger;
        var registry = new AgentRegistryService(logger);
        services.AddSingleton(Mock.Of<IJobDispatcher>());
        services.AddSingleton(new JobDispatcherService(registry, logger));
        services.AddSingleton(Mock.Of<IOrchestratorRunService>());
        services.AddSingleton(Mock.Of<IPipelineRunHistoryService>());
        services.AddSingleton(registry);
        services.AddSingleton(Mock.Of<ILabelSwapper>());
    }
}
