using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>Forwards TCP to a target and can break every open connection, as a network drop does.</summary>
/// <remarks>
/// <para>
/// A test that points a client at <see cref="Url"/> instead of the target gets a broken transport on demand:
/// <see cref="DropAllConnections"/> resets both sockets of every open pair, so the client sees a connection reset and
/// the target sees its peer vanish. The listener stays open, so the client's next attempt connects as it would after a
/// real outage.
/// </para>
/// <para>
/// Forwarding is byte for byte, so it does not matter what rides on top: HTTP, a WebSocket, a SignalR hub. The proxy
/// never reads the target's path or query, only its host and port.
/// </para>
/// </remarks>
public sealed class TcpDropProxy : IAsyncDisposable
{
    private readonly Uri _target;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, ConnectionPair> _pairs = new();
    private readonly ConcurrentDictionary<int, Task> _pumps = new();
    private readonly Task _acceptLoop;
    private int _connectionCount;
    private int _disposed;

    public TcpDropProxy(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The address a client connects to instead of the target.</summary>
    public string Url { get; }

    /// <summary>Connections accepted so far, dropped ones included.</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>
    /// Resets every open connection, on both sides. The listener keeps accepting. Connections accepted after the
    /// call are untouched.
    /// </summary>
    public void DropAllConnections()
    {
        // Removing the keys one by one, rather than clearing the dictionary, leaves a connection that is accepted
        // while this runs tracked, so disposal still closes it.
        foreach (var id in _pairs.Keys)
        {
            if (_pairs.TryRemove(id, out var pair))
                pair.Drop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // Cancel first: a pump that registers its pair after the loop below sees the cancellation and closes it itself.
        await _cts.CancelAsync();
        _listener.Stop();

        foreach (var id in _pairs.Keys)
        {
            if (_pairs.TryRemove(id, out var pair))
                pair.Close();
        }

        try
        {
            await _acceptLoop;
            await Task.WhenAll(_pumps.Values);
        }
        catch (Exception ex) when (IsExpectedSocketFailure(ex))
        {
            // The sockets were closed under the copy loops on purpose.
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
                client.NoDelay = true;
            }
            catch (Exception ex) when (IsExpectedSocketFailure(ex))
            {
                client?.Dispose();

                // Cancellation or a stopped listener ends the accept. Anything else is a transient failure on one
                // connection, such as a client that reset before it was accepted: the next client must still get in.
                if (_cts.IsCancellationRequested || ex is ObjectDisposedException)
                    return;
                continue;
            }

            var id = Interlocked.Increment(ref _connectionCount);

            var pump = PumpAsync(id, client);
            _pumps[id] = pump;
            // Forget a pump when it ends. If it already ended, the continuation runs at once.
            _ = pump.ContinueWith(ended => _pumps.TryRemove(id, out _), TaskScheduler.Default);
        }
    }

    /// <summary>Connects to the target and copies both ways until either side ends or the pair is closed.</summary>
    private async Task PumpAsync(int id, TcpClient client)
    {
        var upstream = new TcpClient { NoDelay = true };
        var pair = new ConnectionPair(client, upstream);
        Task? toTarget = null;
        Task? toClient = null;

        try
        {
            await upstream.ConnectAsync(_target.Host, _target.Port, _cts.Token);

            _pairs[id] = pair;
            if (_cts.IsCancellationRequested)
                return;

            var clientStream = client.GetStream();
            var upstreamStream = upstream.GetStream();
            toTarget = clientStream.CopyToAsync(upstreamStream, _cts.Token);
            toClient = upstreamStream.CopyToAsync(clientStream, _cts.Token);

            await Task.WhenAny(toTarget, toClient);
        }
        catch (Exception ex) when (IsExpectedSocketFailure(ex))
        {
            // The peer went away, or the pair was dropped: either way this connection is over.
        }
        finally
        {
            _pairs.TryRemove(id, out _);
            pair.Close();
        }

        // Closing the pair ends the copy that was still running; observe its failure so it is not left unobserved.
        await ObserveAsync(toTarget);
        await ObserveAsync(toClient);
    }

    private static async Task ObserveAsync(Task? copy)
    {
        if (copy is null)
            return;

        try
        {
            await copy;
        }
        catch (Exception ex) when (IsExpectedSocketFailure(ex))
        {
            // Expected once the sockets are closed.
        }
    }

    private static bool IsExpectedSocketFailure(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException;

    /// <summary>The two sockets of one proxied connection: the client's and the one to the target.</summary>
    private sealed class ConnectionPair(TcpClient client, TcpClient upstream)
    {
        /// <summary>Closes both sockets with a reset, as a dropped network does.</summary>
        public void Drop()
        {
            Reset(client);
            Reset(upstream);
            Close();
        }

        /// <summary>Closes both sockets. Safe to call more than once.</summary>
        public void Close()
        {
            client.Dispose();
            upstream.Dispose();
        }

        private static void Reset(TcpClient socket)
        {
            try
            {
                // Linger with a zero timeout turns the close into a reset instead of an orderly shutdown. A closed
                // TcpClient has no socket left.
                var socketToReset = socket.Client;
                if (socketToReset is not null)
                    socketToReset.LingerState = new LingerOption(true, 0);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Already closed: nothing left to reset.
            }
        }
    }
}
