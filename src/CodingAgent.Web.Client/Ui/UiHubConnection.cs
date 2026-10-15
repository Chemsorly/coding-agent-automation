using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgent.Web.Ui;

/// <summary>
/// The page's connection to the UI hub (<c>/hubs/ui</c>, spec 051 D7): it joins the groups of the runs and
/// chats it shows (<c>HubMethodNames.SubscribeToRun</c> and the like) and receives their events.
/// </summary>
public interface IUiHubConnection : IAsyncDisposable
{
    HubConnectionState State { get; }

    /// <summary>
    /// Raised after the connection reconnected. A reconnect is a new hub connection without any groups, so
    /// handlers join their groups again here.
    /// </summary>
    event Func<string?, Task>? Reconnected;

    Task StartAsync(CancellationToken ct = default);

    Task InvokeAsync(string methodName, object? arg1, CancellationToken ct = default);

    IDisposable On<T1, T2>(string methodName, Action<T1, T2> handler);

    IDisposable On<T1, T2, T3>(string methodName, Action<T1, T2, T3> handler);
}

/// <summary>
/// Backoff of the UI hub's automatic reconnect: 2^min(retry, 7) seconds plus up to a second of jitter, at
/// most 120 s, retrying forever (as the agents' connection does).
/// </summary>
internal sealed class InfiniteRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var delay = TimeSpan.FromSeconds(Math.Pow(2, Math.Min(retryContext.PreviousRetryCount, 7)));
        if (delay > TimeSpan.FromSeconds(120))
            delay = TimeSpan.FromSeconds(120);
        return delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
    }
}

/// <summary>
/// <see cref="IUiHubConnection"/> over WebSockets without negotiation, since the API replicas are not sticky.
/// It authenticates with the user's hub token (<see cref="IUiAccessTokenSource.GetHubTokenAsync"/>), fetched
/// again on every connect and reconnect. The hub closes the connection when that token, and so the session,
/// expires; reconnecting then fails until the user signs in again.
/// </summary>
public sealed class UiHubConnection : IUiHubConnection
{
    private readonly HubConnection _connection;

    public UiHubConnection(Uri hubUrl, IUiAccessTokenSource tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = async () => await tokens.GetHubTokenAsync();
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
            })
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .Build();

        _connection.Reconnected += connectionId => Reconnected?.Invoke(connectionId) ?? Task.CompletedTask;
    }

    public HubConnectionState State => _connection.State;

    public event Func<string?, Task>? Reconnected;

    public Task StartAsync(CancellationToken ct = default) => _connection.StartAsync(ct);

    public Task InvokeAsync(string methodName, object? arg1, CancellationToken ct = default) =>
        _connection.InvokeAsync(methodName, arg1, ct);

    public IDisposable On<T1, T2>(string methodName, Action<T1, T2> handler) => _connection.On(methodName, handler);

    public IDisposable On<T1, T2, T3>(string methodName, Action<T1, T2, T3> handler) => _connection.On(methodName, handler);

    public async ValueTask DisposeAsync()
    {
        await _connection.StopAsync();
        await _connection.DisposeAsync();
    }
}
