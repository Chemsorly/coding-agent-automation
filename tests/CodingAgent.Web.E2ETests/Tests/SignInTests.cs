using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class SignInTests : E2ETestBase
{
    public SignInTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Verifies that <see cref="E2EFixture.SignInAsync"/> succeeds even when the post-login landing
    /// page's JavaScript resources are severely delayed. The fix uses <c>WaitUntilState.Commit</c>
    /// instead of the default <c>Load</c>, so it returns as soon as the browser commits to a URL
    /// outside /login — before any scripts on the landing page have a chance to run.
    /// </summary>
    [Fact]
    public async Task SignIn_SlowLandingPage_StillSignsIn()
    {
        var browser = await Fixture.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();

        await context.RouteAsync("**/_framework/blazor.web.js*", async route =>
        {
            if (route.Request.Frame.Url.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                await route.ContinueAsync();
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(35));
                try { await route.ContinueAsync(); } catch (PlaywrightException) { }
            }
        });

        var page = await context.NewPageAsync();

        await E2EFixture.SignInAsync(page, Fixture.ServerAddress);

        Assert.Contains(await context.CookiesAsync(), c => c.Name == "ca_session");
    }
}
