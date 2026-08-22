namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Test double for <see cref="IHubConnectionManagerFactory"/>.
/// Delegates creation to a caller-supplied factory function, allowing tests to inject
/// arbitrary <see cref="IHubConnectionManager"/> instances (e.g., <see cref="FakeHubConnectionManager"/>).
/// </summary>
internal sealed class FakeHubConnectionManagerFactory : IHubConnectionManagerFactory
{
    private readonly Func<IHubConnectionManager> _factory;
    public int CreateCallCount { get; private set; }

    public FakeHubConnectionManagerFactory(Func<IHubConnectionManager> factory)
    {
        _factory = factory;
    }

    public IHubConnectionManager Create()
    {
        CreateCallCount++;
        return _factory();
    }
}
