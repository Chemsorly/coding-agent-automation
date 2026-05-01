using CodingAgentWebUI.E2ETests.Infrastructure;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Smoke tests that validate the E2E infrastructure works:
/// factory starts, Kestrel binds, Playwright connects, page loads.
/// </summary>
[Trait("Category", "E2E")]
public sealed class SmokeTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public SmokeTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task App_Starts_And_PageLoads()
    {
        // Navigate to the root — should redirect to /agent-coding or show the app
        var response = await Page.GotoAsync(BaseUrl);

        // Verify we got a successful response
        Assert.NotNull(response);
        Assert.True(response.Ok, $"Expected 200 OK but got {response.Status}");
    }

    [Fact]
    public async Task AgentCoding_Page_Loads()
    {
        await Page.GotoAsync($"{BaseUrl}/agent-coding");

        // Wait for the page to render (Blazor Server needs a moment to establish circuit)
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 10_000 });

        var heading = await Page.TextContentAsync("h1");
        Assert.Contains("Agent Coding", heading);
    }

    [Fact]
    public async Task Settings_Page_Loads()
    {
        await Page.GotoAsync($"{BaseUrl}/settings");

        await Page.WaitForSelectorAsync("h1", new() { Timeout = 10_000 });

        var heading = await Page.TextContentAsync("h1");
        Assert.Contains("Settings", heading);
    }

    [Fact]
    public async Task AgentMonitoring_Page_Loads()
    {
        await Page.GotoAsync($"{BaseUrl}/agent-monitoring");

        await Page.WaitForSelectorAsync("h1", new() { Timeout = 10_000 });

        var heading = await Page.TextContentAsync("h1");
        Assert.Contains("Agent", heading);
    }

    [Fact]
    public async Task Blazor_Circuit_Connects()
    {
        // Track ALL network responses
        var responses = new List<(string Url, int Status)>();
        Page.Response += (_, response) =>
        {
            responses.Add((response.Url, response.Status));
        };

        await Page.GotoAsync($"{BaseUrl}/agent-coding");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 10_000 });
        await Page.WaitForTimeoutAsync(3000);

        // Get the page HTML to check what script src is used
        var scriptSrc = await Page.EvaluateAsync<string>(
            "() => { const s = document.querySelector('script[src*=\"blazor\"]'); return s ? s.src : 'NOT FOUND'; }");

        // Find framework-related responses
        var frameworkResponses = responses.Where(r => r.Url.Contains("_framework") || r.Url.Contains("blazor")).ToList();
        var failedResponses = responses.Where(r => r.Status >= 400).ToList();

        var diagnostics = $"ScriptSrc={scriptSrc}; " +
            $"FrameworkReqs={string.Join("|", frameworkResponses.Select(r => $"{r.Status}:{new Uri(r.Url).PathAndQuery}"))}; " +
            $"FailedReqs={string.Join("|", failedResponses.Select(r => $"{r.Status}:{new Uri(r.Url).PathAndQuery}"))}; " +
            $"TotalReqs={responses.Count}";

        // The test passes if blazor.web.js loads successfully
        var blazorJs = frameworkResponses.FirstOrDefault(r => r.Url.Contains("blazor"));
        Assert.True(blazorJs.Status == 200,
            $"blazor.web.js not served (status={blazorJs.Status}). Diagnostics: {diagnostics}");
    }
}
