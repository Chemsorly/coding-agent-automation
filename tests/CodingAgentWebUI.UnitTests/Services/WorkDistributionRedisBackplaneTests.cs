using CodingAgentWebUI;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests the SignalR Redis backplane configuration in ConfigureSignalRRedisBackplane.
/// Validates: Issue #946 (RES-09) — AbortOnConnectFail=false and connection monitoring.
/// </summary>
public class WorkDistributionRedisBackplaneTests
{
    /// <summary>
    /// Helper: calls AddWorkDistribution with Redis connection string set and returns the service collection.
    /// Uses SignalR mode (DB path) to trigger Redis backplane configuration.
    /// </summary>
    private static IServiceCollection BuildServicesWithRedis(string? redisConnectionString)
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Database:Host"] = "localhost",
            ["Database:Name"] = "test",
            ["WorkDistribution:Mode"] = "SignalR",
        };

        if (redisConnectionString is not null)
            configValues["SignalR:Redis:ConnectionString"] = redisConnectionString;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkDistribution(config);
        return services;
    }

    /// <summary>
    /// Resolves the configured RedisOptions by applying all IConfigureOptions registrations.
    /// </summary>
    // TODO: BuildServiceProvider() returns IDisposable but is not disposed here. Wrap in using or dispose after use.
    private static RedisOptions ResolveRedisOptions(IServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        var configureOptions = provider.GetServices<IConfigureOptions<RedisOptions>>();
        var redisOptions = new RedisOptions();
        foreach (var configure in configureOptions)
        {
            configure.Configure(redisOptions);
        }
        return redisOptions;
    }

    [Fact]
    public void NoRedisConnectionString_DoesNotRegisterRedisOptions()
    {
        // Arrange & Act
        var services = BuildServicesWithRedis(null);

        // Assert: no IConfigureOptions<RedisOptions> registered
        var hasRedisConfig = services.Any(d =>
            d.ServiceType == typeof(IConfigureOptions<RedisOptions>));
        Assert.False(hasRedisConfig, "Redis options should not be registered when no connection string is provided");
    }

    [Fact]
    public void WithRedisConnectionString_AbortOnConnectFail_IsFalse()
    {
        // Arrange & Act
        var services = BuildServicesWithRedis("localhost:6379");
        var options = ResolveRedisOptions(services);

        // Assert
        Assert.False(options.Configuration.AbortOnConnectFail,
            "AbortOnConnectFail should be false to prevent startup crashes when Redis is unavailable");
    }

    [Fact]
    public void WithRedisConnectionString_ChannelPrefix_IsCaa()
    {
        // Arrange & Act
        var services = BuildServicesWithRedis("localhost:6379");
        var options = ResolveRedisOptions(services);

        // Assert
        Assert.Equal(RedisChannel.Literal("caa"), options.Configuration.ChannelPrefix);
    }

    [Fact]
    public void WithRedisConnectionString_ConnectRetry_Is5()
    {
        // Arrange & Act
        var services = BuildServicesWithRedis("localhost:6379");
        var options = ResolveRedisOptions(services);

        // Assert
        Assert.Equal(5, options.Configuration.ConnectRetry);
    }

    [Fact]
    public void WithRedisConnectionString_ReconnectRetryPolicy_IsExponentialRetry()
    {
        // Arrange & Act
        var services = BuildServicesWithRedis("localhost:6379");
        var options = ResolveRedisOptions(services);

        // Assert
        Assert.IsType<ExponentialRetry>(options.Configuration.ReconnectRetryPolicy);
    }

    [Fact]
    public void WithRedisConnectionString_ConnectionFactory_IsSet()
    {
        // Arrange & Act
        var services = BuildServicesWithRedis("localhost:6379");
        var options = ResolveRedisOptions(services);

        // Assert
        // TODO: This only asserts NotNull. Consider verifying the factory wires ConnectionFailed/ConnectionRestored event handlers.
        Assert.NotNull(options.ConnectionFactory);
    }

    [Fact]
    public void WithEmptyRedisConnectionString_DoesNotRegisterRedisOptions()
    {
        // Arrange — empty string should behave same as null (early return)
        var configValues = new Dictionary<string, string?>
        {
            ["Database:Host"] = "localhost",
            ["Database:Name"] = "test",
            ["WorkDistribution:Mode"] = "SignalR",
            ["SignalR:Redis:ConnectionString"] = "",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkDistribution(config);

        // Assert: no IConfigureOptions<RedisOptions> registered
        var hasRedisConfig = services.Any(d =>
            d.ServiceType == typeof(IConfigureOptions<RedisOptions>));
        Assert.False(hasRedisConfig, "Redis options should not be registered when connection string is empty");
    }
}
