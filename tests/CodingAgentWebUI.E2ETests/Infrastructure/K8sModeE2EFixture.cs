using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Shared fixture for K8s-mode E2E tests. Creates the WebApplicationFactory once
/// per test class (via IClassFixture). Tests DispatchService → K8s Job creation
/// and KubernetesWorkDistributor → WorkItem insertion.
/// </summary>
public sealed class K8sModeE2EFixture : IAsyncLifetime
{
    public K8sModeE2EWebApplicationFactory Factory { get; } = new();
    public string ServerAddress => Factory.ServerAddress;

    private ApiE2EWebApplicationFactory? _apiFactory;

    /// <summary>
    /// Pipeline API host — the sole host of <c>/hubs/agent</c> since Spec 044.
    /// </summary>
    public string AgentHubUrl => _apiFactory?.ServerAddress
        ?? throw new InvalidOperationException("API host not started");

    public string ApiKey => K8sModeE2EWebApplicationFactory.TestApiKey;

    // Convenience accessors
    public InMemoryConfigurationStore ConfigStore => Factory.ConfigStore;
    public FakeProviderFactory FakeProviders => Factory.FakeProviders;
    public InMemoryIssueProvider IssueProvider => Factory.FakeProviders.IssueProvider;
    public FakeKubernetesJobClient K8sClient => Factory.FakeK8sClient;
    public InMemoryPipelineRunHistoryService HistoryService => Factory.HistoryService;
    public IDbContextFactory<PipelineDbContext> DbContextFactory => Factory.DbContextFactory;

    public async Task InitializeAsync()
    {
        // API first: the monolith reads PipelineApi:BaseUrl during configuration.
        _apiFactory = new ApiE2EWebApplicationFactory(
            Factory.DbName, Factory.ConfigStore, Factory.HistoryService, ApiKey);
        using (var apiClient = _apiFactory.CreateClient()) { }
        Factory.ApiBaseUrl = _apiFactory.ServerAddress;

        // Start the server
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
