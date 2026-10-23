using AwesomeAssertions;
using CodingAgent.Infrastructure;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Characterization tests that pin the five shared orchestration services registered by
/// <see cref="SchedulerServiceCollectionExtensions.AddSchedulerServices"/>.
///
/// These are a prerequisite for Epic #3847, which will extract all five registrations into a
/// shared DI extension.  Each test asserts the concrete implementation type and singleton
/// identity so that any future refactor that accidentally changes either will be caught.
/// </summary>
public sealed class SchedulerSharedOrchestrationRegistrationTests
{
    private static IConfiguration BuildMinimalConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Disable the dispatch loop to avoid registering WorkItemDispatchLoop,
                // which is not relevant to this test.
                ["Scheduler:Dispatch:Enabled"] = "false",
                // No Redis connection string — avoids a real network connection attempt.
            })
            .Build();

    private static ServiceProvider BuildProvider()
    {
        var config = BuildMinimalConfig();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSchedulerServices("http://localhost:5000", "test-key", config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AddSchedulerServices_IProviderFactory_ResolvesAsProviderFactorySingleton()
    {
        await using var provider = BuildProvider();

        var first = provider.GetRequiredService<IProviderFactory>();
        var second = provider.GetRequiredService<IProviderFactory>();

        first.Should().BeOfType<ProviderFactory>(
            "IProviderFactory must be backed by the real ProviderFactory implementation");
        first.Should().BeSameAs(second,
            "IProviderFactory must be registered as a singleton");
    }

    [Fact]
    public async Task AddSchedulerServices_IConsolidationService_ResolvesAsConsolidationServiceSingleton()
    {
        await using var provider = BuildProvider();

        var first = provider.GetRequiredService<IConsolidationService>();
        var second = provider.GetRequiredService<IConsolidationService>();

        first.Should().BeOfType<ConsolidationService>(
            "IConsolidationService must be backed by ConsolidationService");
        first.Should().BeSameAs(second,
            "IConsolidationService must be registered as a singleton");
    }

    [Fact]
    public async Task AddSchedulerServices_IDependencyChecker_ResolvesAsDependencyCheckerSingleton()
    {
        await using var provider = BuildProvider();

        var first = provider.GetRequiredService<IDependencyChecker>();
        var second = provider.GetRequiredService<IDependencyChecker>();

        first.Should().BeOfType<DependencyChecker>(
            "IDependencyChecker must be backed by DependencyChecker");
        first.Should().BeSameAs(second,
            "IDependencyChecker must be registered as a singleton");
    }

    [Fact]
    public async Task AddSchedulerServices_IDispatchOrchestrationService_ResolvesAsDispatchOrchestrationServiceSingleton()
    {
        await using var provider = BuildProvider();

        var first = provider.GetRequiredService<IDispatchOrchestrationService>();
        var second = provider.GetRequiredService<IDispatchOrchestrationService>();

        first.Should().BeOfType<DispatchOrchestrationService>(
            "IDispatchOrchestrationService must be backed by DispatchOrchestrationService");
        first.Should().BeSameAs(second,
            "IDispatchOrchestrationService must be registered as a singleton");
    }

    [Fact]
    public async Task AddSchedulerServices_ConsolidationStalenessService_ResolvesAsItselfSingleton()
    {
        await using var provider = BuildProvider();

        var first = provider.GetRequiredService<ConsolidationStalenessService>();
        var second = provider.GetRequiredService<ConsolidationStalenessService>();

        // TODO [WARNING]: BeOfType<ConsolidationStalenessService>() is near-tautological here
        // because GetRequiredService<ConsolidationStalenessService>() already constrains the
        // returned value to ConsolidationStalenessService or a subclass. The assertion only fails
        // if a derived class is registered as the concrete type — a currently non-existent scenario.
        // The meaningful characterisation assertion is BeSameAs (singleton identity) below.
        // Kept per issue #3857 requirements; revisit if the class becomes sealed.
        first.Should().BeOfType<ConsolidationStalenessService>(
            "ConsolidationStalenessService must resolve to its own concrete type");
        first.Should().BeSameAs(second,
            "ConsolidationStalenessService must be registered as a singleton");
    }
}
