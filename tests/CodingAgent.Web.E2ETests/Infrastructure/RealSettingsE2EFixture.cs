using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// E2E hosts with no <see cref="E2ESettingsShim"/>: settings routes reach the real API handlers, which read the
/// EF InMemory database. Seed through the API, not through the config store, for those routes.
/// </summary>
public sealed class RealSettingsE2EFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;

    public E2EWebApplicationFactory Factory { get; } = new();
    public ApiE2EWebApplicationFactory Api { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;
    public string BaseUrl => Factory.ServerAddress;
    public IDbContextFactory<PipelineDbContext> DbContextFactory => Factory.DbContextFactory;

    public async Task InitializeAsync()
    {
        Api = new ApiE2EWebApplicationFactory(
            Factory.DbName,
            Factory.ConfigStore,
            Factory.HistoryService,
            Factory.FakeProviders,
            Factory.FakeK8sClient,
            E2EWebApplicationFactory.TestApiKey);
        using (Api.CreateClient()) { }
        Factory.ApiBaseUrl = Api.ServerAddress;
        using (Factory.CreateClient()) { }
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
            await Browser.DisposeAsync();
        _playwright?.Dispose();
        await Factory.DisposeAsync();
        await Api.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RealSettingsE2ECollection : ICollectionFixture<RealSettingsE2EFixture>
{
    public const string Name = "E2E real settings";
}
