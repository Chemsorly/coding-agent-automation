using System.Security.Cryptography;
using System.Text;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.IntegrationTests.SignalR;

/// <summary>
/// WebApplicationFactory that starts Kestrel on a random port with external providers mocked out.
/// Used for SignalR reconnection integration tests.
/// </summary>
public sealed class SignalRTestFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "signalr-test-key";

    public SignalRTestFactory()
    {
        // Start real Kestrel on a random port (required for SignalR client connections)
        UseKestrel(0);
    }

    public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:Host", "");

        // Reset Serilog to prevent "logger is already frozen" across multiple factory instances.
        // Use SilentLogger so Program.cs's CreateBootstrapLogger() creates a fresh ReloadableLogger.
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().CreateLogger();

        builder.ConfigureServices(services =>
        {
            // Override the API key via options rather than env var — Program.cs reads
            // the env var during service registration (before ConfigureWebHost runs),
            // so Environment.SetEnvironmentVariable is too late.
            services.PostConfigure<AgentApiKeyAuthOptions>(
                AgentApiKeyDefaults.AuthenticationScheme,
                opts => opts.ApiKey = TestApiKey);
            // Remove all hosted services
            services.RemoveAll<IHostedService>();

            // Replace external providers with mocks
            var configStore = new Mock<IConfigurationStore>();
            configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineConfiguration());
            configStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<ProviderConfig>());
            configStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProviderConfig?)null);

            ReplaceService<IConfigurationStore>(services, configStore.Object);
            ReplaceService<IPipelineConfigStore>(services, configStore.Object);
            ReplaceService<IProviderConfigStore>(services, configStore.Object);
            ReplaceService<IAgentProfileStore>(services, configStore.Object);
            ReplaceService<IQualityGateConfigStore>(services, configStore.Object);
            ReplaceService<IReviewerConfigStore>(services, configStore.Object);
            ReplaceService<IProjectStore>(services, configStore.Object);
            ReplaceService<IProviderFactory>(services, new Mock<IProviderFactory>().Object);
            ReplaceService<IQualityGateValidator>(services, new Mock<IQualityGateValidator>().Object);

            // Mock IConsolidationService — Program.cs calls CleanupOrphanedRunsAsync
            // and RehydrateQueuedRunsAsync at startup which hit PostgreSQL directly.
            var consolidationMock = new Mock<IConsolidationService>();
            consolidationMock.Setup(s => s.CleanupOrphanedRunsAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            consolidationMock.Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<ConsolidationRun>());
            ReplaceService<IConsolidationService>(services, consolidationMock.Object);
        });
    }

    private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var descriptor in descriptors)
            services.Remove(descriptor);
        services.AddSingleton(implementation);
    }
}

/// <summary>
/// Shared fixture for SignalR reconnection integration tests.
/// </summary>
public sealed class SignalRTestFixture : IAsyncLifetime
{
    public SignalRTestFactory Factory { get; } = new();
    public AgentRegistryService Registry { get; private set; } = null!;
    public string ServerAddress => Factory.ServerAddress;

    public async Task InitializeAsync()
    {
        // Trigger host start
        using var _ = Factory.CreateClient();
        Registry = Factory.Services.GetRequiredService<AgentRegistryService>();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
    }

    /// <summary>
    /// Creates a new SignalR hub connection for the given agentId.
    /// </summary>
    public HubConnection CreateHubConnection(string agentId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SignalRTestFactory.TestApiKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(agentId));
        var derivedToken = Convert.ToHexString(hash).ToLowerInvariant();

        return new HubConnectionBuilder()
            .WithUrl($"{ServerAddress}{HubRoutes.Agent}?agentId={agentId}&access_token={derivedToken}")
            .Build();
    }

    /// <summary>
    /// Waits for the registry to reflect the expected status for an agent.
    /// </summary>
    public async Task WaitForStatusAsync(string agentId, AgentStatus expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var entry = Registry.GetByAgentId(agentId);
            if (entry?.Status == expected)
                return;
            await Task.Delay(50);
        }

        var actual = Registry.GetByAgentId(agentId)?.Status;
        throw new TimeoutException(
            $"Agent '{agentId}' did not reach status '{expected}' within timeout. Current: '{actual}'");
    }
}
