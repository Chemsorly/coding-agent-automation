using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.IntegrationTests.Smoke;

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that overrides external
/// services with mocks so the app boots without real GitHub/Kiro CLI dependencies.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Force legacy mode by clearing any Database:Host config from the environment.
        // Without this, the app may detect a DB connection string from environment
        // variables or appsettings and enter database mode, causing pages to query PostgreSQL.
        builder.UseSetting("Database:Host", "");

        builder.ConfigureServices(services =>
        {
            // Reduce shutdown timeout to prevent test host hangs
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

            // Remove all hosted services (PipelineLoopService, HeartbeatMonitorService,
            // JobQueueDrainService) — they are not needed for integration tests and their
            // background loops can prevent the test host from shutting down cleanly.
            services.RemoveAll<IHostedService>();

            // Remove DatabaseReadinessMonitor singleton — it's registered as both
            // a singleton and a hosted service. RemoveAll<IHostedService> removes the
            // background loop, but the singleton remains and /healthz resolves it
            // to probe PostgreSQL. Remove it so /healthz skips the DB check.
            services.RemoveAll<DatabaseReadinessMonitor>();

            // Replace IConfigurationStore with a mock returning defaults
            var configStore = CreateConfigurationStoreMock();
            ReplaceService<IConfigurationStore>(services, configStore);
            ReplaceService<IPipelineConfigStore>(services, configStore);
            ReplaceService<IProviderConfigStore>(services, configStore);
            ReplaceService<IAgentProfileStore>(services, configStore);
            ReplaceService<IQualityGateConfigStore>(services, configStore);
            ReplaceService<IReviewerConfigStore>(services, configStore);

            // Replace IProviderFactory with a mock
            ReplaceService<IProviderFactory>(services, new Mock<IProviderFactory>().Object);

            // Replace IQualityGateValidator with a mock (prevents real dotnet build/test)
            ReplaceService<IQualityGateValidator>(services, new Mock<IQualityGateValidator>().Object);

            // Replace IConsolidationService — Program.cs calls CleanupOrphanedRunsAsync
            // and RehydrateQueuedRunsAsync during startup, which hit the database directly
            // (not via a hosted service), so RemoveAll<IHostedService> doesn't prevent it.
            var consolidationMock = new Mock<IConsolidationService>();
            consolidationMock.Setup(s => s.CleanupOrphanedRunsAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            consolidationMock.Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<ConsolidationRun>());
            ReplaceService<IConsolidationService>(services, consolidationMock.Object);
        });
    }

    private static IConfigurationStore CreateConfigurationStoreMock()
    {
        var mock = new Mock<IConfigurationStore>();
        mock.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mock.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        return mock.Object;
    }

    private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null)
            services.Remove(descriptor);

        services.AddSingleton(implementation);
    }
}
