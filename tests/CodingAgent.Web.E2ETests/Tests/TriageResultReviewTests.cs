using System.Net;
using System.Net.Http.Json;
using CodingAgent.Contracts.Auth;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;
// Disambiguate: CodingAgent.Pipeline.Models also has a TriageListPage DTO record.
using TriageListPage = CodingAgent.Web.E2ETests.PageObjects.TriageListPage;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Browser E2E tests for reviewing a triage result on <c>/triage/{id}</c>: the rendered report, editing
/// and resetting a draft, selecting and queueing drafts, a create where one draft fails, a repeated
/// create, and what a reader or an operator of another project sees.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class TriageResultReviewTests : E2ETestBase
{
    public TriageResultReviewTests(E2EFixture fixture) : base(fixture) { }

    // ════════════════════════════════════════════════════════════════════
    // Result and drafts
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenAResultIsReported_ShouldShowVerdictEvidenceAndDrafts()
    {
        var result = TriageTestData.CauseFound(
            TriageTestData.Draft("d1", "Fix the retry"),
            TriageTestData.Draft("d2", "Add an alert"));
        var (id, api) = await StartWithResultAsync(result.Drafts.ToArray());
        using var _ = api;

        var page = new TriageDetailPage(Page, BaseUrl);
        await page.NavigateAsync(id);
        await page.WaitForLoadedAsync();

        await Assertions.Expect(page.Status).ToHaveTextAsync("RCA ready");
        await Assertions.Expect(page.VerdictPill).ToHaveTextAsync("Cause found");
        await Assertions.Expect(page.Verdict).ToContainTextAsync(result.Summary);
        await Assertions.Expect(page.Investigated).ToContainTextAsync(result.Investigated[0].Check);
        await Assertions.Expect(page.Evidence).ToContainTextAsync(result.Evidence[0].Claim);
        await Assertions.Expect(page.Draft).ToHaveCountAsync(2);
        await Assertions.Expect(page.Create).ToHaveTextAsync("Create 2 issues");
    }

    [Fact]
    public async Task WhenADraftIsEdited_ShouldShowTheEdit_AndResetRestoresTheAgentsVersion()
    {
        var (id, api) = await StartWithResultAsync(
            TriageTestData.Draft("d1", "Original title", "Original body"));
        using var _ = api;

        var page = new TriageDetailPage(Page, BaseUrl);
        await page.NavigateAsync(id);
        await page.WaitForLoadedAsync();

        // ── Act: edit and save ───────────────────────────────────────────
        await page.DraftEdit.ClickAsync();
        await page.DraftTitle.FillAsync("Edited title");
        await page.DraftBody.FillAsync("Edited body");
        await page.DraftSave.ClickAsync();

        // ── Assert: the drawer closed and the draft shows the edit ───────
        await Assertions.Expect(page.DraftEditor).ToHaveCountAsync(0);
        await Assertions.Expect(page.Draft).ToContainTextAsync("Edited title");
        await Assertions.Expect(page.Draft).ToContainTextAsync("Edited body");
        await Assertions.Expect(page.Draft).ToContainTextAsync("edited by admin");

        var edited = Assert.Single((await GetDetailAsync(api, id)).Record.Drafts);
        Assert.Equal("Edited title", edited.Current.Title);
        Assert.Equal("Original title", edited.Original.Title);
        Assert.Equal("admin", edited.EditedBy);

        // ── Act: reset to the agent's version ────────────────────────────
        await page.DraftEdit.ClickAsync();
        await page.DraftReset.ClickAsync();

        // ── Assert: the form holds the original again; closing shows it too ─
        await Assertions.Expect(page.DraftTitle).ToHaveValueAsync("Original title");
        await page.DraftCancel.ClickAsync();
        await Assertions.Expect(page.DraftEditor).ToHaveCountAsync(0);
        await Assertions.Expect(page.Draft).ToContainTextAsync("Original title");
        await Assertions.Expect(page.Draft).Not.ToContainTextAsync("edited by");

        var reset = Assert.Single((await GetDetailAsync(api, id)).Record.Drafts);
        Assert.False(reset.Edited);
        Assert.Equal("Original title", reset.Current.Title);
    }

    [Fact]
    public async Task WhenOneDraftIsEditedSelectedAndQueued_ShouldCreateExactlyThatIssue()
    {
        var (id, api) = await StartWithResultAsync(
            TriageTestData.Draft("d1", "Fix the retry", "Remove the retry."),
            TriageTestData.Draft("d2", "Add an alert"));
        using var _ = api;

        var page = new TriageDetailPage(Page, BaseUrl);
        await page.NavigateAsync(id);
        await page.WaitForLoadedAsync();

        // ── Act: edit the first draft, drop the second, queue, create ─────
        await page.DraftEdit.First.ClickAsync();
        await page.DraftTitle.FillAsync("Fix the retry policy");
        await page.DraftSave.ClickAsync();
        await Assertions.Expect(page.DraftEditor).ToHaveCountAsync(0);

        await page.DraftSelect.Nth(1).UncheckAsync();
        await page.Queue.CheckAsync();
        await Assertions.Expect(page.Create).ToHaveTextAsync("Create 1 issue");
        await page.Create.ClickAsync();

        // ── Assert: the page shows the one created issue ──────────────────
        await Assertions.Expect(page.Notice).ToHaveTextAsync("Created 1 issue.");
        await Assertions.Expect(page.Status).ToHaveTextAsync("Issues created");
        await Assertions.Expect(page.CreatedLink).ToHaveCountAsync(1);
        await Assertions.Expect(page.CreatedIssues).ToContainTextAsync($"{TriageTestData.TemplateName} #");

        // ── Assert: the tracker got exactly the edited, queued issue ──────
        var issue = Assert.Single(Fixture.IssueProvider.CreatedIssues);
        Assert.Equal("Fix the retry policy", issue.Title);
        Assert.Contains("Remove the retry.", issue.Body);
        Assert.Contains("(attempt 1) and approved in Coding Agent.", issue.Body);
        Assert.Contains(AgentLabels.Generated, issue.Labels!);
        Assert.Contains(AgentLabels.Next, issue.Labels!);
    }

    // ════════════════════════════════════════════════════════════════════
    // Partial failure and repeated create
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenOneCreateFails_ShouldShowThePartialResult_AndRetryOnlyTheRemainingDraft()
    {
        var (id, api) = await StartWithResultAsync(
            TriageTestData.Draft("d1", "First fix"),
            TriageTestData.Draft("d2", "Second fix"));
        using var _ = api;

        var page = new TriageDetailPage(Page, BaseUrl);
        await page.NavigateAsync(id);
        await page.WaitForLoadedAsync();

        // ── Act: the tracker refuses the next create only (the first draft's) ─
        Fixture.IssueProvider.Faults.FailNext(nameof(InMemoryIssueProvider.CreateIssueAsync));
        await page.Create.ClickAsync();

        // ── Assert: partial result on the page, in the tracker and in the record ─
        await Assertions.Expect(page.Notice)
            .ToContainTextAsync("Created 1 issue; 1 failed: The issue could not be created:");
        var firstRound = Assert.Single(Fixture.IssueProvider.CreatedIssues);
        Assert.Equal("Second fix", firstRound.Title);
        await Assertions.Expect(page.Draft).ToHaveCountAsync(2);
        await Assertions.Expect(page.CreatedLink).ToHaveCountAsync(1);

        var recorded = Assert.Single((await GetDetailAsync(api, id)).Record.CreatedIssues);
        Assert.Equal("a1-d2", recorded.DraftId);

        // ── Act: retry; only the draft without an issue is left ───────────
        await Assertions.Expect(page.Create).ToHaveTextAsync("Create 1 issue");
        await page.Create.ClickAsync();

        // ── Assert: each draft has exactly one issue, unqueued ────────────
        await Assertions.Expect(page.Notice).ToHaveTextAsync("Created 1 issue.");
        var titles = Fixture.IssueProvider.CreatedIssues.Select(i => i.Title).ToList();
        Assert.Equal(new[] { "Second fix", "First fix" }, titles);
        Assert.All(Fixture.IssueProvider.CreatedIssues,
            i => Assert.Equal(new[] { AgentLabels.Generated }, i.Labels!));
    }

    [Fact]
    public async Task WhenCreateIsRepeatedForRecordedDrafts_ShouldNotCreateAgain()
    {
        var (id, api) = await StartWithResultAsync(TriageTestData.Draft("d1", "Fix it"));
        using var _ = api;
        var request = new CreateTriageIssuesRequest { DraftIds = ["a1-d1"] };

        var first = await api.PostAsJsonAsync(
            $"/api/ui/triages/{id}/create-issues", request, PipelineJsonOptions.Default);
        var second = await api.PostAsJsonAsync(
            $"/api/ui/triages/{id}/create-issues", request, PipelineJsonOptions.Default);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var result = (await second.Content.ReadFromJsonAsync<CreateTriageIssuesResult>(PipelineJsonOptions.Default))!;
        Assert.Single(result.Created);
        Assert.Empty(result.Errors);
        Assert.Single(Fixture.IssueProvider.CreatedIssues);
        Assert.Single((await GetDetailAsync(api, id)).Record.CreatedIssues);
    }

    // ════════════════════════════════════════════════════════════════════
    // Roles
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenAGlobalReaderOpensATriage_ShouldShowNoActions()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        var id = await TriageTestData.SeedRecordAsync(
            Fixture, "Reader sees this", TriageState.NeedsReview,
            TriageTestData.CauseFound(TriageTestData.Draft("d1", "Fix it")));

        var browser = await Fixture.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var readerPage = await context.NewPageAsync();
        await E2ETestSignIn.SignInAsync(readerPage, BaseUrl, E2ETestSignIn.ReaderUser);

        var list = new TriageListPage(readerPage, BaseUrl);
        await list.NavigateAsync();
        await Assertions.Expect(list.Rows.Filter(new LocatorFilterOptions { HasText = "Reader sees this" }))
            .ToBeVisibleAsync();
        await Assertions.Expect(list.NewTriageLink).ToHaveCountAsync(0);

        var detail = new TriageDetailPage(readerPage, BaseUrl);
        await detail.NavigateAsync(id);
        await detail.WaitForLoadedAsync();
        await Assertions.Expect(detail.Verdict).ToBeVisibleAsync();
        await Assertions.Expect(detail.Draft).ToHaveCountAsync(1);
        await Assertions.Expect(detail.Actions).ToHaveCountAsync(0);
        await Assertions.Expect(detail.DraftSelect).ToHaveCountAsync(0);
        await Assertions.Expect(detail.DraftEdit).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAGlobalReaderOpensAnInconclusiveTriage_ShouldShowQuestionsReadOnly()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        var id = await TriageTestData.SeedRecordAsync(
            Fixture, "Which environment", TriageState.NeedsInput,
            new TriageResult
            {
                Verdict = TriageVerdict.Inconclusive,
                Summary = "Cannot reproduce without knowing the environment",
                Questions = [new TriageQuestion { Question = "Which environment?" }],
            });

        var browser = await Fixture.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var readerPage = await context.NewPageAsync();
        await E2ETestSignIn.SignInAsync(readerPage, BaseUrl, E2ETestSignIn.ReaderUser);

        var detail = new TriageDetailPage(readerPage, BaseUrl);
        await detail.NavigateAsync(id);
        await detail.WaitForLoadedAsync();

        await Assertions.Expect(detail.ReportQuestions).ToContainTextAsync("Which environment?");
        await Assertions.Expect(detail.Questions).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAReaderCallsAnAction_ShouldBeRefused()
    {
        var id = await TriageTestData.SeedRecordAsync(
            Fixture, "Reader cannot dismiss", TriageState.NeedsReview,
            TriageTestData.CauseFound(TriageTestData.Draft("d1", "Fix it")));
        using var readerApi = Fixture.CreateUiApiClient(
            new AccessGrant(AccessRole.ReadOnly, new Dictionary<string, AccessRole>(), []));

        var response = await readerApi.PostAsJsonAsync(
            $"/api/ui/triages/{id}/dismiss", new DismissTriageRequest(), PipelineJsonOptions.Default);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var adminApi = Fixture.CreateUiApiClient();
        Assert.Equal(TriageState.NeedsReview, (await GetDetailAsync(adminApi, id)).Record.State);
    }

    [Fact]
    public async Task WhenAScopedOperatorOpensAnotherProjectsTriage_ShouldSeeNotFound()
    {
        var otherId = await TriageTestData.SeedRecordAsync(Fixture, "Other project", TriageState.New);
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture, E2ETestSignIn.TeamProjectId);
        var ownId = await TriageTestData.SeedRecordAsync(
            Fixture, "My project", TriageState.NeedsReview,
            TriageTestData.CauseFound(TriageTestData.Draft("d1", "Fix it")),
            projectId: E2ETestSignIn.TeamProjectId);

        var browser = await Fixture.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var alicePage = await context.NewPageAsync();
        await E2ETestSignIn.SignInAsync(alicePage, BaseUrl, "alice", E2ETestSignIn.TeamGroup);

        var detail = new TriageDetailPage(alicePage, BaseUrl);
        await detail.NavigateAsync(otherId);
        await Assertions.Expect(detail.NotFound).ToHaveTextAsync("Triage not found.");

        // Positive control: her own project's triage opens with its actions
        await detail.NavigateAsync(ownId);
        await detail.WaitForLoadedAsync();
        await Assertions.Expect(detail.Actions).ToBeVisibleAsync();

        var list = new TriageListPage(alicePage, BaseUrl);
        await list.NavigateAsync();
        await Assertions.Expect(list.Rows).ToHaveCountAsync(1);
        await Assertions.Expect(list.Rows).ToContainTextAsync("My project");
    }

    // ════════════════════════════════════════════════════════════════════
    // Arrange helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Starts an operator triage, lets a connected agent report a cause-found result with
    /// <paramref name="drafts"/>, and waits until the triage is <see cref="TriageStatus.NeedsReview"/>.
    /// The agent has completed its job by then, so it is disconnected on return. The caller disposes the client.
    /// </summary>
    private async Task<(Guid Id, HttpClient Api)> StartWithResultAsync(params TriageDraft[] drafts)
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        var api = Fixture.CreateUiApiClient();
        try
        {
            await using var agent = new FakeAgentClient("agent-triage-review", TriageTestData.AgentLabel);
            await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
            var triage = await TriageTestData.StartOperatorTriageAsync(api, "Checkout fails");
            await TriageTestData.ReportResultAsync(agent, TriageTestData.CauseFound(drafts));
            await TriageTestData.WaitForStatusAsync(api, triage.Record.Id, TriageStatus.NeedsReview);
            return (triage.Record.Id, api);
        }
        catch
        {
            api.Dispose();
            throw;
        }
    }

    private static async Task<TriageDetail> GetDetailAsync(HttpClient api, Guid id) =>
        (await api.GetFromJsonAsync<TriageDetail>($"/api/ui/triages/{id}", PipelineJsonOptions.Default))!;
}
