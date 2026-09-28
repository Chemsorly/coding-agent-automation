using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the Settings page Agent Provider CRUD flow.
/// Validates: navigate → view pre-seeded provider → add new provider → edit → verify renamed
/// and ID unchanged → delete → verify removed.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class SettingsCrudTests : E2ETestBase
{
    public SettingsCrudTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Settings_AgentProvider_AddEditDelete()
    {
        // Arrange
        var settingsPage = new SettingsPage(Page, BaseUrl);

        // Act: Navigate to settings
        await settingsPage.NavigateAsync();

        // Select the "Agent" tree node to view agent providers
        await settingsPage.SelectTreeNodeAsync("Agent");

        // Assert: pre-seeded "E2E Agent Provider" is visible (from InMemoryConfigurationStore.SeedDefaults)
        var initialProviders = await settingsPage.GetProviderNamesAsync();
        Assert.Contains("E2E Agent Provider", initialProviders);

        // Act: Add a new agent provider
        await settingsPage.ClickAddProviderAsync();
        await settingsPage.FillDisplayNameAsync("New Test Provider");
        await settingsPage.ClickSaveAsync();

        // Assert: "New Test Provider" appears in the provider list
        var providersAfterAdd = await settingsPage.GetProviderNamesAsync();
        Assert.Contains("New Test Provider", providersAfterAdd);

        // Assert: success status message appears
        var hasSuccessMessage = await settingsPage.IsStatusMessageVisibleAsync("saved");
        Assert.True(hasSuccessMessage, "Expected a success status message containing 'saved'");

        // --- Scenario 1: Edit step (previously missing) ---

        // Capture the ID of the new provider before editing
        var configs = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);
        var newConfig = configs.FirstOrDefault(c => c.DisplayName == "New Test Provider");
        Assert.NotNull(newConfig);
        var originalId = newConfig.Id;

        // Act: Edit "New Test Provider" → rename to "Renamed Provider"
        await settingsPage.ClickEditProviderAsync("New Test Provider");
        await settingsPage.FillDisplayNameAsync("Renamed Provider");
        await settingsPage.ClickSaveAsync();

        // Assert: "Renamed Provider" now appears, "New Test Provider" is gone
        var providersAfterEdit = await settingsPage.GetProviderNamesAsync();
        Assert.Contains("Renamed Provider", providersAfterEdit);
        Assert.DoesNotContain("New Test Provider", providersAfterEdit);

        // Assert: The underlying config ID is unchanged after the rename
        var configsAfterEdit = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);
        var renamedConfig = configsAfterEdit.FirstOrDefault(c => c.DisplayName == "Renamed Provider");
        Assert.NotNull(renamedConfig);
        Assert.Equal(originalId, renamedConfig.Id);

        // Act: Delete "Renamed Provider"
        await settingsPage.ClickDeleteProviderAsync("Renamed Provider");

        // Assert: "Renamed Provider" is no longer in the list
        var providersAfterDelete = await settingsPage.GetProviderNamesAsync();
        Assert.DoesNotContain("Renamed Provider", providersAfterDelete);

        // The pre-seeded provider should still be there
        Assert.Contains("E2E Agent Provider", providersAfterDelete);
    }
}
