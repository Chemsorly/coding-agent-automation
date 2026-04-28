using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
        builder.ConfigureServices(services =>
        {
            // Replace IConfigurationStore with a mock returning defaults
            ReplaceService<IConfigurationStore>(services, CreateConfigurationStoreMock());

            // Replace IProviderFactory with a mock
            ReplaceService<IProviderFactory>(services, new Mock<IProviderFactory>().Object);

            // Replace IQualityGateValidator with a mock (prevents real dotnet build/test)
            ReplaceService<IQualityGateValidator>(services, new Mock<IQualityGateValidator>().Object);
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
