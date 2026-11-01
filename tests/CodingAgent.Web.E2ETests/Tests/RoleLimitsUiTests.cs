using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Verifies that role and project-scope limits are enforced in the cockpit UI.
/// Covers gaps not addressed by AccessControlTests: run-page action buttons, the Pipelines
/// table read-only state, the issue dispatch drawer, cross-project resource hiding, and the
/// project-not-found guard. Each test opens its own browser context without the shared admin
/// session, following the AccessControlTests.cs:16-38 pattern.
/// Issue identifiers reserved for this file: 9401–9404.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RoleLimitsUiTests : IAsyncLifetime
{
    private readonly E2EFixture _fixture;
    private IBrowserContext? _context;
    private IPage _page = null!;

    public RoleLimitsUiTests(E2EFixture fixture) => _fixture = fixture;

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

    // ── Sign-in helpers ────────────────────────────────────────────────────────

    private Task SignInAsReaderAsync() =>
        _page.GotoCockpitPageAsync(E2ETestSignIn.SignInUrl(BaseUrl, E2ETestSignIn.ReaderUser));

    private Task SignInAsAliceAsync() =>
        _page.GotoCockpitPageAsync(E2ETestSignIn.SignInUrl(BaseUrl, "alice", E2ETestSignIn.TeamGroup));

    private async Task OpenRunAsync(string runId)
    {
        await _page.GotoCockpitPageAsync($"{BaseUrl}/runs/{runId}");
        // RunPage.razor sets data-initialized='true' unconditionally in the finally block of
        // OnParametersSetAsync (_loading = false), so this selector fires for both found and
        // not-found runs (including 404 responses that set _run to null).
        await _page.WaitForSelectorAsync(
            "[data-testid='run-page'][data-initialized='true']",
            new() { Timeout = 15_000 });
    }

    /// <summary>
    /// Seeds a run summary in the history service and returns its run ID (a new GUID string).
    /// Both IssueProviderConfigId and RepoProviderConfigId are set, which is required for
    /// CanRedispatch to evaluate to true (RunPage.razor:975-979).
    /// </summary>
    private async Task<string> SeedRunAsync(string issue, PipelineStep step, string? projectId)
    {
        var runId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        await _fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier(issue),
            IssueTitle = "Role run",
            FinalStep = step,
            RunType = PipelineRunType.Implementation,
            ProjectId = projectId,
            StartedAtOffset = now.AddHours(-1),
#pragma warning disable CS0618
            StartedAt = now.AddHours(-1).DateTime,
#pragma warning restore CS0618
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            CompletedAtOffset = step.IsTerminal() ? now.AddMinutes(-50) : null
        });
        return runId;
    }

    // ── Run page — cancel button ───────────────────────────────────────────────

    [Fact]
    public async Task GlobalReader_LiveRun_HasNoCancelButton()
    {
        // Global reader can VIEW the run (readonly access) but cannot cancel it
        // (CanCancel = Access.CanOperate(projectId) = false for readonly).
        var runId = await SeedRunAsync("9401", PipelineStep.GeneratingCode, E2ETestSignIn.TeamProjectId);
        await SignInAsReaderAsync();
        await OpenRunAsync(runId);

        // Positive assertion first: confirm the progress card rendered
        await Assertions.Expect(_page.Locator(".cockpit-card:has(h2:has-text('Pipeline progress'))"))
            .ToBeVisibleAsync();
        await Assertions.Expect(_page.Locator("[data-testid='cancel-pipeline-btn']"))
            .ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ScopedOperator_LiveRunOfOwnProject_HasCancelButton()
    {
        // Control case: alice (operator on TeamProjectId) CAN cancel her own project's run.
        var runId = await SeedRunAsync("9401", PipelineStep.GeneratingCode, E2ETestSignIn.TeamProjectId);
        await SignInAsAliceAsync();
        await OpenRunAsync(runId);

        await Assertions.Expect(_page.Locator("[data-testid='cancel-pipeline-btn']"))
            .ToBeVisibleAsync();
    }

    // ── Run page — redispatch card ─────────────────────────────────────────────

    [Fact]
    public async Task GlobalReader_FailedRun_HasNoRedispatchCard()
    {
        // Global reader cannot redispatch (CanRedispatch requires CanOperate).
        var runId = await SeedRunAsync("9402", PipelineStep.Failed, E2ETestSignIn.TeamProjectId);
        await SignInAsReaderAsync();
        await OpenRunAsync(runId);

        // Positive assertion first: confirm the run page shows the correct run
        await Assertions.Expect(_page.Locator("h1")).ToContainTextAsync("#9402");
        await Assertions.Expect(_page.Locator("[data-testid='redispatch-card']"))
            .ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ScopedOperator_FailedRunOfOwnProject_HasRedispatchCard()
    {
        // Control case: alice (operator on TeamProjectId) CAN redispatch her own project's run.
        var runId = await SeedRunAsync("9402", PipelineStep.Failed, E2ETestSignIn.TeamProjectId);
        await SignInAsAliceAsync();
        await OpenRunAsync(runId);

        await Assertions.Expect(_page.Locator("[data-testid='redispatch-btn']"))
            .ToBeVisibleAsync();
    }

    // ── Pipelines table ────────────────────────────────────────────────────────

    [Fact]
    public async Task GlobalReader_PipelinesTable_IsReadOnly()
    {
        // TemplateTableSection.razor gates Actions column, btn-add, btn-delete, and
        // feature-config-row behind CanEdit (= Access.IsAdmin). Reader is not admin.
        await _fixture.ConfigStore.SaveTemplateAsync(
            WellKnownIds.DefaultProjectId,
            new PipelineJobTemplate
            {
                Id = "role-template",
                Name = "Role Repo",
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                Enabled = true
            },
            CancellationToken.None);

        await SignInAsReaderAsync();
        await _page.GotoCockpitPageAsync($"{BaseUrl}/pipelines");
        // ClickAsync auto-waits for the element to become actionable (covers template data load)
        await _page.ClickAsync("button.feature-badge-bar");

        // Positive assertion first: read-only toggle is present
        await Assertions.Expect(_page.Locator("[data-testid='template-enabled-readonly']"))
            .ToHaveTextAsync("On");
        await Assertions.Expect(_page.Locator(".template-table th:text-is('Actions')"))
            .ToHaveCountAsync(0);
        await Assertions.Expect(_page.Locator("button.btn-add"))
            .ToHaveCountAsync(0);
        await Assertions.Expect(_page.Locator(".feature-config-row"))
            .ToHaveCountAsync(0);
        await Assertions.Expect(_page.Locator("button.btn-delete"))
            .ToHaveCountAsync(0);
    }

    // ── Issue dispatch drawer ──────────────────────────────────────────────────

    [Fact]
    public async Task GlobalReader_IssueDrawer_HasNoDispatchButton()
    {
        // IssueDispatchDrawer.razor:133 guards dispatch-issue-btn behind CanDispatch.
        // For a global reader, CanDispatch = Access.CanOperate(...) = false.
        await _fixture.ConfigStore.SaveTemplateAsync(
            WellKnownIds.DefaultProjectId,
            new PipelineJobTemplate
            {
                Id = "role-template",
                Name = "Role Repo",
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                Enabled = true
            },
            CancellationToken.None);

        _fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9403",
            Title = "#9403",
            Labels = ["enhancement"],
            Description = ""
        });

        await SignInAsReaderAsync();
        await _page.GotoCockpitPageAsync($"{BaseUrl}/pipelines");

        // Do NOT call AgentCodingPage.NavigateAsync — it waits for button.btn-add (admin only)
        var codingPage = new AgentCodingPage(_page, BaseUrl);
        await codingPage.SelectTemplateAsync("Role Repo");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("9403");

        // Positive assertion first: the issue row is visible in the drawer
        await Assertions.Expect(_page.Locator("[data-testid='issue-row-9403']"))
            .ToBeVisibleAsync();
        await Assertions.Expect(_page.Locator("[data-testid='dispatch-issue-btn']"))
            .ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ScopedOperator_IssueDrawerOfOwnProject_HasDispatchButton()
    {
        // Control case: alice (operator on TeamProjectId) CAN dispatch issues in her project.
        await _fixture.ConfigStore.SaveTemplateAsync(
            E2ETestSignIn.TeamProjectId,
            new PipelineJobTemplate
            {
                Id = "role-template-team",
                Name = "Role Repo",
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                Enabled = true
            },
            CancellationToken.None);

        _fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9403",
            Title = "#9403",
            Labels = ["enhancement"],
            Description = ""
        });

        await SignInAsAliceAsync();
        await _page.GotoCockpitPageAsync($"{BaseUrl}/pipelines");

        var codingPage = new AgentCodingPage(_page, BaseUrl);
        await codingPage.SelectTemplateAsync("Role Repo");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("9403");

        await Assertions.Expect(_page.Locator("[data-testid='dispatch-issue-btn']"))
            .ToBeVisibleAsync();
    }

    // ── Cross-project resource hiding ──────────────────────────────────────────

    [Fact]
    public async Task ScopedOperator_RunOfOtherProject_LooksMissing()
    {
        // UiRunEndpoints.GetRun calls access.DemandRead(run.ProjectId). Alice has no read right
        // on DefaultProjectId, so the API returns 404 and RunPage shows "Run not found."
        var runId = await SeedRunAsync("9404", PipelineStep.Completed, WellKnownIds.DefaultProjectId);
        await SignInAsAliceAsync();

        // Set up the response listener BEFORE navigation to avoid racing the API call
        var api = _page.WaitForResponseAsync(r => r.Url.EndsWith($"/api/ui/runs/{runId}"));
        await OpenRunAsync(runId);

        Assert.Equal(404, (await api).Status);
        await Assertions.Expect(_page.Locator("[data-testid='run-page'] .cockpit-empty"))
            .ToHaveTextAsync("Run not found.");
        var pageText = await _page.TextContentAsync("body");
        Assert.DoesNotContain("Role run", pageText);
    }

    [Fact]
    public async Task ScopedOperator_TemplateOfOtherProject_LooksMissing()
    {
        // UiConfigEndpoints.cs:62-66 returns only readable templates; alice cannot read
        // DefaultProjectId, so RepositoryPageService.CanView() is false.
        await _fixture.ConfigStore.SaveTemplateAsync(
            WellKnownIds.DefaultProjectId,
            new PipelineJobTemplate
            {
                Id = "role-template-other",
                Name = "Other Repo",
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                Enabled = true
            },
            CancellationToken.None);

        await SignInAsAliceAsync();
        await _page.GotoCockpitPageAsync($"{BaseUrl}/pipelines/role-template-other");

        // Positive assertion first: confirm the not-found state (polls through loading)
        await Assertions.Expect(_page.Locator(".monitoring-empty"))
            .ToHaveTextAsync("Template not found.");
        var pageText = await _page.TextContentAsync("body");
        Assert.DoesNotContain("Other Repo", pageText);
    }

    // ── Project pages ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ScopedOperator_OtherProjectPage_LooksMissing()
    {
        // ProjectPage.razor:18-21: !Access.CanAdminister(ProjectId) → project-not-found.
        // Alice has no role on DefaultProjectId.
        await SignInAsAliceAsync();
        await _page.GotoCockpitPageAsync($"{BaseUrl}/projects/{WellKnownIds.DefaultProjectId}");

        await Assertions.Expect(_page.Locator("[data-testid='project-not-found']"))
            .ToHaveTextAsync("Project not found.");
    }

    [Fact]
    public async Task ScopedOperator_OwnProjectPage_LooksMissing_WithoutAdmin()
    {
        // Alice is operator (not admin) on TeamProjectId; ProjectPage requires CanAdminister.
        await SignInAsAliceAsync();
        await _page.GotoCockpitPageAsync($"{BaseUrl}/projects/{E2ETestSignIn.TeamProjectId}");

        await Assertions.Expect(_page.Locator("[data-testid='project-not-found']"))
            .ToBeVisibleAsync();
    }

    [Fact]
    public async Task ScopedAdmin_OtherProjectPage_LooksMissing()
    {
        // Dana is admin on TeamProjectId only.
        // Other project → not found; own project → project-description-input visible.
        await _page.GotoCockpitPageAsync(E2ETestSignIn.SignInUrl(BaseUrl, "dana", E2ETestSignIn.TeamAdminGroup));

        await _page.GotoCockpitPageAsync($"{BaseUrl}/projects/{WellKnownIds.DefaultProjectId}");
        await Assertions.Expect(_page.Locator("[data-testid='project-not-found']"))
            .ToBeVisibleAsync();

        await _page.GotoCockpitPageAsync($"{BaseUrl}/projects/{E2ETestSignIn.TeamProjectId}");
        await Assertions.Expect(_page.Locator("[data-testid='project-description-input']"))
            .ToBeVisibleAsync();
    }

    [Fact]
    public async Task ScopedOperator_FleetUrl_IsDenied()
    {
        // Fleet.razor has [Authorize(Policy = AccessPolicies.GlobalRead)].
        // Alice has no global role, so she gets the access-denied component.
        await SignInAsAliceAsync();
        await _page.GotoCockpitPageAsync($"{BaseUrl}/fleet");

        await Assertions.Expect(_page.Locator("[data-testid='access-denied']"))
            .ToBeVisibleAsync();
        await Assertions.Expect(_page.Locator("h1:text-is('Fleet')"))
            .ToHaveCountAsync(0);
    }

    [Fact]
    public async Task GlobalReader_UnknownProject_SaysNotFound()
    {
        // A global reader has no CanAdminister on any project. ProjectPage.razor:18-21 renders
        // project-not-found for any project ID the reader visits.
        // (A global admin gets the new-project form instead, hence using the reader here.)
        await SignInAsReaderAsync();
        await _page.GotoCockpitPageAsync($"{BaseUrl}/projects/{Guid.NewGuid()}");

        await Assertions.Expect(_page.Locator("[data-testid='project-not-found']"))
            .ToBeVisibleAsync();
    }
}
