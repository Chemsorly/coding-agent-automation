using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Smoke tests that validate the E2E infrastructure works:
/// factory starts, Kestrel binds, Playwright connects, page loads.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class SmokeTests : E2ETestBase
{
    public SmokeTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task App_Starts_And_PageLoads()
    {
        // Navigate to the root — should redirect to /agent-coding or show the app
        var response = await Page.GotoCockpitPageAsync(BaseUrl);

        // Verify we got a successful response
        Assert.NotNull(response);
        Assert.True(response.Ok, $"Expected 200 OK but got {response.Status}");
    }

    [Fact]
    public async Task AgentCoding_Page_Loads()
    {
        // /agent-coding is the Pipelines page (the route is kept as an alias); its heading is "Pipelines".
        await Page.GotoCockpitPageAsync($"{BaseUrl}/agent-coding");

        // Wait for the page to render (Blazor Server needs a moment to establish circuit)
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 10_000 });

        var heading = await Page.TextContentAsync("h1");
        Assert.Contains("Pipelines", heading);
    }

    [Fact]
    public async Task Settings_Page_Loads()
    {
        await Page.GotoCockpitPageAsync($"{BaseUrl}/settings");

        await Page.WaitForSelectorAsync("h1", new() { Timeout = 10_000 });

        var heading = await Page.TextContentAsync("h1");
        Assert.Contains("Settings", heading);
    }

    [Fact]
    public async Task Fleet_Page_Loads()
    {
        await Page.GotoCockpitPageAsync($"{BaseUrl}/fleet");

        await Page.WaitForSelectorAsync("h1", new() { Timeout = 10_000 });

        var heading = await Page.TextContentAsync("h1");
        Assert.Contains("Fleet", heading);
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

        await Page.GotoCockpitPageAsync($"{BaseUrl}/agent-coding");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 10_000 });

        // Wait for Blazor framework scripts to load
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('script[src*=\"blazor\"]') !== null",
            null,
            new() { Timeout = 10_000 });

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

    /// <summary>
    /// Smoke test for all cockpit routes: navigates each route in a single browser session (loop,
    /// not [Theory]) to reuse the Blazor circuit and stay within the 15 s time budget.
    /// For each route verifies: correct &lt;h1&gt; text, its nav item is marked active, no Blazor
    /// error UI is visible, and no console errors were emitted.
    /// </summary>
    [Fact]
    public async Task All_Cockpit_Routes_Load_With_Correct_Heading_And_Active_Nav()
    {
        // (route, expected h1 substring)
        var routes = new (string Route, string ExpectedHeading)[]
        {
            ("/overview",     "Overview"),
            ("/work",         "Work"),
            ("/runs",         "Runs"),
            ("/triage",       "Triage"),
            ("/fleet",        "Fleet"),
            ("/attention",    "Attention"),
            ("/insights",     "Insights"),
            ("/pipelines",    "Pipelines"),
            ("/consolidation","Consolidation"),
            ("/settings",     "Settings"),
            ("/knowledge",    "Knowledge"),
            ("/agent-chat",   "Agent Chat"),
            ("/about",        "About"),
        };

        // Wire console error listener once for the entire session (not per route).
        // Filter to Type == "error" only — console.warn from SignalR WebSocket fallback is benign.
        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type == "error")
                consoleErrors.Add($"[{msg.Type}] {msg.Text}");
        };

        foreach (var (route, expectedHeading) in routes)
        {
            consoleErrors.Clear();

            await Page.GotoCockpitPageAsync($"{BaseUrl}{route}");
            await Page.WaitForSelectorAsync("h1", new() { Timeout = 10_000 });

            // Verify the correct page rendered.
            var heading = await Page.TextContentAsync("h1");
            Assert.True(
                heading != null && heading.Contains(expectedHeading),
                $"Route {route}: expected <h1> to contain \"{expectedHeading}\" but got \"{heading}\"");

            // Verify the nav item for this route is marked active.
            // CockpitLayout uses href without a leading slash (e.g. href="overview").
            // GotoCockpitPageAsync provides a stronger interactivity guarantee: it waits until
            // the circuit has rendered CockpitLayout, so NavLink's active class is reliable
            // without an additional explicit wait.
            var navHref = route.TrimStart('/');
            var navLink = Page.Locator($".cockpit-nav-link[href='{navHref}']");
            var navClasses = await navLink.GetAttributeAsync("class") ?? "";
            Assert.True(
                navClasses.Contains("active"),
                $"Route {route}: expected nav item href=\"{navHref}\" to have class \"active\" but got \"{navClasses}\"");

            // Verify no Blazor error UI is displayed.
            var blazorErrorUi = Page.Locator("#blazor-error-ui");
            var isBlazorErrorVisible = await blazorErrorUi.IsVisibleAsync();
            Assert.False(
                isBlazorErrorVisible,
                $"Route {route}: Blazor error UI (#blazor-error-ui) is visible — the circuit may have crashed");

            // Verify no console errors were emitted for this route.
            Assert.True(
                consoleErrors.Count == 0,
                $"Route {route}: unexpected console errors: {string.Join("; ", consoleErrors)}");
        }
    }

    /// <summary>
    /// Smoke test for /runs/{id}: seeds a completed run, navigates to its detail page, and
    /// verifies the page renders the run's title in the &lt;h1&gt;.
    /// </summary>
    [Fact]
    public async Task RunDetail_Page_Loads_For_Seeded_Completed_Run()
    {
        var runId = Guid.NewGuid();
        const string issueTitle = "Smoke test run";

        // Seed a completed run into the in-memory history service.
        // AddRunSummaryAsync inserts the summary directly; GetRunAsync(Guid) on the API host
        // looks it up without the terminal-step filter that GetRunHistoryAsync applies.
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = runId.ToString(),
            IssueIdentifier = new IssueIdentifier("1"),
            IssueTitle = issueTitle,
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
#pragma warning disable CS0618
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5).DateTime,
#pragma warning restore CS0618
        });

        await Page.GotoCockpitPageAsync($"{BaseUrl}/runs/{runId}");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 10_000 });

        var heading = await Page.TextContentAsync("h1");
        Assert.True(
            heading != null && heading.Contains(issueTitle),
            $"/runs/{runId}: expected <h1> to contain \"{issueTitle}\" but got \"{heading}\"");
    }

    [Fact]
    public async Task GotoCockpitPageAsync_ReturnsOnlyWhenLayoutIsInteractiveAndAccessChecked()
    {
        await Page.GotoCockpitPageAsync($"{BaseUrl}/overview");

        var ready = await Page.EvaluateAsync<bool>("""
            () => {
                const toggle = document.querySelector('.cockpit-theme-toggle');
                return !!toggle
                    && Object.getOwnPropertyNames(toggle).some(k => k.startsWith('_blazorEvents_'))
                    && !document.querySelector('.auth-authorizing');
            }
            """);
        Assert.True(ready, "GotoCockpitPageAsync returned before CockpitLayout was interactive and its access check had finished");
    }

    [Fact]
    public async Task GotoCockpitPageAsync_PageWithoutCockpitLayout_ThrowsTimeoutNamingTheSignal()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => Page.GotoCockpitPageAsync("about:blank", timeoutMs: 1_000));

        Assert.Contains(".cockpit-theme-toggle", ex.Message);
        Assert.Contains("about:blank", ex.Message);
        Assert.Contains("1000", ex.Message);
        Assert.NotNull(ex.InnerException);
    }
}
