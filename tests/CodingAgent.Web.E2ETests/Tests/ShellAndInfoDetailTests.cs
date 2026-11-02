using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests covering gaps in the new-version banner, the Refresh button, the User Profile page
/// (admin, unbound user, reader), and the About page (no runs, scoped user, global reader).
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class ShellAndInfoDetailTests : E2ETestBase
{
    public ShellAndInfoDetailTests(E2EFixture fixture) : base(fixture) { }

    // ── New-version banner ────────────────────────────────────────────────────

    [Fact]
    public async Task DifferentAppVersionHeader_ShowsTheBanner_AndReloadClearsIt()
    {
        // Intercept the runs API and inject a different app-version header.
        // BffHttpHandler reports X-App-Version to VersionWatch; a value different from
        // the page's app-version meta (default "local") sets IsOutdated = true.
        await Page.RouteAsync("**/api/ui/runs?*", async route =>
        {
            var response = await route.FetchAsync();
            var headers = new Dictionary<string, string>(response.Headers) { ["x-app-version"] = "different-build" };
            await route.FulfillAsync(new() { Response = response, Headers = headers });
        });

        await Page.GotoCockpitPageAsync($"{BaseUrl}/runs");

        // Banner should be visible
        var banner = Page.Locator("[data-testid='new-version-banner']");
        await Assertions.Expect(banner).ToContainTextAsync("A new version is available.");

        // Mark the window so we can detect the reload
        await Page.EvaluateAsync("() => { window.__beforeReload = true; }");

        // Remove the route so the reload gets a normal (same-version) response
        await Page.UnrouteAsync("**/api/ui/runs?*");

        // Click the reload button
        await Page.Locator("[data-testid='new-version-reload']").ClickAsync();

        // Wait for the reload to complete (window.__beforeReload will be gone after the reload)
        await Page.WaitForFunctionAsync("() => window.__beforeReload === undefined");
        await Page.WaitForCockpitPageReadyAsync();

        // After reload with a same-version response, banner should be gone
        Assert.Equal(0, await Page.Locator("[data-testid='new-version-banner']").CountAsync());
    }

    [Fact]
    public async Task SameVersion_ShowsNoBanner()
    {
        await Page.GotoCockpitPageAsync($"{BaseUrl}/runs");
        await Page.WaitForSelectorAsync("h1");

        Assert.Equal(0, await Page.Locator("[data-testid='new-version-banner']").CountAsync());
    }

    // ── Refresh button ────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshOnRuns_ShowsANewRun()
    {
        // Navigate with no runs
        await Page.GotoCockpitPageAsync($"{BaseUrl}/runs");

        // Seed a run after the page has loaded
        var started = DateTimeOffset.UtcNow.AddMinutes(-2);
        var completed = started + TimeSpan.FromMinutes(1);
#pragma warning disable CS0618
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9651"),
            IssueTitle = "Issue 9651",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = started,
            StartedAt = started.DateTime,
            CompletedAtOffset = completed,
            CompletedAt = completed.DateTime,
        });
