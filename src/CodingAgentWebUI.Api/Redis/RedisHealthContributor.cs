using StackExchange.Redis;

namespace CodingAgentWebUI.Api.Redis;

/// <summary>
/// Checks Redis connectivity as part of /healthz.
/// If the <see cref="IConnectionMultiplexer"/> cannot PING, liveness returns 503 so Kubernetes restarts the pod.
/// </summary>
public sealed class RedisHealthContributor
{
    private readonly IConnectionMultiplexer _mux;

    public RedisHealthContributor(IConnectionMultiplexer mux)
    {
        ArgumentNullException.ThrowIfNull(mux);
        _mux = mux;
    }

    /// <summary>Returns true when Redis responds to PING within the default timeout.</summary>
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            var db = _mux.GetDatabase();
            var latency = await db.PingAsync();
            return latency != TimeSpan.Zero;
        }
        catch
        {
            return false;
        }
    }
}
