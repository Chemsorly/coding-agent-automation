using CodingAgentWebUI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StackExchange.Redis;

namespace CodingAgentWebUI.UnitTests.Services;

public class InfrastructureHealthServiceTests
{
    private static InfrastructureHealthService CreateService(
        DatabaseHealthState? dbHealth = null,
        IConnectionMultiplexer? redis = null,
        bool dbModeActive = false,
        bool redisConfigured = false)
    {
        var services = new ServiceCollection();
        if (dbHealth is not null)
            services.AddSingleton(dbHealth);
        if (redis is not null)
            services.AddSingleton(redis);

        var sp = services.BuildServiceProvider();

        var configData = new Dictionary<string, string?>();
        if (dbModeActive)
            configData["Database:Host"] = "localhost";
        if (redisConfigured)
            configData["SignalR:Redis:ConnectionString"] = "localhost:6379";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        return new InfrastructureHealthService(sp, configuration);
    }

    [Fact]
    public void DatabaseConnected_ReturnsNull_WhenDbModeNotActive()
    {
        var service = CreateService(dbModeActive: false);

        Assert.Null(service.DatabaseConnected);
    }

    // TODO: Add test for when dbModeActive=true but DatabaseHealthState is not registered in DI.
    // Should document whether the service returns null or false in that scenario.

    [Fact]
    public void DatabaseConnected_ReturnsTrue_WhenDbHealthy()
    {
        var dbHealth = new DatabaseHealthState();
        var service = CreateService(dbHealth: dbHealth, dbModeActive: true);

        Assert.True(service.DatabaseConnected);
    }

    [Fact]
    public void DatabaseConnected_ReturnsFalse_WhenDbUnhealthy()
    {
        var dbHealth = new DatabaseHealthState();
        dbHealth.MarkUnhealthy();
        var service = CreateService(dbHealth: dbHealth, dbModeActive: true);

        Assert.False(service.DatabaseConnected);
    }

    [Fact]
    public void RedisConnected_ReturnsNull_WhenRedisNotConfigured()
    {
        var service = CreateService(redisConfigured: false);

        Assert.Null(service.RedisConnected);
    }

    // TODO: Add test for when redisConfigured=true but IConnectionMultiplexer is not registered in DI.
    // Should document whether the service returns null or false in that scenario.

    [Fact]
    public void RedisConnected_ReturnsTrue_WhenMultiplexerConnected()
    {
        var mockRedis = new Mock<IConnectionMultiplexer>();
        mockRedis.Setup(r => r.IsConnected).Returns(true);

        var service = CreateService(redis: mockRedis.Object, redisConfigured: true);

        Assert.True(service.RedisConnected);
    }

    [Fact]
    public void RedisConnected_ReturnsFalse_WhenMultiplexerDisconnected()
    {
        var mockRedis = new Mock<IConnectionMultiplexer>();
        mockRedis.Setup(r => r.IsConnected).Returns(false);

        var service = CreateService(redis: mockRedis.Object, redisConfigured: true);

        Assert.False(service.RedisConnected);
    }

    [Fact]
    public void DatabaseConnected_ReflectsStateChanges()
    {
        var dbHealth = new DatabaseHealthState();
        var service = CreateService(dbHealth: dbHealth, dbModeActive: true);

        Assert.True(service.DatabaseConnected);

        dbHealth.MarkUnhealthy();
        Assert.False(service.DatabaseConnected);

        dbHealth.MarkHealthy();
        Assert.True(service.DatabaseConnected);
    }

    [Fact]
    public void BothNull_WhenLegacyModeAndNoRedis()
    {
        var service = CreateService(dbModeActive: false, redisConfigured: false);

        Assert.Null(service.DatabaseConnected);
        Assert.Null(service.RedisConnected);
    }
}
