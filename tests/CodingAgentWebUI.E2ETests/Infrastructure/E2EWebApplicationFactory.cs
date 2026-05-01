using CodingAgentWebUI.E2ETests.Fakes;
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
            ReplaceService<IProviderFactory>(services, FakeProviders);
            ReplaceService<IQualityGateValidator>(services, QualityGateValidator);
            ReplaceService<IPipelineRunHistoryService>(services, HistoryService);

            // Disable PipelineLoopService (background polling)
            RemoveHostedService<PipelineLoopService>(services);
        });
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

        // Reset singleton services in the running host
        var orchestration = Services.GetRequiredService<PipelineOrchestrationService>();
        orchestration.ResetForTesting();

        var registry = Services.GetRequiredService<AgentRegistryService>();
        registry.ResetForTesting();

        var runService = Services.GetRequiredService<OrchestratorRunService>();
        runService.ResetForTesting();

        var dispatcher = Services.GetRequiredService<JobDispatcherService>();
        dispatcher.ResetForTesting();
    }

    private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
    {
        // Remove all registrations for this type
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var descriptor in descriptors)
            services.Remove(descriptor);

        services.AddSingleton(implementation);
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
