using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Spec 049 Req 12.5: sign-in through the real login form, and what principals other than the
/// local admin see. Each test opens its own browser context without the shared admin session.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "IAM")]
[Collection(E2ECollection.Name)]
public sealed class AccessControlTests : IAsyncLifetime
{
    private readonly E2EFixture _fixture;
    private IBrowserContext? _context;
    private IPage _page = null!;

    public AccessControlTests(E2EFixture fixture) => _fixture = fixture;

    private string BaseUrl => _fixture.ServerAddress;

    public async Task InitializeAsync()
    {
        await _fixture.ResetAllAsync();
        var browser = await _fixture.GetBrowserAsync();
        _context = await browser.NewContextAsync();
        await E2ETestBase.StubExternalFontsAsync(_context);
        _page = await _context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        if (_context is not null)
            await _context.DisposeAsync();
    }

    [Fact]
    public async Task Anonymous_IsSentToLogin_SignsIn_LandsOnRequestedPage_AndLogsOut()
    {
        await _page.GotoAsync($"{BaseUrl}/runs");
        await _page.WaitForURLAsync(url => url.Contains("/login"));
        Assert.Contains("returnUrl=%2Fruns", _page.Url);

        await _page.FillAsync("[data-testid=login-password]", E2EWebApplicationFactory.TestAdminPassword);
        await _page.ClickAsync("[data-testid=login-submit]");
        await _page.WaitForURLAsync(url => url.EndsWith("/runs"));

        await Assertions.Expect(_page.Locator("[data-testid=user-menu-name]")).ToHaveTextAsync("admin");
        await _page.ClickAsync("[data-testid=user-menu] summary");
        await _page.ClickAsync("[data-testid=logout-button]");
        await _page.WaitForURLAsync(url => url.Contains("/login"));

        await _page.GotoAsync($"{BaseUrl}/overview");
        await _page.WaitForURLAsync(url => url.Contains("/login"));
    }

    [Fact]
    public async Task WrongPassword_ShowsGenericError()
    {
        await _page.GotoAsync($"{BaseUrl}/login");
        await _page.FillAsync("[data-testid=login-password]", "not-the-password");
        await _page.ClickAsync("[data-testid=login-submit]");

        await Assertions.Expect(_page.Locator("[data-testid=login-error]")).ToHaveTextAsync("Invalid username or password");
    }

    [Fact]
    public async Task GlobalReader_SeesNoSettings_AndGetsAccessDeniedThere()
    {
        await _page.GotoAsync(E2ETestSignIn.SignInUrl(BaseUrl, E2ETestSignIn.ReaderUser));
        await _page.GotoAsync($"{BaseUrl}/overview");
        await Assertions.Expect(_page.Locator("[data-testid=user-menu-name]")).ToHaveTextAsync(E2ETestSignIn.ReaderUser);

        await Assertions.Expect(_page.Locator(".cockpit-nav-link[aria-label='Settings']")).ToHaveCountAsync(0);
        await Assertions.Expect(_page.Locator(".cockpit-nav-link[aria-label='Fleet']")).ToHaveCountAsync(1);

        await _page.GotoAsync($"{BaseUrl}/settings");
        await Assertions.Expect(_page.Locator("[data-testid=access-denied]")).ToContainTextAsync("Access denied");
    }

    [Fact]
    public async Task GlobalReader_GetsNoWorkActions()
    {
        var pendingId = await SeedPendingItemAsync("3201");
        await _page.GotoAsync(E2ETestSignIn.SignInUrl(BaseUrl, E2ETestSignIn.ReaderUser));
        await _page.GotoAsync($"{BaseUrl}/work");

        await Assertions.Expect(_page.Locator($"[data-testid='priority-readonly-{pendingId}']")).ToBeVisibleAsync();
        await Assertions.Expect(_page.Locator("input.priority-input")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ScopedOperator_SeesOnlyItsProject_AndNoAllProjectsOption()
    {
        await _page.GotoAsync(E2ETestSignIn.SignInUrl(BaseUrl, "alice", E2ETestSignIn.TeamGroup));
        await _page.GotoAsync($"{BaseUrl}/overview");

        var options = _page.Locator(".cockpit-project-switcher option");
        await Assertions.Expect(options).ToHaveCountAsync(1);
        await Assertions.Expect(options.First).ToHaveTextAsync("Default");
        await Assertions.Expect(_page.Locator(".cockpit-nav-link[aria-label='Fleet']")).ToHaveCountAsync(0);
        await Assertions.Expect(_page.Locator(".cockpit-nav-link[aria-label='Agent Chat']")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task NoAccessUser_SeesItsGroupsOnTheProfile()
    {
        await _page.GotoAsync(E2ETestSignIn.SignInUrl(BaseUrl, "carol", "unbound-group"));

        await _page.WaitForURLAsync(url => url.EndsWith("/user"));
        await Assertions.Expect(_page.Locator("[data-testid=profile-groups]")).ToContainTextAsync("unbound-group");

        await _page.GotoAsync($"{BaseUrl}/overview");
        await Assertions.Expect(_page.Locator("[data-testid=access-denied]")).ToContainTextAsync("You have no access yet");
    }

    /// <summary>Seeds one pending work item of the "Default" project into the database.</summary>
    private async Task<Guid> SeedPendingItemAsync(string issueIdentifier)
    {
        var id = Guid.NewGuid();
        await using var db = _fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new CodingAgent.Infrastructure.Persistence.Entities.WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Pending,
            Payload = "{}",
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            TimeoutSeconds = 3600,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
        });
        await db.SaveChangesAsync();
        return id;
    }
}
