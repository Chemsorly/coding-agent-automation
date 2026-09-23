using CodingAgent.AgentGateway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Serilog;
using StackExchange.Redis;

namespace CodingAgent.Api;

/// <summary>
/// Extension methods for registering SignalR for the Pipeline API,
/// with optional Redis backplane (Req 5.8).
/// </summary>
internal static class ApiSignalRRegistration
{
    /// <summary>
    /// Registers SignalR with MessagePack protocol, agent authorization filter,
    /// and an optional Redis backplane when SignalR:Redis:ConnectionString is set.
    /// The shared core (MessagePack formatter list, MaximumReceiveMessageSize,
    /// AgentAuthorizationFilter) is provided by
    /// <see cref="AgentSignalRServiceCollectionExtensions.AddAgentSignalRServices"/>.
    /// Channel prefix "caa" matches the monolith (Req 5.8).
    /// </summary>
    public static IServiceCollection AddApiSignalR(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var signalR = services.AddAgentSignalRServices(environment);

        // ── Optional Redis backplane (Req 5.8) ──────────────────────────────
        var redisConnectionString = configuration.GetValue<string>("SignalR:Redis:ConnectionString");
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            var config = ConfigurationOptions.Parse(redisConnectionString);
            config.ChannelPrefix = RedisChannel.Literal("caa");
            config.AbortOnConnectFail = false;
            config.ConnectRetry = 5;
            config.ReconnectRetryPolicy = new ExponentialRetry(5000, 55000);

            // Create the multiplexer once and share it between SignalR's backplane and
            // the /readyz probe. A single shared instance means the probe checks the exact
            // connection that serves hub messages — no second connection to maintain.
            var multiplexer = ConnectionMultiplexer.Connect(config);
            multiplexer.ConnectionFailed += (_, e) =>
                Log.Warning("Redis backplane connection failed: {FailureType} — {Exception}",
                    e.FailureType, e.Exception?.Message);
            multiplexer.ConnectionRestored += (_, e) =>
                Log.Information("Redis backplane connection restored: {EndPoint}", e.EndPoint);

            // Register as IConnectionMultiplexer so /readyz can resolve it conditionally.
            services.AddSingleton<IConnectionMultiplexer>(multiplexer);

            signalR.AddStackExchangeRedis(options =>
            {
                options.Configuration = config;
                // Reuse the shared multiplexer instead of creating a second connection.
                options.ConnectionFactory = _ => Task.FromResult<IConnectionMultiplexer>(multiplexer);
            });

            Log.Information("Pipeline API: SignalR Redis backplane configured");
        }

        return services;
    }
}
