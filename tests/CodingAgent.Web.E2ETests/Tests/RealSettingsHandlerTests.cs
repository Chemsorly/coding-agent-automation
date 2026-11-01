using System.Text.Json;
using System.Text.Json.Serialization;
using CodingAgent.Api;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

[Trait("Category", "E2E")]
[Collection(RealSettingsE2ECollection.Name)]
public sealed class RealSettingsHandlerTests : IAsyncLifetime
{
    private readonly RealSettingsE2EFixture _fixture;
    private IBrowserContext? _context;
    private IPage _page = null!;
    private string _tempDir = null!;

    public RealSettingsHandlerTests(RealSettingsE2EFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions { AcceptDownloads = true });
        _page = await _context.NewPageAsync();
        await E2EFixture.SignInAsync(_page, _fixture.BaseUrl);
        _tempDir = Path.Combine(Path.GetTempPath(), $"e2e-rs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Remove any leftover dismissal row so test 2 always starts with the banner visible.
        await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
        var dismissEntry = await db.KeyValueStore.FindAsync("first_run_banner_dismissed");
        if (dismissEntry is not null)
        {
            db.KeyValueStore.Remove(dismissEntry);
            await db.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_context is not null)
            await _context.DisposeAsync();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task WhenBundleIsImportedThroughThePage_ShouldReachTheDatabaseAndComeBackInExport()
    {
        // Arrange: build a bundle with a unique quality gate and write it to a temp file
        var gateId = Guid.NewGuid();
        var bundle = new ConfigBundle
        {
            PipelineConfig = JsonSerializer.Serialize(new PipelineConfiguration(), PipelineJsonOptions.Default),
            QualityGateConfigs =
            [
                new NamedConfigDto
                {
                    Id = gateId,
                    Name = "Real Handler Gate",
                    Configuration = JsonSerializer.Serialize(
                        new QualityGateConfiguration { Id = gateId.ToString(), DisplayName = "Real Handler Gate", Enabled = true },
                        PipelineJsonOptions.Default)
                }
            ]
        };
        var bundleJson = JsonSerializer.Serialize(
            bundle,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });
        var importPath = Path.Combine(_tempDir, "import.json");
        await File.WriteAllTextAsync(importPath, bundleJson);

        // Act: navigate to Import / Export and import the bundle through the page
        var settingsPage = new SettingsPage(_page, _fixture.BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Import / Export");
        var section = new DataManagementSectionHelper(_page);
        await section.WaitForSectionAsync();
        await section.SelectImportFileAsync(importPath);
        await section.ClickImportAsync();

        // Assert: the page shows success
        Assert.True(await section.IsSuccessMessageVisibleAsync());

        // Assert: the real ImportConfigAsync handler wrote to the EF InMemory database
        await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
        {
            var entityExists = await db.QualityGateConfigs.AnyAsync(q => q.Id == gateId && q.Name == "Real Handler Gate");
            Assert.True(entityExists, "EF InMemory database should contain the imported quality gate config");
        }

        // Assert: the fake config store was NOT used — proves E2ESettingsShim was absent
        var storeConfigs = await _fixture.Factory.ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None);
        Assert.DoesNotContain(storeConfigs, qg => qg.DisplayName == "Real Handler Gate");

        // Act: re-navigate to Import / Export and export the config
        var settingsPage2 = new SettingsPage(_page, _fixture.BaseUrl);
        await settingsPage2.NavigateAsync();
        await settingsPage2.SelectTreeNodeAsync("Import / Export");
        var section2 = new DataManagementSectionHelper(_page);
        await section2.WaitForSectionAsync();
        var exportPath = Path.Combine(_tempDir, "export.json");
        var exportBytes = await section2.ClickDownloadAndSaveAsync(exportPath);

        // Assert: the exported JSON is valid and contains the gate name (ExportConfigAsync read the EF DB)
        var exportText = System.Text.Encoding.UTF8.GetString(exportBytes);
        JsonDocument.Parse(exportText); // throws if not valid JSON
        Assert.Contains("Real Handler Gate", exportText);
    }

    [Fact]
    public async Task WhenFirstRunBannerIsDismissed_ShouldStoreItInTheDatabaseAndStayHidden()
    {
        // Arrange: navigate to overview; the banner is visible because no templates are configured
        // and InitializeAsync removed the dismissal row from the EF database
        await _page.GotoCockpitPageAsync($"{_fixture.BaseUrl}/overview");
        var banner = _page.Locator(".first-run-banner");
        await banner.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        // Act: click dismiss
        await _page.Locator(".first-run-banner-dismiss").ClickAsync();

        // Blazor removes the element from the DOM entirely — use Detached, not Hidden
        await banner.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10_000 });

        // Assert: DismissFirstRunBanner wrote "true" to IKeyValueStore (EF InMemory), not the fake client
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string? dismissedValue = null;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _fixture.Api.Services.CreateScope();
            var kvStore = scope.ServiceProvider.GetRequiredService<IKeyValueStore>();
            dismissedValue = await kvStore.GetAsync("first_run_banner_dismissed", CancellationToken.None);
            if (dismissedValue == "true") break;
            await Task.Delay(50);
        }
        Assert.Equal("true", dismissedValue);

        // Assert: banner stays hidden after reloading the page
        await _page.GotoCockpitPageAsync($"{_fixture.BaseUrl}/overview");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        Assert.Equal(0, await _page.Locator(".first-run-banner").CountAsync());
    }

    [Fact]
    public async Task WhenProjectHasSecrets_BffShouldReturnThemMasked()
    {
        // Arrange: seed a project with a known secret value into the config store
        // (ConfigStore is the registered IProjectStore in ApiE2EWebApplicationFactory)
        var projectId = Guid.NewGuid();
        await _fixture.Factory.ConfigStore.SaveProjectAsync(
            new PipelineProject
            {
                Id = projectId.ToString(),
                Name = "masking",
                Enabled = true,
                TemplateIds = [],
                Secrets = new Dictionary<string, string> { ["API_TOKEN"] = "canary-secret-value" }
            },
            CancellationToken.None);

        // Navigate to /overview so the page has a valid session cookie for same-origin requests
        await _page.GotoCockpitPageAsync($"{_fixture.BaseUrl}/overview");

        // Act: fetch the project through the BFF; the X-CSRF header is required by BffRequestGuard
        var result = await _page.EvaluateAsync<JsonElement>(
            "async id => { const r = await fetch(`/api/ui/settings/projects/${id}`, { headers: { 'X-CSRF': '1' } }); return { status: r.status, body: await r.text() }; }",
            projectId.ToString());

        // Assert: 200 response
        Assert.Equal(200, result.GetProperty("status").GetInt32());

        // Assert: response body does not contain the raw secret
        var body = result.GetProperty("body").GetString()!;
        Assert.DoesNotContain("canary-secret-value", body);

        // Assert: UiSecretMask.Redact replaced the secret value with "****"
        var parsed = JsonSerializer.Deserialize<JsonElement>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var maskedToken = parsed.GetProperty("secrets").GetProperty("API_TOKEN").GetString();
        Assert.Equal("****", maskedToken);
    }
}
