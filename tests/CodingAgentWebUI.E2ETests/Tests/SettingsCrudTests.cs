using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// E2E tests for the Settings page Agent Provider CRUD flow.
/// Validates: navigate → view pre-seeded provider → add new provider → verify in list → delete → verify removed.
/// </summary>
[Trait("Category", "E2E")]
public sealed class SettingsCrudTests : E2ETestBase, IClassFixture<E2EFixture>
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

        // Act: Delete "New Test Provider"
        await settingsPage.ClickDeleteProviderAsync("New Test Provider");

        // Assert: "New Test Provider" is no longer in the list
        var providersAfterDelete = await settingsPage.GetProviderNamesAsync();
        Assert.DoesNotContain("New Test Provider", providersAfterDelete);

        // The pre-seeded provider should still be there
        Assert.Contains("E2E Agent Provider", providersAfterDelete);
    }
}
