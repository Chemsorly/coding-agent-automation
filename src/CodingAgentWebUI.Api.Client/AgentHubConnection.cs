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
/// Note: MessagePack protocol is intentionally omitted here. The API.Client project
/// is a plain Microsoft.NET.Sdk library; adding MessagePack from the ASP.NET Core
/// shared framework requires FrameworkReference which conflicts with the library's
/// portability requirements. Spec 045 (which wires the UI circuit to the API hub)
/// can add MessagePack via the Blazor Server host project, which already has the
/// ASP.NET Core framework available. Until then, the JSON protocol is used as the
/// fallback.
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
    }

    /// <inheritdoc/>
    public HubConnectionState State => _connection.State;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken ct = default) => _connection.StartAsync(ct);

    /// <inheritdoc/>
    public IDisposable On<T>(string methodName, Action<T> handler) => _connection.On(methodName, handler);

    /// <inheritdoc/>
    public IDisposable On(string methodName, Action handler) => _connection.On(methodName, handler);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _connection.StopAsync();
        await _connection.DisposeAsync();
    }
}
