using CodingAgent.Infrastructure.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// Unit tests for <see cref="HttpClientExtensions.AddStandardHttpClientPoolDefaults"/>.
/// Verifies that the sentinel guard prevents duplicate handler registration, that the
/// fluent API contract is maintained, and that the configured handler has the correct
/// <see cref="System.Net.Http.SocketsHttpHandler.PooledConnectionLifetime"/>.
/// </summary>
public class HttpClientExtensionsTests
{
    [Fact]
    public void AddStandardHttpClientPoolDefaults_RegistersSentinel()
    {
        var services = new ServiceCollection();

        services.AddStandardHttpClientPoolDefaults();

        Assert.Single(services,
            d => d.ServiceType == typeof(HttpClientExtensions.HttpClientPoolDefaultsSentinel));
    }

    [Fact]
    public void AddStandardHttpClientPoolDefaults_CalledTwice_ServiceCountIsUnchanged()
    {
        var services = new ServiceCollection();
        services.AddStandardHttpClientPoolDefaults();
        var countAfterFirst = services.Count;

        services.AddStandardHttpClientPoolDefaults();

        Assert.Equal(countAfterFirst, services.Count);
    }

    [Fact]
    public void AddStandardHttpClientPoolDefaults_ReturnsServices()
    {
        var services = new ServiceCollection();

        var result = services.AddStandardHttpClientPoolDefaults();

        Assert.Same(services, result);
    }

    /// <summary>
    /// Verifies the configured <see cref="System.Net.Http.SocketsHttpHandler"/> has
    /// <see cref="System.Net.Http.SocketsHttpHandler.PooledConnectionLifetime"/> set to 90
    /// seconds. Guards against silent regressions if the lifetime constant is changed.
    /// </summary>
    [Fact]
    public void AddStandardHttpClientPoolDefaults_SetsPooledConnectionLifetimeTo90Seconds()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddStandardHttpClientPoolDefaults();
        var sp = services.BuildServiceProvider();

        // ConfigureHttpClientDefaults registers IConfigureOptions<HttpClientFactoryOptions>
        // (no name filter), so its HttpMessageHandlerBuilderActions are present on every
        // named instance including the default empty-string name.
        var options = sp.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(string.Empty);
        var builder = new TestHandlerBuilder(sp);
        foreach (var action in options.HttpMessageHandlerBuilderActions)
            action(builder);

        var handler = Assert.IsType<SocketsHttpHandler>(builder.PrimaryHandler);
        Assert.Equal(TimeSpan.FromSeconds(90), handler.PooledConnectionLifetime);
    }

    /// <summary>
    /// Minimal <see cref="HttpMessageHandlerBuilder"/> implementation used to capture and
    /// inspect the primary handler configured by <c>ConfigurePrimaryHttpMessageHandler</c>.
    /// </summary>
    private sealed class TestHandlerBuilder : HttpMessageHandlerBuilder
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly List<DelegatingHandler> _additionalHandlers = [];

        public TestHandlerBuilder(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public override IServiceProvider Services => _serviceProvider;
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
        public override IList<DelegatingHandler> AdditionalHandlers => _additionalHandlers;
        public override HttpMessageHandler Build() => PrimaryHandler;
    }
}
