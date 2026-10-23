using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Spec 049 Req 12.5: sign-in through the real login form, and what principals other than the
/// local admin see. Each test opens its own browser context without the shared admin session.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "IAM")]
[Collection(E2ECollection.Name)]
public sealed partial class AccessControlTests : IAsyncLifetime
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
        await Assertions.Expect(options.First).ToHaveTextAsync("E2E Team");
        await Assertions.Expect(_page.Locator(".cockpit-nav-link[aria-label='Fleet']")).ToHaveCountAsync(0);
        await Assertions.Expect(_page.Locator(".cockpit-nav-link[aria-label='Agent Chat']")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task ScopedOperator_ProfileNamesItsProject_ThroughTheApiAsThatUser()
    {
        // Spec 051: the web host signs a user token, the real API checks it on /api/ui/me and
        // resolves the bound project ID to its name. A rejected token would leave IDs only.
        await _page.GotoAsync(E2ETestSignIn.SignInUrl(BaseUrl, "alice", E2ETestSignIn.TeamGroup));
        await _page.WaitForURLAsync(url => url.EndsWith("/user"));

        await Assertions.Expect(_page.Locator("[data-testid=profile-bindings]"))
            .ToContainTextAsync($"Project E2E Team ({E2ETestSignIn.TeamProjectId})");
        await Assertions.Expect(_page.Locator("[data-testid=profile-projects-unavailable]")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ProjectAdmin_SavesTheProjectAndARepositorysBehaviour_ThroughTheApiAsThatUser()
    {
        // #3577: the web host signs the project admin role into the user token, and the real API takes
        // the project page's and the Behaviour card's saves from it.
        var templateId = Guid.NewGuid().ToString();
        await _fixture.ConfigStore.SaveTemplateAsync(E2ETestSignIn.TeamProjectId, new PipelineJobTemplate
        {
            Id = templateId,
            Name = "Team repo",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
        }, CancellationToken.None);
        await _page.GotoAsync(E2ETestSignIn.SignInUrl(BaseUrl, "dana", E2ETestSignIn.TeamAdminGroup));

        await _page.GotoAsync($"{BaseUrl}/projects/{E2ETestSignIn.TeamProjectId}");
        var description = _page.Locator(".settings-section textarea").First;
        await description.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        await description.FillAsync("Owned by the team");
        await _page.ClickAsync("button:has-text('Save Overview')");
        await Assertions.Expect(_page.Locator(".inline-status")).ToContainTextAsync("Project overview saved.");

        var repository = new RepositoryPageObject(_page, BaseUrl);
        await repository.NavigateAsync(templateId);
        await repository.SetSteeringContentAsync("Team steering");
        await repository.SaveBehaviourAsync();
        Assert.Contains("Behaviour saved", await repository.GetBehaviourSuccessMessageAsync());

        var project = await _fixture.ConfigStore.GetProjectByIdAsync(E2ETestSignIn.TeamProjectId, CancellationToken.None);
        Assert.Equal("Owned by the team", project!.Description);
        var template = (await _fixture.ConfigStore.LoadTemplatesForProjectAsync(E2ETestSignIn.TeamProjectId, CancellationToken.None))
            .Single(t => t.Id == templateId);
        Assert.Equal("Team steering", template.SteeringContent);
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

    /// <summary>
    /// Spec 052 Req 3.3, 9.5: when the session is gone (here: the cookie deleted, as after a logout in
    /// another browser), the next call through the BFF gets 401, the client confirms with the session
    /// endpoint and does a full navigation to the login page, with the page it was on as the return URL.
    /// </summary>
    [Fact]
    public async Task DeletedSession_LeadsToTheLoginPage_OnTheNextCall()
    {
        await _page.GotoAsync(E2ETestSignIn.SignInUrl(BaseUrl, E2ETestSignIn.ReaderUser));
        await _page.GotoCockpitPageAsync($"{BaseUrl}/overview");

        await _context!.ClearCookiesAsync();
        await _page.Locator("a.cockpit-nav-link[href='runs']").First.ClickAsync();

        await _page.WaitForURLAsync(LoginRedirect(), new() { Timeout = 15_000 });
        Assert.Contains("returnUrl=%2Fruns", _page.Url);
    }


    [GeneratedRegex(@"/login\?returnUrl=")]
    private static partial Regex LoginRedirect();
}
