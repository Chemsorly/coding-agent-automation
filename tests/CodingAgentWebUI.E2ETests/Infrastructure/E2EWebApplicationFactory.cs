using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// WebApplicationFactory that starts the Blazor Server app on a real Kestrel port
/// with all external providers replaced by in-memory fakes.
/// Uses the .NET 10 first-class UseKestrel() API — no CreateHost override needed.
/// </summary>
public sealed class E2EWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "e2e-test-key";

    // Shared fake instances — accessible by tests for seeding and assertions
    public InMemoryConfigurationStore ConfigStore { get; } = new();
    public FakeProviderFactory FakeProviders { get; } = new();
    public ConfigurableQualityGateValidator QualityGateValidator { get; } = new();
    public InMemoryPipelineRunHistoryService HistoryService { get; } = new();

    // Resettable services — created during ConfigureServices, used in ResetAll
    private ResettablePipelineOrchestrationService? _orchestration;
    private AgentRegistryService? _registry;
    private OrchestratorRunService? _runService;
    private JobDispatcherService? _dispatcher;

    /// <summary>Exposes the agent registry for test assertions and wait helpers.</summary>
    public AgentRegistryService AgentRegistry => _registry ?? throw new InvalidOperationException("Not initialized");

    public E2EWebApplicationFactory()
    {
        // Use the .NET 10 first-class API to start real Kestrel on a random port
        UseKestrel(0);
    }

    /// <summary>The base address of the running Kestrel server (e.g., http://localhost:12345).</summary>
    public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set the API key via environment variable before host builds
        Environment.SetEnvironmentVariable("AGENT_API_KEY", TestApiKey);

        // Set environment to Development so static web assets are resolved correctly.
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Seed default test data
            ConfigStore.SeedDefaults();

            // Replace external provider interfaces with fakes
            ReplaceService<IConfigurationStore>(services, ConfigStore);
            ReplaceService<IPipelineConfigStore>(services, ConfigStore);
            ReplaceService<IProviderConfigStore>(services, ConfigStore);
            ReplaceService<IAgentProfileStore>(services, ConfigStore);
            ReplaceService<IQualityGateConfigStore>(services, ConfigStore);
            ReplaceService<IReviewerConfigStore>(services, ConfigStore);
            ReplaceService<IProjectStore>(services, ConfigStore);
            ReplaceService<IProviderFactory>(services, FakeProviders);
            ReplaceService<IQualityGateValidator>(services, QualityGateValidator);
            ReplaceService<IPipelineRunHistoryService>(services, HistoryService);

            // Replace singleton services with resettable subclasses
            ReplaceWithResettableServices(services);

            // Disable PipelineLoopService (background polling)
            RemoveHostedService<PipelineLoopService>(services);
        });
    }

    private void ReplaceWithResettableServices(IServiceCollection services)
    {
        // AgentRegistryService — sealed, uses internal Reset()
        _registry = new AgentRegistryService(Serilog.Log.Logger);
        RemoveService<AgentRegistryService>(services);
        services.AddSingleton(_registry);

        // OrchestratorRunService — sealed, uses internal Reset()
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
        RemoveService<OrchestratorRunService>(services);
        RemoveService<IOrchestratorRunService>(services);
        services.AddSingleton(_runService);
        services.AddSingleton<IOrchestratorRunService>(_runService);

        // JobDispatcherService — sealed, uses internal Reset()
        _dispatcher = new JobDispatcherService(_registry, Serilog.Log.Logger);
        RemoveService<JobDispatcherService>(services);
        services.AddSingleton(_dispatcher);

        // PipelineOrchestrationService → ResettablePipelineOrchestrationService
        _orchestration = new ResettablePipelineOrchestrationService(
            ConfigStore,
            FakeProviders,
            new IssueDescriptionParser(),
            new AgentPhaseExecutor(Serilog.Log.Logger),
            new QualityGateExecutor(QualityGateValidator, new PullRequestOrchestrator(Serilog.Log.Logger), new CiLogWriter(Serilog.Log.Logger), new FeedbackService(Serilog.Log.Logger), Serilog.Log.Logger),
            Serilog.Log.Logger,
            new BrainUpdateService(Serilog.Log.Logger),
            HistoryService,
            _runService);
        RemoveService<PipelineOrchestrationService>(services);
        services.AddSingleton(_orchestration);
        services.AddSingleton<PipelineOrchestrationService>(_orchestration);
    }

    /// <summary>
    /// Resets all fakes and singleton services for test isolation.
    /// </summary>
    public void ResetAll()
    {
        ConfigStore.Reset();
        ConfigStore.SeedDefaults();
        FakeProviders.Reset();
        QualityGateValidator.Reset();
        HistoryService.Reset();

        // Reset resettable service subclasses
        _orchestration?.Reset();
        _registry?.Reset();
        _runService?.Reset();
        _dispatcher?.Reset();

        // Reset consolidation badge service
        var badgeService = Services.GetRequiredService<ConsolidationBadgeService>();
        badgeService.Reset();
    }

    private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
    {
        RemoveService<T>(services);
        services.AddSingleton(implementation);
    }

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var descriptor in descriptors)
            services.Remove(descriptor);
    }

    private static void RemoveHostedService<T>(IServiceCollection services) where T : class
    {
        var descriptors = services.Where(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(T)).ToList();
        foreach (var descriptor in descriptors)
            services.Remove(descriptor);
    }
}
