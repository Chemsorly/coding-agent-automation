using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the first-run banner (rendered by CockpitLayout on every page when no job
/// templates are configured).
///
/// Scenarios tested:
/// 1. Banner is visible when no templates exist.
/// 2. The banner link navigates to the Pipelines page.
/// 3. Dismiss hides the banner, also after a reload.
/// 4. Banner is hidden once a template is configured.
///
/// Isolation note: <see cref="InMemoryPipelineApiConfigClient"/> stores the dismiss flag in its
/// own <c>_keyValues</c> dictionary, which is NOT reset by <see cref="E2EWebApplicationFactory.ResetAll"/>.
/// Each test calls <c>Fixture.Factory.ApiConfigClient.Reset()</c> in its arrange step to ensure
/// a clean dismiss-flag state.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class FirstRunBannerTests : E2ETestBase
{
    public FirstRunBannerTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Scenario 1: With an empty template store, the banner is visible on the Overview page.
    /// </summary>
    [Fact]
    public async Task Banner_IsVisible_WithNoTemplates()
    {
        // Arrange: default seed has no enabled templates; clear dismiss flag for isolation
        Fixture.Factory.ApiConfigClient.Reset();

        // Act: navigate to a page rendered by CockpitLayout
        await Page.GotoCockpitPageAsync($"{BaseUrl}/overview");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });

        // Assert: banner is present and shows the expected message
        var banner = Page.Locator(".first-run-banner");
        await banner.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible, Timeout = 10_000 });
        var bannerText = await banner.TextContentAsync();
        Assert.Contains("No job templates configured", bannerText);
    }

    /// <summary>
    /// Scenario 2: The banner link navigates to the Pipelines page, where the add-template
    /// control is visible.
    /// </summary>
    [Fact]
    public async Task Banner_Link_NavigatesToPipelinesPage()
    {
        // Arrange
        Fixture.Factory.ApiConfigClient.Reset();

        // Act: navigate to overview and wait for banner
        await Page.GotoCockpitPageAsync($"{BaseUrl}/overview");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        var bannerLink = Page.Locator(".first-run-banner a");
        await bannerLink.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible, Timeout = 10_000 });

        // Assert: link href is relative "pipelines" (no leading /) — Blazor resolves against <base href>
        var href = await bannerLink.GetAttributeAsync("href");
        Assert.Equal("pipelines", href);

        // Act: click the link
        await bannerLink.ClickAsync();

        // Assert: navigated to the Pipelines page
        await Page.WaitForURLAsync(url => url.Contains("/pipelines"), new() { Timeout = 10_000 });
        // Wait for the Pipelines page h1 to appear — ensures Blazor has rendered the new page
        // before asserting, avoiding a race where h1 still shows "Overview".
        await Page.WaitForFunctionAsync("document.querySelector('h1')?.textContent?.includes('Pipelines')", null, new() { Timeout = 10_000 });
        var heading = await Page.TextContentAsync("h1");
        Assert.Contains("Pipelines", heading);
    }

    /// <summary>
    /// Scenario 3: Dismiss hides the banner for good. (It used to have no visible effect: the old show
    /// condition kept the banner up whenever no template existed, which is the only time it shows.)
    /// </summary>
    [Fact]
    public async Task Banner_Dismiss_HidesIt_AlsoAfterAReload()
    {
        // Arrange
        Fixture.Factory.ApiConfigClient.Reset();

        await Page.GotoCockpitPageAsync($"{BaseUrl}/overview");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        var banner = Page.Locator(".first-run-banner");
        await banner.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible, Timeout = 10_000 });

        // Act: click dismiss
        await Page.Locator(".first-run-banner-dismiss").ClickAsync();

        // Assert: the banner goes away, and a fresh page load does not bring it back
        await banner.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Detached, Timeout = 10_000 });
        await Page.GotoCockpitPageAsync($"{BaseUrl}/overview");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        Assert.Equal(0, await banner.CountAsync());
    }

    /// <summary>
    /// Scenario 4: Once a template is configured, the banner no longer shows.
    /// </summary>
    [Fact]
    public async Task Banner_IsHidden_WhenTemplateExists()
    {
        // Arrange: seed an enabled template and clear dismiss flag
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "first-run-banner-test-template",
            Name = "Banner Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);
        Fixture.Factory.ApiConfigClient.Reset();

        // Act: navigate to overview
        await Page.GotoCockpitPageAsync($"{BaseUrl}/overview");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });

        // Wait a moment for Blazor circuit to establish and component to initialise
        await Page.WaitForTimeoutAsync(2000);

        // Assert: banner is not present (HasEnabledTemplates returns true)
        var bannerCount = await Page.Locator(".first-run-banner").CountAsync();
        Assert.Equal(0, bannerCount);
    }
}
