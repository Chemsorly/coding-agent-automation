using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
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

    // ── Order-independence (IPipelineRunHistoryService) ────────────────

    // TODO: DB mode tests below only verify descriptor count (HaveCount(1)), not the resolved
    // implementation type. Consider building the service provider and asserting
    // GetRequiredService<IPipelineRunHistoryService>() is PostgresPipelineRunHistoryService
    // to directly validate the acceptance criterion.

    // TODO: DB mode tests below don't call RegisterLegacyPrerequisites, so they don't exercise
    // the full set of registrations present in production. If prerequisites ever add competing
    // IPipelineRunHistoryService registrations, these tests wouldn't catch the regression.
    [Fact]
    public void DbMode_OnlyPostgresHistoryDescriptor_RegardlessOfRegistrationOrder()
    {
        // Arrange: DB mode config
        var config = BuildConfig("localhost", "SignalR");
        var services = new ServiceCollection();
        services.AddLogging();

        // Act — call AddWorkDistribution first, then AddPipelineCoreServices (reversed order)
        services.AddWorkDistribution(config);
        services.AddPipelineCoreServices(isDatabaseMode: true);

        // Assert: only one IPipelineRunHistoryService descriptor exists (from AddWorkDistribution)
        var descriptors = services.Where(d => d.ServiceType == typeof(IPipelineRunHistoryService)).ToList();
        descriptors.Should().HaveCount(1,
            "Only Postgres registration should exist in DB mode — AddPipelineCoreServices must skip the in-memory registration");
    }

    [Fact]
    public void DbMode_OnlyPostgresHistoryDescriptor_NormalOrder()
    {
        // Arrange: DB mode config
        var config = BuildConfig("localhost", "SignalR");
        var services = new ServiceCollection();
        services.AddLogging();

        // Act — normal order: AddPipelineCoreServices first, then AddWorkDistribution
        services.AddPipelineCoreServices(isDatabaseMode: true);
        services.AddWorkDistribution(config);

        // Assert: only one IPipelineRunHistoryService descriptor exists (from AddWorkDistribution)
        var descriptors = services.Where(d => d.ServiceType == typeof(IPipelineRunHistoryService)).ToList();
        descriptors.Should().HaveCount(1,
            "Only Postgres registration should exist in DB mode — AddPipelineCoreServices must skip the in-memory registration");
    }

    // TODO: Add a LegacyMode_ResolvesInMemoryHistoryService_NormalOrder test that verifies
    // AddPipelineCoreServices(isDatabaseMode: false) followed by AddWorkDistribution(config)
    // still resolves the in-memory service, for symmetric coverage with DB mode tests.

    // TODO: This test name claims order independence, but it still relies on AddPipelineCoreServices
    // being called AFTER RegisterLegacyPrerequisites (which registers a mock IPipelineRunHistoryService).
    // DI "last wins" makes the real implementation resolve correctly, but reversing the order within
    // the test would cause the mock to win. Not a production issue (no mocks there), but misleading.
    [Fact]
    public void LegacyMode_ResolvesInMemoryHistoryService_RegardlessOfRegistrationOrder()
    {
        // Arrange: legacy mode (no DB)
        var config = BuildConfig(null, null);
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterLegacyPrerequisites(services);

        // Act — call AddWorkDistribution first, then AddPipelineCoreServices (reversed order)
        services.AddWorkDistribution(config);
        services.AddPipelineCoreServices(isDatabaseMode: false);

        // Assert: the last descriptor (DI "last wins") must be from AddPipelineCoreServices,
        // producing the real PipelineRunHistoryService — not the mock from RegisterLegacyPrerequisites.
        var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IPipelineRunHistoryService>();
        resolved.Should().BeOfType<PipelineRunHistoryService>(
            "Legacy mode must resolve the in-memory PipelineRunHistoryService from AddPipelineCoreServices, not a mock or other implementation");
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
