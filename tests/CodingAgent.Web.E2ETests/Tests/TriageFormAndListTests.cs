using System.Net.Http.Json;
using System.Text.RegularExpressions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;
// Disambiguate: CodingAgent.Pipeline.Models also has a TriageListPage DTO record.
using TriageListPage = CodingAgent.Web.E2ETests.PageObjects.TriageListPage;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Browser E2E tests for the triage new-form (<c>/triage/new</c>) and the triage list
/// (<c>/triage</c>), exercising validation, preview, submission, filtering, paging and dismiss.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class TriageFormAndListTests : E2ETestBase
{
    public TriageFormAndListTests(E2EFixture fixture) : base(fixture) { }

    // ════════════════════════════════════════════════════════════════════
    // Form — required fields and CanStart guard
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenRequiredFieldsAreMissing_ShouldKeepStartDisabled()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);

        var form = new TriageNewPage(Page, BaseUrl);
        await form.NavigateAsync();
        await form.WaitForPreviewAsync();

        // Nothing filled: disabled
        await Assertions.Expect(form.StartButton).ToBeDisabledAsync();

        // Title only: disabled (_whatHappened and _expected are blank)
        await form.Title.FillAsync("Checkout fails");
        await Assertions.Expect(form.StartButton).ToBeDisabledAsync();

        // Title + expected, but NOT happened: disabled
        // This catches a mutation that drops the _whatHappened guard from CanStart;
        // the purely-additive sequence (title → title+happened → all three) would miss it.
        await form.Expected.FillAsync("No errors");
        await Assertions.Expect(form.StartButton).ToBeDisabledAsync();

        // Title + expected + happened: enabled (all three required fields now non-blank)
        await form.Happened.FillAsync("Saw 502 errors");
        await Assertions.Expect(form.StartButton).ToBeEnabledAsync();

        // Overwrite title with whitespace: disabled (CanStart uses IsNullOrWhiteSpace)
        await form.Title.FillAsync("   ");
        await Assertions.Expect(form.StartButton).ToBeDisabledAsync();
    }

    // ════════════════════════════════════════════════════════════════════
    // Form — preview content
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenPreviewLoads_ShouldNameTheRepositoryThatRunsTheJob()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);

        var form = new TriageNewPage(Page, BaseUrl);
        await form.NavigateAsync();
        await form.WaitForPreviewAsync();

        await Assertions.Expect(form.Preview).ToContainTextAsync(TriageTestData.TemplateName);
        await Assertions.Expect(form.Preview).ToContainTextAsync("runs the job");
    }

    // ════════════════════════════════════════════════════════════════════
    // Form — submission and landing page
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenFormIsSubmitted_ShouldLandOnTheTriagePage_WithTheRequest()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);

        var form = new TriageNewPage(Page, BaseUrl);
        await form.NavigateAsync();
        await form.WaitForPreviewAsync();

        const string happened = "One in five checkouts fails with 502";
        const string expected = "Checkout completes without error";

        await form.FillRequiredAsync("Checkout 502 after deploy", happened, expected);
        await form.SelectWhenAsync("Last hour");

        // Fill change-bound optional fields before clicking submit
        await form.Environment.FillAsync("staging");
        await form.Where.FillAsync("/pay");

        await form.StartButton.ClickAsync();

        // Wait for navigation to the detail page
        await Page.WaitForURLAsync(url => Regex.IsMatch(url, @"/triage/[0-9a-f\-]{36}$"),
            new PageWaitForURLOptions { Timeout = 15_000 });

        // Extract the triage id from the URL
        var triageId = Guid.Parse(new Uri(Page.Url).Segments[^1]);

        var detail = new TriageDetailPage(Page, BaseUrl);
        await detail.WaitForLoadedAsync();

        await Assertions.Expect(detail.Status).ToHaveTextAsync("Investigating");
        await Assertions.Expect(detail.Running).ToContainTextAsync("Attempt 1 is investigating");
        await Assertions.Expect(detail.Request).ToContainTextAsync(happened);
        await Assertions.Expect(detail.Request).ToContainTextAsync(expected);
        await Assertions.Expect(detail.Request).ToContainTextAsync("→ now (UTC)");
        await Assertions.Expect(detail.Request).ToContainTextAsync("staging");
        await Assertions.Expect(detail.Request).ToContainTextAsync("/pay");

        // Verify via the API
        using var api = Fixture.CreateUiApiClient();
        var apiDetail = await api.GetFromJsonAsync<TriageDetail>(
            $"/api/ui/triages/{triageId}", PipelineJsonOptions.Default);
        Assert.NotNull(apiDetail);
        Assert.Equal("admin", apiDetail.Record.RequestedBy);
        Assert.Equal(TriageSource.Operator, apiDetail.Record.Source);
        Assert.NotNull(apiDetail.Record.Request?.From);
        Assert.True(
            Math.Abs((apiDetail.Record.Request.From.Value - DateTimeOffset.UtcNow.AddHours(-1)).TotalMinutes) < 5,
            $"Request.From {apiDetail.Record.Request.From} is not within 5 minutes of UtcNow.AddHours(-1)");
        Assert.Null(apiDetail.Record.Request.Until);
    }

    [Fact]
    public async Task WhenCustomWhenIsChosen_ShouldShowTheRangeFields_AndSendTheStart()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);

        var form = new TriageNewPage(Page, BaseUrl);
        await form.NavigateAsync();
        await form.WaitForPreviewAsync();

        // Before selecting Custom: #triage-from is absent and Today preset has aria-pressed="true"
        await Assertions.Expect(form.From).ToHaveCountAsync(0);
        await Assertions.Expect(form.WhenPreset("Today")).ToHaveAttributeAsync("aria-pressed", "true");

        await form.SelectWhenAsync("Custom");

        // Both range fields should now be visible
        await Assertions.Expect(form.From).ToBeVisibleAsync();
        await Assertions.Expect(form.Until).ToBeVisibleAsync();

        // Fill the From field (datetime-local format)
        await form.From.FillAsync("2026-10-30T14:00");

        await form.FillRequiredAsync("Checkout 502", "Observed 502 errors", "Expected success");
        await form.StartButton.ClickAsync();

        await Page.WaitForURLAsync(url => Regex.IsMatch(url, @"/triage/[0-9a-f\-]{36}$"),
            new PageWaitForURLOptions { Timeout = 15_000 });

        var triageId = Guid.Parse(new Uri(Page.Url).Segments[^1]);

        using var api = Fixture.CreateUiApiClient();
        var apiDetail = await api.GetFromJsonAsync<TriageDetail>(
            $"/api/ui/triages/{triageId}", PipelineJsonOptions.Default);
        Assert.NotNull(apiDetail?.Record.Request?.From);
        Assert.Null(apiDetail!.Record.Request!.Until);

        // Verify the specific date was transmitted: a non-null From alone cannot distinguish
        // "Custom was selected and the date sent" from "Custom was ignored and Today's start sent."
        // The browser fills "2026-10-30T14:00" local; LocalToUtcAsync converts it, but year/month/day
        // are preserved for any timezone offset within ±13 h of UTC.
        var from = apiDetail.Record.Request.From!.Value.ToUniversalTime();
        Assert.Equal(2026, from.Year);
        Assert.Equal(10, from.Month);
        Assert.Equal(30, from.Day);
    }

    // ════════════════════════════════════════════════════════════════════
    // Form — preview hints (no template / no route / similar / no project)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenTheProjectHasNoTemplate_ShouldShowTheProblem_AndKeepStartDisabled()
    {
        // ResetAllAsync cleared templates; do not seed any

        var form = new TriageNewPage(Page, BaseUrl);
        await form.NavigateAsync();
        await form.WaitForPreviewAsync();

        await Assertions.Expect(form.PreviewProblem).ToContainTextAsync(
            "The project has no enabled template, so no agent can run a triage.");

        await form.FillRequiredAsync("My title", "Happened", "Expected");
        await Assertions.Expect(form.StartButton).ToBeDisabledAsync();
    }

    [Fact]
    public async Task WhenNoRouteResolves_ShouldShowTheRoutingProblem_AndKeepStartDisabled()
    {
        // Save the template but do NOT seed a route
        await Fixture.ConfigStore.SaveTemplateAsync(
            (ProjectId)WellKnownIds.DefaultProjectId,
            new PipelineJobTemplate
            {
                Id = TriageTestData.TemplateId,
                Name = TriageTestData.TemplateName,
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                Enabled = true,
            },
            CancellationToken.None);

        var form = new TriageNewPage(Page, BaseUrl);
        await form.NavigateAsync();
        await form.WaitForPreviewAsync();

        await Assertions.Expect(form.PreviewProblem).ToContainTextAsync(
            "No agent route: set the default route in Settings \u2192 Agents \u2192 Routing");

        await form.FillRequiredAsync("My title", "Happened", "Expected");
        await Assertions.Expect(form.StartButton).ToBeDisabledAsync();
    }

    [Fact]
    public async Task WhenATitleSharesWords_ShouldListTheSimilarTriage()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);

        using var api = Fixture.CreateUiApiClient();
        await TriageTestData.StartOperatorTriageAsync(api, "Checkout returns 502 after deploy");

        var form = new TriageNewPage(Page, BaseUrl);
        await form.NavigateAsync();
        await form.WaitForPreviewAsync();

        await form.Title.FillAsync("Checkout 502 again");
        // Blur fires LoadSimilarAsync
        await form.Title.PressAsync("Tab");

        await Assertions.Expect(form.Similar).ToContainTextAsync("Checkout returns 502 after deploy");
        await Assertions.Expect(form.Similar).ToContainTextAsync("Matched on title words");
    }

    [Fact]
    public async Task WhenTheOperatorsOnlyProjectIsDisabled_ShouldShowTheNoProjectState()
    {
        // Disable the E2E Team project (alice's only operator project)
        var teamProject = await Fixture.ConfigStore.GetProjectByIdAsync(
            (ProjectId)E2ETestSignIn.TeamProjectId, CancellationToken.None);
        Assert.NotNull(teamProject);
        await Fixture.ConfigStore.SaveProjectAsync(
            teamProject with { Enabled = false }, CancellationToken.None);

        // Open a fresh browser context for alice (not the shared admin context)
        var browser = await Fixture.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        // Sign alice in via the test sign-in endpoint.
        // E2ETestSignIn.SignInAsync wraps the raw GotoAsync so this file does not call it directly
        // (the architecture guard LayerBoundaryTests.E2ENavigation forbids raw GotoAsync in Tests/).
        await E2ETestSignIn.SignInAsync(page, BaseUrl, "alice", E2ETestSignIn.TeamGroup);

        var form = new TriageNewPage(page, BaseUrl);
        await form.NavigateAsync();

        await Assertions.Expect(form.NoProject).ToContainTextAsync(
            "You can start triages only in projects where you are an operator.");
    }

    // ════════════════════════════════════════════════════════════════════
    // List — tabs and counts
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenTriagesHaveDifferentStates_ShouldSortThemIntoTabs()
    {
        // Seed records directly for the three terminal/ready states
        await TriageTestData.SeedRecordAsync(Fixture, "Review me", TriageState.NeedsReview,
            TriageTestData.CauseFound());
        await TriageTestData.SeedRecordAsync(Fixture, "Answer me", TriageState.NeedsInput,
            new TriageResult
            {
                Verdict = TriageVerdict.Inconclusive,
                Confidence = TriageConfidence.Low,
                Summary = "Could not determine root cause.",
                Investigated = [],
            });
        await TriageTestData.SeedRecordAsync(Fixture, "Gone", TriageState.Dismissed,
            TriageTestData.CauseFound());

        // A live, investigating triage via the API (has an active WorkItem)
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        using var api = Fixture.CreateUiApiClient();
        await TriageTestData.StartOperatorTriageAsync(api, "Running now");

        var list = new TriageListPage(Page, BaseUrl);
        await list.NavigateAsync();

        // All tab: 4 rows
        await Assertions.Expect(list.Rows).ToHaveCountAsync(4);

        // Need you tab
        await list.ClickTabAsync("need-you");
        await Assertions.Expect(list.Tab("need-you")).ToContainTextAsync("Need you \u00b7 2");
        await Assertions.Expect(list.Rows).ToHaveCountAsync(2);
        await Assertions.Expect(list.Rows.Filter(new LocatorFilterOptions { HasText = "Review me" })).ToHaveCountAsync(1);
        await Assertions.Expect(list.Rows.Filter(new LocatorFilterOptions { HasText = "Answer me" })).ToHaveCountAsync(1);

        // Investigating tab
        await list.ClickTabAsync("investigating");
        await Assertions.Expect(list.Tab("investigating")).ToContainTextAsync("Investigating \u00b7 1");
        await Assertions.Expect(list.Rows).ToHaveCountAsync(1);
        await Assertions.Expect(list.Rows.First).ToContainTextAsync("Running now");

        // Done tab
        await list.ClickTabAsync("done");
        await Assertions.Expect(list.Rows).ToHaveCountAsync(1);
        await Assertions.Expect(list.Rows.First).ToContainTextAsync("Gone");
    }

    // ════════════════════════════════════════════════════════════════════
    // List — source filter and search
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenSourceAndSearchAreSet_ShouldFilterTheRows()
    {
        await TriageTestData.SeedRecordAsync(Fixture, "Payment timeout", TriageState.NeedsReview,
            TriageTestData.CauseFound(), source: TriageSource.Operator);
        await TriageTestData.SeedRecordAsync(Fixture, "Login broken", TriageState.NeedsReview,
            TriageTestData.CauseFound(), source: TriageSource.Issue);

        var list = new TriageListPage(Page, BaseUrl);
        await list.NavigateAsync();

        // Filter by Tracker issue
        await list.ClickSourceAsync("Tracker issue");
        await Assertions.Expect(list.Rows).ToHaveCountAsync(1);
        await Assertions.Expect(list.Rows.First).ToContainTextAsync("Login broken");

        // Filter by Operator
        await list.ClickSourceAsync("Operator");
        await Assertions.Expect(list.Rows).ToHaveCountAsync(1);
        await Assertions.Expect(list.Rows.First).ToContainTextAsync("Payment timeout");

        // Reset to All sources
        await list.ClickSourceAsync("All sources");
        await Assertions.Expect(list.Rows).ToHaveCountAsync(2);

        // Search — case-insensitive contains
        await list.SearchAsync("payment");
        await Assertions.Expect(list.Rows).ToHaveCountAsync(1);
        await Assertions.Expect(list.Rows.First).ToContainTextAsync("Payment timeout");

        // Search with no match → empty state
        await list.SearchAsync("nothing-matches");
        await Assertions.Expect(list.Rows).ToHaveCountAsync(0);
        await Assertions.Expect(list.Empty).ToContainTextAsync("No triages match the current filters.");
    }

    // ════════════════════════════════════════════════════════════════════
    // List — empty state (no triages at all)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenThereAreNoTriages_ShouldShowTheEmptyState()
    {
        var list = new TriageListPage(Page, BaseUrl);
        await list.NavigateAsync();

        await Assertions.Expect(list.Empty).ToContainTextAsync(
            "No triages yet. Start one when something behaves differently than expected.");
    }

    // ════════════════════════════════════════════════════════════════════
    // List — pagination
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenThereAreMoreThanAPage_ShouldPage()
    {
        // Seed 26 records (page size is 25 per Triage.razor)
        for (var i = 0; i < 26; i++)
        {
            await TriageTestData.SeedRecordAsync(Fixture, $"Record {i:D2}",
                TriageState.NeedsReview, TriageTestData.CauseFound());
        }

        var list = new TriageListPage(Page, BaseUrl);
        await list.NavigateAsync();

        // Page 1: 25 rows
        await Assertions.Expect(list.Rows).ToHaveCountAsync(25);
        await Assertions.Expect(list.PagerInfo).ToContainTextAsync("1\u201325 of 26");
        await Assertions.Expect(list.PrevButton).ToBeDisabledAsync();

        // Navigate to page 2
        await list.NextPageAsync();
        await Assertions.Expect(list.Rows).ToHaveCountAsync(1);
        await Assertions.Expect(list.PagerInfo).ToContainTextAsync("26\u201326 of 26");
        await Assertions.Expect(list.NextButton).ToBeDisabledAsync();

        // Navigate back to page 1
        await list.PrevPageAsync();
        await Assertions.Expect(list.Rows).ToHaveCountAsync(25);
    }

    // ════════════════════════════════════════════════════════════════════
    // List — error state
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenTheListFailsToLoad_ShouldShowTheError()
    {
        // Intercept the list API call and return 500 before navigating
        await Page.RouteAsync("**/api/ui/triages?*",
            r => r.FulfillAsync(new RouteFulfillOptions { Status = 500 }));

        var list = new TriageListPage(Page, BaseUrl);
        await list.NavigateAsync();

        await Assertions.Expect(Page.Locator(".cockpit-card .cockpit-empty"))
                        .ToContainTextAsync("Failed to load triages:");
        await Assertions.Expect(Page.Locator(".cockpit-card .cockpit-empty"))
                        .ToContainTextAsync("500");
    }

    // ════════════════════════════════════════════════════════════════════
    // List — dismiss flow
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenATriageIsDismissed_ShouldShowTheReason_AndMoveItToDone()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);

        await using var agent = new FakeAgentClient("agent-triage-list", TriageTestData.AgentLabel);
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        using var api = Fixture.CreateUiApiClient();
        var triage = await TriageTestData.StartOperatorTriageAsync(api, "Login fails with 503");
        var id = triage.Record.Id;

        await TriageTestData.ReportResultAsync(agent, TriageTestData.CauseFound(
            TriageTestData.Draft("d1", "Fix it")));
        await TriageTestData.WaitForStatusAsync(api, id, TriageStatus.NeedsReview);

        // Open the detail page and dismiss the triage
        var detail = new TriageDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(id);
        await detail.WaitForLoadedAsync();
        await detail.DismissAsync("Known, tracked elsewhere");

        // Status changes to Dismissed
        await Assertions.Expect(detail.Status).ToHaveTextAsync("Dismissed");

        // Dismissed banner shows author and reason
        await Assertions.Expect(detail.Dismissed).ToContainTextAsync("Dismissed by admin");
        await Assertions.Expect(detail.Dismissed).ToContainTextAsync("Known, tracked elsewhere");
        await Assertions.Expect(detail.Dismissed).ToContainTextAsync("It can still be re-run.");

        // Dismiss button hidden after dismissal (status != Dismissed guard in TriagePage.razor)
        await Assertions.Expect(detail.DismissOpen).ToHaveCountAsync(0);

        // Re-run button still visible
        await Assertions.Expect(detail.RerunOpen).ToBeVisibleAsync();

        // On the list the Done tab shows 1 row
        var list = new TriageListPage(Page, BaseUrl);
        await list.NavigateAsync();
        await list.ClickTabAsync("done");
        await Assertions.Expect(list.Rows).ToHaveCountAsync(1);
    }
}
