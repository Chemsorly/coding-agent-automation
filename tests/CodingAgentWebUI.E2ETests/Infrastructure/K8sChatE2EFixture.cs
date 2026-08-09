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
#pragma warning disable CA1822 // API key property accessed via instance throughout the test suite; making it static would break all callers
    public string ApiKey => K8sChatE2EWebApplicationFactory.TestApiKey;
#pragma warning restore CA1822

    // Convenience accessors
    public InMemoryConfigurationStore ConfigStore => Factory.ConfigStore;
    public FakeKubernetesJobClient K8sClient => Factory.FakeK8sClient;

    public async Task InitializeAsync()
    {
        // Start the server (creating a client triggers host start)
        using var _ = Factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
    }
}