#pragma warning restore CS0618

        // Click Refresh now
        await Page.Locator("button[title='Refresh now']").ClickAsync();

        // The new run should appear
        var runRow = Page.Locator("tbody tr:has-text('#9651')");
        await Assertions.Expect(runRow).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }

    [Fact]
    public async Task RefreshOnWork_ShowsANewQueueItem()
    {
        await Page.GotoCockpitPageAsync($"{BaseUrl}/work");
        var work = new WorkPage(Page, BaseUrl);

        // Seed a Pending item after the page has loaded (following AccessControlTests.SeedPendingItemAsync)
        var workItemId = Guid.NewGuid();
        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "9652",
                IssueProviderConfigId = "issue-e2e",
                Status = WorkItemStatus.Pending,
                Payload = "{}",
                AgentSelector = "kiro,dotnet",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                TimeoutSeconds = 3600,
                ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
            });
            await db.SaveChangesAsync();
        }

        // Click Refresh now
        await Page.Locator("button[title='Refresh now']").ClickAsync();

        // The new queue item should appear
        await Assertions.Expect(work.QueueRow("9652")).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }

    // ── User Profile ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Profile_LocalAdmin_SaysBindingsDoNotApply()
    {
        await Page.GotoCockpitPageAsync($"{BaseUrl}/user");

        await Assertions.Expect(Page.Locator("[data-testid='profile-global-role']")).ToHaveTextAsync("admin");
        await Assertions.Expect(Page.GetByText("The local admin account is always a global admin.")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Profile_UserWithoutGroupsOrBindings_SaysSo()
    {
        var browser = await Fixture.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions());
        var page = await context.NewPageAsync();

        // erin has no binding and no groups; /test/sign-in redirects to /user
        await page.GotoCockpitPageAsync(E2ETestSignIn.SignInUrl(BaseUrl, "erin"));
        await page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });

        await Assertions.Expect(page.Locator("[data-testid='profile-no-groups']")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[data-testid='profile-no-bindings']"))
            .ToContainTextAsync("No binding matches this account");
        await Assertions.Expect(page.Locator("[data-testid='profile-global-role']")).ToHaveTextAsync("none");
    }

    [Fact]
    public async Task Profile_GlobalReader_ShowsRoleAndBinding()
    {
        var browser = await Fixture.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions());
        var page = await context.NewPageAsync();

        await page.GotoCockpitPageAsync(E2ETestSignIn.SignInUrl(BaseUrl, E2ETestSignIn.ReaderUser));
        await page.GotoCockpitPageAsync($"{BaseUrl}/user");

        await Assertions.Expect(page.Locator("[data-testid='profile-global-role']")).ToHaveTextAsync("readonly");
        await Assertions.Expect(page.Locator("[data-testid='profile-bindings']")).ToBeVisibleAsync();
    }

    // ── About page ────────────────────────────────────────────────────────────

    [Fact]
    public async Task About_NoRuns_SaysSo()
    {
        await Page.GotoCockpitPageAsync($"{BaseUrl}/about");

        var mutedText = Page.Locator("[data-testid='about-stats'] .about-muted");
        await Assertions.Expect(mutedText).ToHaveTextAsync("No pipeline runs yet.");
    }

    [Fact]
    public async Task About_ScopedUser_HasNoStats()
    {
        var browser = await Fixture.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions());
        var page = await context.NewPageAsync();

        // alice is an operator on the E2E Team project (not a GlobalRead user)
        await page.GotoCockpitPageAsync(E2ETestSignIn.SignInUrl(BaseUrl, "alice", E2ETestSignIn.TeamGroup));
        await page.GotoCockpitPageAsync($"{BaseUrl}/about");

        // Assert h1 is visible first to confirm the page has rendered (avoid count-0 false pass)
        await page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });

        Assert.Equal(0, await page.Locator("[data-testid='about-stats']").CountAsync());
    }

    [Fact]
    public async Task About_GlobalReader_HasStats()
    {
        // Seed a run so stats show something meaningful (not strictly needed — About renders
        // stats div even with 0 runs when HasGlobalRead is true, but let's be thorough)
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        var completed = started + TimeSpan.FromMinutes(1);
#pragma warning disable CS0618
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("ABOUT-READER-1"),
            IssueTitle = "Issue ABOUT-READER-1",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = started,
            StartedAt = started.DateTime,
            CompletedAtOffset = completed,
            CompletedAt = completed.DateTime,
        });
#pragma warning restore CS0618

        var browser = await Fixture.GetBrowserAsync();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions());
        var page = await context.NewPageAsync();

        await page.GotoCockpitPageAsync(E2ETestSignIn.SignInUrl(BaseUrl, E2ETestSignIn.ReaderUser));
        await page.GotoCockpitPageAsync($"{BaseUrl}/about");

        await Assertions.Expect(page.Locator("[data-testid='about-stats']")).ToBeVisibleAsync();
    }
}
