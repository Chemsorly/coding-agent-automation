using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using StackExchange.Redis;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Extension methods for registering SignalR for the Pipeline API,
/// with optional Redis backplane (Req 5.8).
/// </summary>
internal static class ApiSignalRRegistration
{
    /// <summary>
    /// Registers SignalR with MessagePack protocol, agent authorization filter,
    /// and an optional Redis backplane when SignalR:Redis:ConnectionString is set.
    /// Channel prefix "caa" matches the monolith (Req 5.8).
    /// </summary>
    public static IServiceCollection AddApiSignalR(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var signalR = services.AddSignalR(options =>
            {
                options.MaximumReceiveMessageSize = 128 * 1024; // 128 KB
            })
            .AddMessagePackProtocol(options =>
            {
                options.SerializerOptions = MessagePackSerializerOptions.Standard
                    .WithResolver(CompositeResolver.Create(
                        new IMessagePackFormatter[] { new JobIdFormatter(), new AgentIdFormatter() },
                        new IFormatterResolver[] { ContractlessStandardResolverAllowPrivate.Instance }));
            });

        // Hub filter for agent authorization
        services.AddSingleton<IHubFilter>(sp => new AgentAuthorizationFilter(
            sp.GetRequiredService<IAgentRegistryService>(),
            Log.Logger));

        // ── Optional Redis backplane (Req 5.8) ──────────────────────────────
        var redisConnectionString = configuration.GetValue<string>("SignalR:Redis:ConnectionString");
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            var config = ConfigurationOptions.Parse(redisConnectionString);
            config.ChannelPrefix = RedisChannel.Literal("caa");
            config.AbortOnConnectFail = false;
            config.ConnectRetry = 5;
            config.ReconnectRetryPolicy = new ExponentialRetry(5000, 55000);

            signalR.AddStackExchangeRedis(options =>
            {
                options.Configuration = config;
                options.ConnectionFactory = async writer =>
                {
                    var connection = await ConnectionMultiplexer.ConnectAsync(config, writer);
                    connection.ConnectionFailed += (_, e) =>
                        Log.Warning("Redis backplane connection failed: {FailureType} — {Exception}",
                            e.FailureType, e.Exception?.Message);
                    connection.ConnectionRestored += (_, e) =>
                        Log.Information("Redis backplane connection restored: {EndPoint}", e.EndPoint);
                    return connection;
                };
            });

            Log.Information("Pipeline API: SignalR Redis backplane configured");
        }

        return services;
    }
}
