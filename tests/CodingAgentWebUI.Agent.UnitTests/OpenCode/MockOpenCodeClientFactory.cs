namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// A mock <see cref="IHttpClientFactory"/> that returns <see cref="HttpClient"/> instances
/// backed by a <see cref="MockOpenCodeHandler"/>. The base address is set to
/// <c>http://127.0.0.1:4096</c> to match the OpenCode server default.
/// </summary>
public sealed class MockOpenCodeClientFactory : IHttpClientFactory
{
    private readonly MockOpenCodeHandler _handler;

    /// <summary>
    /// The underlying mock handler. Use this to enqueue responses and inspect recorded requests.
    /// </summary>
    public MockOpenCodeHandler Handler => _handler;

    public MockOpenCodeClientFactory(MockOpenCodeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    /// <summary>
    /// Creates a new <see cref="MockOpenCodeClientFactory"/> with a fresh <see cref="MockOpenCodeHandler"/>.
    /// </summary>
    public MockOpenCodeClientFactory() : this(new MockOpenCodeHandler())
    {
    }

    public HttpClient CreateClient(string name)
    {
        // The handler is NOT disposed by the HttpClient — we reuse it across calls.
        // This matches IHttpClientFactory semantics where the handler is pooled.
        var client = new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://127.0.0.1:4096")
        };
        return client;
    }
}
