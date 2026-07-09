using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Validates that AddOrchestrationServices correctly wires IDbContextFactory into AgentHubFacade.
/// Regression test for: LastProgressAt never written to DB because dbFactory was null.
/// Root cause: DI registration omitted the IDbContextFactory parameter (optional, defaulted to null).
/// </summary>
public sealed class AgentHubFacadeDbFactoryWiringTests
{
    /// <summary>
    /// The AgentHubFacade must receive a non-null IDbContextFactory so that
    /// TouchLastProgressAsync can persist heartbeat progress to the database.
    /// Without this, ReconciliationService falls back to DispatchedAt for timeout
    /// calculations, causing premature 7200s timeouts on legitimate long-running jobs.
    /// </summary>
    [Fact]
    public void AddOrchestrationServices_RegistersAgentHubFacade_WithDbContextFactoryParameter()
    {
        // Arrange: Register orchestration services (where AgentHubFacade is registered)
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Serilog.ILogger>(new Serilog.LoggerConfiguration().CreateLogger());
        services.AddPooledDbContextFactory<PipelineDbContext>(opts =>
            opts.UseInMemoryDatabase($"WiringTest-{Guid.NewGuid():N}"));

        var pipelineConfig = new PipelineConfiguration();
        services.AddSingleton(pipelineConfig);
        services.AddOrchestrationServices(pipelineConfig, workDistributionMode: "SignalR");

        // Provide mocks for remaining dependencies that AddOrchestrationServices doesn't register itself
        services.AddSingleton(Moq.Mock.Of<CodingAgentWebUI.Pipeline.Interfaces.IConfigurationStore>());
        services.AddSingleton(Moq.Mock.Of<CodingAgentWebUI.Pipeline.Interfaces.IProviderFactory>());
        services.AddSingleton(Moq.Mock.Of<CodingAgentWebUI.Pipeline.Interfaces.IPipelineRunHistoryService>());
        services.AddSingleton(Moq.Mock.Of<CodingAgentWebUI.Pipeline.Interfaces.IConsolidationDispatcher>());
        services.AddSingleton(Moq.Mock.Of<CodingAgentWebUI.Pipeline.Interfaces.IJobDispatcher>());
        services.AddSingleton<CodingAgentWebUI.Pipeline.Interfaces.IShutdownSignal>(
            new CodingAgentWebUI.Pipeline.Services.ShutdownSignal());
        services.AddHttpClient();

        // Act: Build the provider and resolve AgentHubFacade
        using var provider = services.BuildServiceProvider();
        var facade = provider.GetRequiredService<IAgentHubFacade>();

        // Assert: The facade's _dbFactory field must not be null
        var dbFactoryField = typeof(AgentHubFacade).GetField("_dbFactory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        dbFactoryField.Should().NotBeNull();

        var dbFactoryValue = dbFactoryField!.GetValue(facade);
        dbFactoryValue.Should().NotBeNull(
            "AgentHubFacade._dbFactory must not be null — " +
            "TouchLastProgressAsync requires it to persist heartbeat progress to the DB. " +
            "Without it, ReconciliationService timeout uses DispatchedAt instead of LastProgressAt.");
    }
}
