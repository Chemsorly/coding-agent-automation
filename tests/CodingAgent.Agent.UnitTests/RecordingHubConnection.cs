using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>A hub invocation recorded by <see cref="RecordingHubConnection"/>.</summary>
internal sealed record HubInvocation(string MethodName, object?[] Args);

/// <summary>
/// <see cref="HubConnection"/> test double that never opens a transport. The <c>InvokeAsync</c> extension
/// methods end in <c>InvokeCoreAsync</c>, which this class overrides to record the call and complete it
/// (or fail it with <see cref="InvokeException"/>), so tests can observe hub calls such as
/// <c>RegisterAgent</c> and <c>DeregisterAgent</c> without a server.
/// </summary>
internal sealed class RecordingHubConnection : HubConnection
{
    private readonly List<HubInvocation> _invocations = [];

    public RecordingHubConnection()
        : base(
            Mock.Of<IConnectionFactory>(),
            new JsonHubProtocol(),
            new UriEndPoint(new Uri("http://localhost:9999/hubs/agent")),
            // TODO: BuildServiceProvider() returns an IDisposable that is never disposed. Benign with one
            // instance per test, but consider storing the provider and disposing it, or using a NullServiceProvider
            // if the base constructor accepts one.
            new ServiceCollection().BuildServiceProvider(),
            NullLoggerFactory.Instance)
    {
    }

    /// <summary>When set, every invocation fails with this exception.</summary>
    public Exception? InvokeException { get; set; }

    /// <summary>Snapshot of the invocations recorded so far, in call order.</summary>
    public IReadOnlyList<HubInvocation> Invocations
    {
        get
        {
            lock (_invocations)
                return _invocations.ToList();
        }
    }

    public override Task<object?> InvokeCoreAsync(
        string methodName, Type returnType, object?[] args, CancellationToken cancellationToken = default)
    {
        lock (_invocations)
            _invocations.Add(new HubInvocation(methodName, args));

        return InvokeException is null
            ? Task.FromResult<object?>(null)
            : Task.FromException<object?>(InvokeException);
    }
}
