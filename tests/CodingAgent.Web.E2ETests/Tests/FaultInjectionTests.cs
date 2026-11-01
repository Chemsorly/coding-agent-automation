using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class FaultInjectionTests : E2ETestBase
{
    public FaultInjectionTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task FleetPage_WhenAgentsApiFails_ShowsLoadError_AndRefreshRecovers()
    {
        // Arrange: install the route before navigating — WASM fetches on first render.
        // await using ensures the route is unregistered even if an assertion throws before
        // the explicit DisposeAsync() in the recovery step. DisposeAsync() is idempotent
        // (catches PlaywrightException on the second call).
        await using var fault = await Page.FailApiAsync("/api/ui/agents");

        // Act: navigate to the Fleet page
        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();

        // Assert: error card is visible with "Failed to load agents" and the status code
        var error = Page.Locator(".cockpit-card .cockpit-empty");
        await Assertions.Expect(error).ToContainTextAsync("Failed to load agents", new() { Timeout = 10_000 });
        await Assertions.Expect(error).ToContainTextAsync("500");
        Assert.True(fault.HitCount >= 1);
        Assert.False(await fleet.IsStatTilePresentAsync("Agents"));

        // Act: remove the fault and click Refresh
        await fault.DisposeAsync();
        await Page.ClickAsync("button[title='Refresh now']");

        // Assert: stat strip reappears and error card is gone
        await Assertions.Expect(
            Page.Locator(".cockpit-stat-l", new() { HasTextString = "Agents" }))
            .ToBeVisibleAsync(new() { Timeout = 10_000 });
        await Assertions.Expect(
            error.Filter(new() { HasTextString = "Failed to load agents" }))
            .ToHaveCountAsync(0);
    }
}
