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
/// 3. Dismiss — with no templates the banner stays visible (Req 8.2).
/// 4. Banner is hidden once a template is configured.
///
/// Isolation note: <see cref="InMemoryPipelineApiConfigClient"/> stores the dismiss flag in its
/// own <c>_keyValues</c> dictionary, which is NOT reset by <see cref="E2EWebApplicationFactory.ResetAll"/>.
/// Each test calls <c>Fixture.Factory.ApiConfigClient.Reset()</c> in its arrange step to ensure
/// a clean dismiss-flag state.
/// </summary>
// TODO [WARNING]: The dismiss-flag isolation relies on each test calling ApiConfigClient.Reset() in
// its own arrange step. If a test is added to this class that does NOT call Reset(), it will inherit
// stale dismiss state from a previously run test. Consider implementing IAsyncLifetime with a
// per-test Reset() call in InitializeAsync() to enforce isolation unconditionally.
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
        // TODO [WARNING]: The issue's Scenario 2 requires that "the add-template control is visible
        // there". This assertion only checks the <h1> heading. Add an assertion that the
        // Pipeline Job Templates section / "+ Add" button is also present and visible on the page.
    }

    /// <summary>
    /// Scenario 3: Dismiss behavior when no templates exist.
    ///
    /// Req 8.2: dismissal has no visible effect when there are no templates. The show condition
    /// is <c>noEnabledTemplates AND !onPipelines AND (!dismissed OR noEnabledTemplates)</c>.
    /// When noEnabledTemplates is true the third clause is always true, so the banner remains
    /// visible regardless of the dismiss flag.
    /// </summary>
    [Fact]
    public async Task Banner_Dismiss_HasNoVisibleEffect_WhenNoTemplatesExist()
    {
        // Arrange
        Fixture.Factory.ApiConfigClient.Reset();

        // Act: navigate to overview and wait for banner
        await Page.GotoCockpitPageAsync($"{BaseUrl}/overview");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        var banner = Page.Locator(".first-run-banner");
        await banner.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible, Timeout = 10_000 });

        // Act: click dismiss
        var dismissButton = Page.Locator(".first-run-banner-dismiss");
        await dismissButton.ClickAsync();

        // Allow Blazor Server to process the click via SignalR round-trip and re-render
        await Page.WaitForTimeoutAsync(2000);
        // TODO [WARNING]: Fixed sleep is unreliable under load or on slow CI runners. The dismiss
        // click may not have propagated via SignalR before the assertion runs, making the subsequent
        // WaitForAsync(Visible) a false positive (banner still visible for the wrong reason).
        // Consider waiting for a deterministic state change instead (e.g. dismiss button briefly
        // disabled, or network idle) to confirm the round-trip completed before asserting.

        // Assert: Req 8.2 — banner is still visible because no templates exist.
        // The dismiss flag is stored but the show condition's third clause
        // (!_dismissed || noEnabledTemplates) evaluates to true when noEnabledTemplates is true,
        // so dismissal is intentionally overridden.
        await banner.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible, Timeout = 5_000 });
        var bannerText = await banner.TextContentAsync();
        Assert.Contains("No job templates configured", bannerText);
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
        // TODO [WARNING]: Fixed sleep can produce a false positive — CountAsync() returning 0
        // because the layout hasn't rendered yet (circuit not established) rather than because
        // the banner logic correctly hid it. Replace with an explicit absence wait, e.g.:
        //   await Expect(Page.Locator(".first-run-banner")).ToHaveCountAsync(0, new() { Timeout = 5000 });
        // after first waiting for a known-ready page element to confirm the circuit is up.

        // Assert: banner is not present (HasEnabledTemplates returns true)
        var bannerCount = await Page.Locator(".first-run-banner").CountAsync();
        Assert.Equal(0, bannerCount);
    }
}
