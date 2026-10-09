using StackExchange.Redis;

namespace CodingAgent.Orchestration.Redis;

/// <summary>
/// Centralised factory for StackExchange.Redis <see cref="ConfigurationOptions"/> that
/// enforces the project-wide resilience defaults for every multiplexer created in <c>src/</c>.
///
/// <para>
/// Without these defaults, <c>ConnectionMultiplexer.Connect(string)</c> uses
/// <c>AbortOnConnectFail = true</c> for endpoints it does not recognise as Azure or Redis Cloud
/// (StackExchange.Redis 3.3.1). That causes a <c>RedisConnectionException</c> when no endpoint
/// answers within <c>connectTimeout</c>, crashing the pod during service registration — before
/// <c>Build()</c> is called — if Redis is temporarily unreachable at startup.
/// </para>
///
/// <para>
/// Callers that need a <c>ChannelPrefix</c> (SignalR backplane) set it themselves after calling
/// <c>Parse</c>; this class does not set it.
/// </para>
/// </summary>
public static class RedisConnectionOptions
{
    /// <summary>
    /// Parses <paramref name="connectionString"/> into a <see cref="ConfigurationOptions"/>
    /// and applies the project-wide resilience defaults:
    /// <list type="bullet">
    ///   <item><c>AbortOnConnectFail = false</c> — return a disconnected multiplexer on startup failure instead of throwing</item>
    ///   <item><c>ConnectRetry = 5</c> — retry the initial connection up to 5 times</item>
    ///   <item><c>ReconnectRetryPolicy = new ExponentialRetry(5000, 55000)</c> — exponential back-off for reconnects</item>
    /// </list>
    /// Any <c>abortConnect=true</c> in the connection string is overridden to <c>false</c>.
    /// </summary>
    /// <param name="connectionString">StackExchange.Redis connection string (e.g. <c>"redis:6379"</c>).</param>
    /// <returns>A <see cref="ConfigurationOptions"/> with resilience defaults applied.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="connectionString"/> is null or whitespace.</exception>
    public static ConfigurationOptions Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var opts = ConfigurationOptions.Parse(connectionString);
        opts.AbortOnConnectFail = false;
        opts.ConnectRetry = 5;
        opts.ReconnectRetryPolicy = new ExponentialRetry(5000, 55000);
        return opts;
    }
}
