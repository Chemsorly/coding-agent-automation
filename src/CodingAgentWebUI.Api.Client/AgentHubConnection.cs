using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Reimplements the backoff policy used by <c>HubConnectionManager.InfiniteRetryPolicy</c>
/// (private nested class in the Agent project, not referenceable from here).
/// Backoff: 2^min(retryCount, 7) seconds + random jitter 0–1000 ms, capped at 120 s.
/// </summary>
internal sealed class InfiniteRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var delay = TimeSpan.FromSeconds(Math.Pow(2, Math.Min(retryContext.PreviousRetryCount, 7)));
        if (delay > TimeSpan.FromSeconds(120))
            delay = TimeSpan.FromSeconds(120);
        delay += TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
        return delay;
    }
}

/// <summary>
/// UI-side SignalR hub connection for subscribing to agent/pipeline events.
/// Configured with automatic reconnect using <see cref="InfiniteRetryPolicy"/>,
/// matching <c>HubConnectionManager</c> on the agent side.
///
/// Protocol: JSON, not MessagePack. The API.Client project is a plain Microsoft.NET.Sdk
/// library and pulling MessagePack in from the ASP.NET Core shared framework would need a
/// FrameworkReference the library deliberately avoids. This is safe rather than a gap:
/// <c>AddMessagePackProtocol()</c> on the API hub *adds* MessagePack alongside the default
/// JSON protocol, so the server speaks both and each client negotiates its own. The custom
/// MessagePack formatters (JobIdFormatter, AgentIdFormatter) only matter for the agent-facing
/// methods, which take strongly-typed <c>JobId</c> / <c>AgentId</c> arguments; every method on
/// the UI surface (IAgentHubUiClient plus SubscribeToRun / UnsubscribeFromRun) uses primitives
/// and plain records that System.Text.Json handles directly.
///
/// If a strongly-typed id is ever added to the UI surface, this connection needs a matching
/// JSON converter — or the whole client needs to move to MessagePack.
/// </summary>
public sealed class AgentHubConnection : IAgentHubConnection
{
    private readonly HubConnection _connection;

    /// <param name="hubUrl">Full URL of the agent hub, e.g. https://host/hubs/agent</param>
    /// <param name="apiKey">Bearer token (master API key) used as the access token.</param>
    public AgentHubConnection(string hubUrl, string apiKey)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, opts =>
            {
                opts.AccessTokenProvider = () => Task.FromResult<string?>(apiKey);
            })
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .Build();

        // Forward the underlying reconnected event so consumers can re-subscribe to hub groups.
        _connection.Reconnected += connectionId =>
        {
            var handler = Reconnected;
            return handler is not null ? handler(connectionId) : Task.CompletedTask;
        };
    }

    /// <inheritdoc/>
    public HubConnectionState State => _connection.State;

    /// <inheritdoc/>
    public event Func<string?, Task>? Reconnected;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken ct = default) => _connection.StartAsync(ct);

    /// <inheritdoc/>
    public Task InvokeAsync(string methodName, object? arg1, CancellationToken ct = default)
        => _connection.InvokeAsync(methodName, arg1, ct);

    /// <inheritdoc/>
    public IDisposable On(string methodName, Action handler) => _connection.On(methodName, handler);

    /// <inheritdoc/>
    public IDisposable On<T>(string methodName, Action<T> handler) => _connection.On(methodName, handler);

    /// <inheritdoc/>
    public IDisposable On<T1, T2>(string methodName, Action<T1, T2> handler) => _connection.On(methodName, handler);

    /// <inheritdoc/>
    public IDisposable On<T1, T2, T3>(string methodName, Action<T1, T2, T3> handler) => _connection.On(methodName, handler);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _connection.StopAsync();
        await _connection.DisposeAsync();
    }
}
