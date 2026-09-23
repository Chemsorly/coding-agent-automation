using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using CodingAgent.Agent;
using CodingAgent.AgentGateway;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Pipeline.UnitTests.Helpers;

/// <summary>
/// In-memory stand-in for the agent hub that reads invocations the way the real hub does: with the
/// production server protocol (<see cref="AgentSignalRServiceCollectionExtensions.AddAgentSignalRCore"/>)
/// and the parameter types of the real <see cref="AgentHub"/> methods. Clients connect through a real
/// <see cref="HubConnection"/> configured with <see cref="AgentHubProtocolExtensions.AddAgentHubProtocol"/>,
/// so the bytes on the in-memory wire are the bytes the agent sends in production.
/// </summary>
/// <remarks>
/// Arguments that cannot be bound never reach a hub method: the hub replies with
/// <see cref="BindingFailureMessage"/> and nothing on the server logs it. The harness sends the same
/// reply and records the failure in <see cref="BindingFailures"/>.
/// </remarks>
internal sealed class InMemoryAgentHub : IConnectionFactory, IAsyncDisposable
{
    private static readonly IInvocationBinder Binder = new AgentHubBinder();

    private readonly ServiceProvider _serverServices;
    private readonly IHubProtocol _protocol;
    private readonly Dictionary<string, object?> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<InvocationMessage> _invocations = new();
    private readonly ConcurrentQueue<InvocationBindingFailureMessage> _bindingFailures = new();
    private readonly List<(IDuplexPipe Transport, Task ServeLoop)> _connections = [];

    public InMemoryAgentHub()
    {
        var services = new ServiceCollection();
        services.AddSignalR().AddAgentSignalRCore();
        _serverServices = services.BuildServiceProvider();
        _protocol = _serverServices.GetServices<IHubProtocol>().Single(p => p.Name == "messagepack");
    }

    /// <summary>Invocations whose arguments bound to the hub method's parameters.</summary>
    public IReadOnlyCollection<InvocationMessage> Invocations => _invocations;

    /// <summary>Invocations the hub rejected because their arguments could not be bound.</summary>
    public IReadOnlyCollection<InvocationBindingFailureMessage> BindingFailures => _bindingFailures;

    /// <summary>
    /// The error the hub sends when it cannot bind an invocation's arguments
    /// (<c>EnableDetailedErrors</c> is off outside Development).
    /// </summary>
    public static string BindingFailureMessage(string hubMethod) =>
        $"Failed to invoke '{hubMethod}' due to an error on the server.";

    /// <summary>Sets the value returned for <paramref name="hubMethod"/>; others complete without a result.</summary>
    public InMemoryAgentHub Returns(string hubMethod, object? result)
    {
        _results[hubMethod] = result;
        return this;
    }

    /// <summary>Opens a started connection configured the way the agent configures its connection.</summary>
    public async Task<HubConnection> ConnectAsync()
    {
        var builder = new HubConnectionBuilder().AddAgentHubProtocol();
        builder.Services.AddSingleton<IConnectionFactory>(this);
        builder.Services.AddSingleton<EndPoint>(new UriEndPoint(new Uri($"http://in-memory{HubRoutes.Agent}")));
        var connection = builder.Build();
        await connection.StartAsync();
        return connection;
    }

    ValueTask<ConnectionContext> IConnectionFactory.ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken)
    {
        var options = new PipeOptions(useSynchronizationContext: false);
        var toHub = new Pipe(options);
        var toClient = new Pipe(options);
        var clientTransport = new DuplexPipe(toClient.Reader, toHub.Writer);
        var hubTransport = new DuplexPipe(toHub.Reader, toClient.Writer);

        lock (_connections)
            _connections.Add((hubTransport, Task.Run(() => ServeAsync(hubTransport))));

        return ValueTask.FromResult<ConnectionContext>(
            new DefaultConnectionContext(Guid.NewGuid().ToString("N"), clientTransport, hubTransport));
    }

    public async ValueTask DisposeAsync()
    {
        (IDuplexPipe Transport, Task ServeLoop)[] connections;
        lock (_connections)
            connections = [.. _connections];

        foreach (var (transport, _) in connections)
            transport.Input.CancelPendingRead();

        // Surfaces any fault in the serve loop as a test failure.
        await Task.WhenAll(connections.Select(c => c.ServeLoop));

        foreach (var (transport, _) in connections)
        {
            await transport.Input.CompleteAsync();
            await transport.Output.CompleteAsync();
        }

        await _serverServices.DisposeAsync();
    }

    private async Task ServeAsync(IDuplexPipe transport)
    {
        if (!await CompleteHandshakeAsync(transport))
            return;

        while (true)
        {
            var result = await transport.Input.ReadAsync();
            var buffer = result.Buffer;
            if (result.IsCanceled)
            {
                transport.Input.AdvanceTo(buffer.Start);
                return;
            }

            while (_protocol.TryParseMessage(ref buffer, Binder, out var message))
                await RespondAsync(message, transport.Output);

            transport.Input.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
                return;
        }
    }

    private static async Task<bool> CompleteHandshakeAsync(IDuplexPipe transport)
    {
        while (true)
        {
            var result = await transport.Input.ReadAsync();
            var buffer = result.Buffer;
            if (!result.IsCanceled && HandshakeProtocol.TryParseRequestMessage(ref buffer, out _))
            {
                transport.Input.AdvanceTo(buffer.Start);
                HandshakeProtocol.WriteResponseMessage(HandshakeResponseMessage.Empty, transport.Output);
                await transport.Output.FlushAsync();
                return true;
            }

            transport.Input.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCanceled || result.IsCompleted)
                return false;
        }
    }

    private async Task RespondAsync(HubMessage message, PipeWriter output)
    {
        var reply = message switch
        {
            InvocationBindingFailureMessage failure => Reject(failure),
            InvocationMessage invocation => Accept(invocation),
            _ => null, // pings and other control messages need no reply
        };
        if (reply is null)
            return;

        _protocol.WriteMessage(reply, output);
        await output.FlushAsync();
    }

    private CompletionMessage? Reject(InvocationBindingFailureMessage failure)
    {
        _bindingFailures.Enqueue(failure);
        return failure.InvocationId is null
            ? null
            : CompletionMessage.WithError(failure.InvocationId, BindingFailureMessage(failure.Target));
    }

    private CompletionMessage? Accept(InvocationMessage invocation)
    {
        _invocations.Enqueue(invocation);
        if (invocation.InvocationId is null)
            return null; // SendAsync: fire-and-forget, no completion expected

        return _results.TryGetValue(invocation.Target, out var result)
            ? CompletionMessage.WithResult(invocation.InvocationId, result)
            : CompletionMessage.Empty(invocation.InvocationId);
    }

    /// <summary>Resolves parameter types from the real <see cref="AgentHub"/> methods, as the hub dispatcher does.</summary>
    private sealed class AgentHubBinder : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) =>
            typeof(AgentHub).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?.GetParameters()
                .Select(p => p.ParameterType)
                .Where(t => t != typeof(CancellationToken))
                .ToArray()
            ?? throw new InvalidOperationException($"{nameof(AgentHub)} has no hub method '{methodName}'.");

        public Type GetReturnType(string invocationId) =>
            throw new NotSupportedException("The hub does not receive completions.");

        public Type GetStreamItemType(string streamId) =>
            throw new NotSupportedException("The hub does not receive streams.");
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }
}
