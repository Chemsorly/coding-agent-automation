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
    public string ApiKey => K8sChatE2EWebApplicationFactory.TestApiKey;

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
