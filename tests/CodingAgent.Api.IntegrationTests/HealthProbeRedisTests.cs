using System.Net;
using AwesomeAssertions;
using CodingAgent.Infrastructure;
using CodingAgent.Pipeline.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using StackExchange.Redis;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Issue #4218: liveness (<c>/healthz</c>) must not depend on Redis, because a restart cannot fix a Redis outage;
/// readiness (<c>/readyz</c>) keeps reporting the disconnected backplane so the pod leaves the Service instead.
/// Builds an in-memory server around <see cref="ApiStartupExtensions.MapApiHealthEndpoints"/> with a multiplexer
/// that is disconnected and throws on every database call.
/// </summary>
public sealed class HealthProbeRedisTests : IAsyncDisposable
{
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private IHost? _host;
    private HttpClient? _client;

    private async Task<HttpClient> CreateClientAsync()
    {
        _redis.Setup(m => m.IsConnected).Returns(false);
        _redis
            .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Throws(new RedisConnectionException(ConnectionFailureType.SocketClosed, "test"));

        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSingleton(_redis.Object);
                        services.AddSingleton<ReadinessState>();
                        services.AddSingleton<DatabaseHealthState>();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapApiHealthEndpoints());
                    });
            })
            .StartAsync();

        _client = _host.GetTestClient();
        return _client;
    }

    [Fact]
    public async Task Healthz_RedisUnavailable_ReturnsOk()
    {
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ok");
        _redis.Verify(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task Readyz_RedisUnavailable_ReturnsServiceUnavailableWithBackplaneReason()
    {
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/readyz");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("redis_backplane_disconnected");
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null)
            await _host.StopAsync();
        _host?.Dispose();
    }
}
