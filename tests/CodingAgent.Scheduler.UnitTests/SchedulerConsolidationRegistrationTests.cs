using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Verifies that <see cref="SchedulerServiceCollectionExtensions.AddSchedulerServices"/> registers
/// <see cref="IConsolidationService"/> and that it resolves to <see cref="ConsolidationService"/>.
/// </summary>
public sealed class SchedulerConsolidationRegistrationTests
{
    private static IConfiguration BuildMinimalConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Disable the dispatch loop to avoid registering WorkItemDispatchLoop
                // which is not relevant to this test.
                ["Scheduler:Dispatch:Enabled"] = "false",
                // No Redis connection string — avoids a real network connection attempt.
            })
            .Build();

    [Fact]
    public async Task Scheduler_ResolvesConsolidationService()
    {
        // Arrange
        var config = BuildMinimalConfig();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSchedulerServices("http://localhost:5000", "test-key", config);

        // Act
        await using var provider = services.BuildServiceProvider();

        var service = provider.GetService<IConsolidationService>();

        // Assert
        service.Should().NotBeNull("IConsolidationService must be registered by AddSchedulerServices");
        service.Should().BeOfType<ConsolidationService>(
            "the registered IConsolidationService must resolve to ConsolidationService");
    }
}
