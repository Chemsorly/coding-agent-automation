using CodingAgentWebUI.E2ETests.Fakes;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Shared fixture for K8s chat E2E tests.
/// Creates the <see cref="K8sChatE2EWebApplicationFactory"/> once per test class
/// (via <see cref="Xunit.IClassFixture{TFixture}"/>).
/// </summary>
public sealed class K8sChatE2EFixture : IAsyncLifetime
{
    public K8sChatE2EWebApplicationFactory Factory { get; } = new();

    public string ServerAddress => Factory.ServerAddress;

    private ApiE2EWebApplicationFactory? _apiFactory;

    /// <summary>
    /// Pipeline API host — the sole host of <c>/hubs/agent</c> since Spec 044, so chat agent
    /// pods register here rather than on the Blazor app.
    /// </summary>
    public string AgentHubUrl => _apiFactory?.ServerAddress
        ?? throw new InvalidOperationException("API host not started");
#pragma warning disable CA1822 // API key property accessed via instance throughout the test suite; making it static would break all callers
    public string ApiKey => K8sChatE2EWebApplicationFactory.TestApiKey;
#pragma warning restore CA1822

    // Convenience accessors
    public InMemoryConfigurationStore ConfigStore => Factory.ConfigStore;
    public FakeKubernetesJobClient K8sClient => Factory.FakeK8sClient;

    public async Task InitializeAsync()
    {
        // API first: the monolith reads PipelineApi:BaseUrl during configuration.
        // This factory keeps no run-history fake of its own; the chat tests assert on the
        // K8s job client, not on history.
        _apiFactory = new ApiE2EWebApplicationFactory(
            Factory.DbName, Factory.ConfigStore, new Fakes.InMemoryPipelineRunHistoryService(), ApiKey);
        using (var apiClient = _apiFactory.CreateClient()) { }
        Factory.ApiBaseUrl = _apiFactory.ServerAddress;

        // Start the server (creating a client triggers host start)
        using var _ = Factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (_apiFactory is not null)
            await _apiFactory.DisposeAsync();
    }
}
