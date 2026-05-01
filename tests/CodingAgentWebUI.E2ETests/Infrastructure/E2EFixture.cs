using CodingAgentWebUI.E2ETests.Fakes;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Shared fixture for E2E tests. Creates the WebApplicationFactory and Playwright browser once
/// per test class (via IClassFixture). Exposes server address, browser, and fake providers.
/// </summary>
public sealed class E2EFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;

    public E2EWebApplicationFactory Factory { get; } = new();
    public IBrowser Browser { get; private set; } = null!;
    public string ServerAddress => Factory.ServerAddress;
    public string ApiKey => E2EWebApplicationFactory.TestApiKey;

    // Convenience accessors for fakes
    public InMemoryConfigurationStore ConfigStore => Factory.ConfigStore;
    public FakeProviderFactory FakeProviders => Factory.FakeProviders;
    public InMemoryIssueProvider IssueProvider => Factory.FakeProviders.IssueProvider;
    public InMemoryRepositoryProvider RepositoryProvider => Factory.FakeProviders.RepositoryProvider;
    public ScriptedAgentProvider AgentProvider => Factory.FakeProviders.AgentProvider;
    public ConfigurableQualityGateValidator QualityGateValidator => Factory.QualityGateValidator;

    public async Task InitializeAsync()
    {
        // Start the server (UseKestrel was called in the factory constructor)
        // Creating a client triggers the host to start
        using var _ = Factory.CreateClient();

        // Launch Playwright browser
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
            await Browser.DisposeAsync();
        _playwright?.Dispose();
        await Factory.DisposeAsync();
    }
}
