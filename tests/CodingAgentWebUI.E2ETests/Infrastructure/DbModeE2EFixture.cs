using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Shared fixture for DB-mode E2E tests. Creates the WebApplicationFactory once
/// per test class (via IClassFixture). No Playwright — these are SignalR + DB assertion tests.
/// </summary>
public sealed class DbModeE2EFixture : IAsyncLifetime
{
    public DbModeE2EWebApplicationFactory Factory { get; } = new();
    public string ServerAddress => Factory.ServerAddress;
    public string ApiKey => DbModeE2EWebApplicationFactory.TestApiKey;

    // Convenience accessors
    public InMemoryConfigurationStore ConfigStore => Factory.ConfigStore;
    public FakeProviderFactory FakeProviders => Factory.FakeProviders;
    public InMemoryIssueProvider IssueProvider => Factory.FakeProviders.IssueProvider;
    public InMemoryRepositoryProvider RepositoryProvider => Factory.FakeProviders.RepositoryProvider;
    public ConfigurableQualityGateValidator QualityGateValidator => Factory.QualityGateValidator;
    public InMemoryPipelineRunHistoryService HistoryService => Factory.HistoryService;
    public IDbContextFactory<PipelineDbContext> DbContextFactory => Factory.DbContextFactory;

    public async Task InitializeAsync()
    {
        // Start the server (UseKestrel was called in factory constructor)
        using var _ = Factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
    }
}
