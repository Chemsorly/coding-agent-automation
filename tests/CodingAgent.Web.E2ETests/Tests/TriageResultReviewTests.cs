using System.Net;
using System.Net.Http.Json;
using CodingAgent.Contracts.Auth;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;
// Disambiguate: CodingAgent.Pipeline.Models also has a TriageListPage DTO record.
using TriageListPage = CodingAgent.Web.E2ETests.PageObjects.TriageListPage;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Browser E2E tests for reviewing triage results: verdict/evidence display, draft editing,
/// issue creation with queue flag, partial-failure retry, idempotency, and role-based access.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class TriageResultReviewTests : E2ETestBase
{
    public TriageResultReviewTests(E2EFixture fixture) : base(fixture) { }

    // ════════════════════════════════════════════════════════════════════
    // Shared arrange for tests 1–5
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Seeds a template and route, starts an operator triage, dispatches it to a fake agent,
    /// reports a CauseFound result, and waits until the triage reaches NeedsReview.
    /// Returns the triage id and a UI API client; the caller owns the client's disposal.
    /// <para>
    /// The FakeAgentClient is placed inside this helper rather than held in the test because
    /// ReportResultAsync completes the agent's job (ReportCompletionAsync then
    /// WaitForStatusAsync(NeedsReview)), so disposing it here introduces no race condition.
    /// This deviates from the literal "hold in test" pitfall note but is safe.
    /// </para>
    /// </summary>
    private async Task<(Guid id, HttpClient api)> StartWithResultAsync(params TriageDraft[] drafts)
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        var api = Fixture.CreateUiApiClient();
        try
        {
            await using var agent = new FakeAgentClient("agent-triage-review", TriageTestData.AgentLabel);
            await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
            var triage = await TriageTestData.StartOperatorTriageAsync(api, "Checkout returns 502");
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

    // ════════════════════════════════════════════════════════════════════
    // Result and drafts (T2)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenAResultIsReported_ShouldShowVerdictEvidenceAndDrafts()
    {
        var (id, api) = await StartWithResultAsync(
            TriageTestData.Draft("d1", "Fix the retry"),
            TriageTestData.Draft("d2", "Add an alert"));
        using var _ = api;

        var detail = new TriageDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(id);
        await detail.WaitForLoadedAsync();

        await Assertions.Expect(detail.Status).ToHaveTextAsync("RCA ready");
        await Assertions.Expect(detail.VerdictPill).ToHaveTextAsync("Cause found");
        await Assertions.Expect(detail.Verdict).ToContainTextAsync(
            TriageTestData.CauseFound().Summary);
        await Assertions.Expect(detail.Investigated).ToContainTextAsync("5xx rate");
        await Assertions.Expect(detail.Evidence).ToContainTextAsync("Pool wait p99 29.8 s");
        await Assertions.Expect(detail.Draft).ToHaveCountAsync(2);
        await Assertions.Expect(detail.Create).ToHaveTextAsync("Create 2 issues");
    }

    [Fact]
    public async Task WhenADraftIsEdited_ShouldShowTheEdit_AndResetRestoresTheAgentsVersion()
    {
        var (id, api) = await StartWithResultAsync(
            TriageTestData.Draft("d1", "Original title", "Original body"));
        using var _ = api;

        var detail = new TriageDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(id);
        await detail.WaitForLoadedAsync();

        // ── Edit ──────────────────────────────────────────────────────
        await detail.DraftEdit.ClickAsync();
        await detail.DraftTitle.FillAsync("Edited title");
        await detail.DraftBody.FillAsync("Edited body");
        // DraftTitle and DraftBody use plain @bind (onchange). FillAsync sets the value;
        // clicking Save blurs the inputs first, committing the changes before the save handler runs.
        await detail.DraftSave.ClickAsync();

        await Assertions.Expect(detail.DraftEditor).ToHaveCountAsync(0);
        await Assertions.Expect(detail.Draft).ToContainTextAsync("Edited title");
        await Assertions.Expect(detail.Draft).ToContainTextAsync("Edited body");
        await Assertions.Expect(detail.Draft).ToContainTextAsync("edited by admin");

        var afterEdit = await api.GetFromJsonAsync<TriageDetail>(
            $"/api/ui/triages/{id}", PipelineJsonOptions.Default);
        Assert.Equal("Edited title", afterEdit!.Record.Drafts[0].Current.Title);
        Assert.Equal("Original title", afterEdit.Record.Drafts[0].Original.Title);
        Assert.Equal("admin", afterEdit.Record.Drafts[0].EditedBy);

        // ── Reset ─────────────────────────────────────────────────────
        await detail.DraftEdit.ClickAsync();
        await detail.DraftReset.ClickAsync();
        await Assertions.Expect(detail.DraftTitle).ToHaveValueAsync("Original title");

        // Close drawer with Cancel
        await Page.Locator(".triage-drawer-foot button:has-text('Cancel')").ClickAsync();
        await Assertions.Expect(detail.Draft).ToContainTextAsync("Original title");
        await Assertions.Expect(detail.Draft).Not.ToContainTextAsync("edited by");

        var afterReset = await api.GetFromJsonAsync<TriageDetail>(
            $"/api/ui/triages/{id}", PipelineJsonOptions.Default);
        Assert.False(afterReset!.Record.Drafts[0].Edited);
    }

    [Fact]
    public async Task WhenOneDraftIsEditedSelectedAndQueued_ShouldCreateExactlyThatIssue()
    {
        var (id, api) = await StartWithResultAsync(
            TriageTestData.Draft("d1", "Fix the retry", "Remove the retry."),
            TriageTestData.Draft("d2", "Add an alert"));
        using var _ = api;

        var detail = new TriageDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(id);
        await detail.WaitForLoadedAsync();

        // Edit d1 (the first draft) title
        await detail.DraftEdit.First.ClickAsync();
        await detail.DraftTitle.FillAsync("Fix the retry policy");
        await detail.DraftSave.ClickAsync();
        await Assertions.Expect(detail.DraftEditor).ToHaveCountAsync(0);

        // Deselect d2 (index 1); all uncreated drafts are selected on load (TriagePage.razor:597-606)
        await detail.DraftSelect.Nth(1).UncheckAsync();

        // Check the queue checkbox (@bind — must use CheckAsync, not a label click)
        await detail.Queue.CheckAsync();

        await Assertions.Expect(detail.Create).ToHaveTextAsync("Create 1 issue");
        await detail.Create.ClickAsync();

        await Assertions.Expect(detail.Notice).ToHaveTextAsync("Created 1 issue.");
        await Assertions.Expect(detail.Status).ToHaveTextAsync("Issues created");
        await Assertions.Expect(detail.CreatedLink).ToHaveCountAsync(1);
        await Assertions.Expect(detail.CreatedIssues).ToContainTextAsync("checkout-api #");

        Assert.Single(Fixture.IssueProvider.CreatedIssues);
        Assert.Equal("Fix the retry policy", Fixture.IssueProvider.CreatedIssues[0].Title);
        Assert.Contains("Remove the retry.", Fixture.IssueProvider.CreatedIssues[0].Body);
        Assert.Contains("(attempt 1) and approved in Coding Agent.",
            Fixture.IssueProvider.CreatedIssues[0].Body);
        Assert.Contains(AgentLabels.Generated, Fixture.IssueProvider.CreatedIssues[0].Labels!);
        Assert.Contains(AgentLabels.Next, Fixture.IssueProvider.CreatedIssues[0].Labels!);
    }

    // ════════════════════════════════════════════════════════════════════
    // Partial failure (T3)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenOneCreateFails_ShouldShowThePartialResult_AndRetryOnlyTheRemainingDraft()
    {
        var (id, api) = await StartWithResultAsync(
            TriageTestData.Draft("d1", "First fix"),
            TriageTestData.Draft("d2", "Second fix"));
        using var _ = api;

        var detail = new TriageDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(id);
        await detail.WaitForLoadedAsync();

        // Arrange per-call failure for the NEXT CreateIssueAsync call only.
        // ProviderFaultPlan.FailNext (times=1, skip=0) is consumed by the first call (a1-d1 /
        // "First fix"). The second call (a1-d2 / "Second fix") succeeds.
        // The global Faults.FailNext is used here (not the boolean flag) because the boolean flag
        // fails every call including the retry, which would make the test loop forever.
        Fixture.IssueProvider.Faults.FailNext("CreateIssueAsync");

        // ── First click: d1 fails, d2 succeeds ──────────────────────
        // Ordering contract: TriageIssueCreator iterates DraftIds in list order (d1 before d2,
        // as submitted by the page's SelectableDrafts sequence). The single FailNext fault is
        // consumed by the first call (d1/"First fix"); the second call (d2/"Second fix") succeeds.
        // If server or UI ever changed draft iteration order this test would fail — intentionally,
        // because the partial-failure assertions are order-dependent.
        await detail.Create.ClickAsync();

        await Assertions.Expect(detail.Notice).ToContainTextAsync(
            "Created 1 issue; 1 failed: The issue could not be created:");
        Assert.Single(Fixture.IssueProvider.CreatedIssues);
        Assert.Equal("Second fix", Fixture.IssueProvider.CreatedIssues[0].Title);
        await Assertions.Expect(detail.Draft).ToHaveCountAsync(2);
        await Assertions.Expect(detail.CreatedLink).ToHaveCountAsync(1);

        var afterFirst = await api.GetFromJsonAsync<TriageDetail>(
            $"/api/ui/triages/{id}", PipelineJsonOptions.Default);
        Assert.Single(afterFirst!.Record.CreatedIssues);
        Assert.Equal("a1-d2", afterFirst.Record.CreatedIssues[0].DraftId);

        // ── Retry: only d1 remains ───────────────────────────────────
        await Assertions.Expect(detail.Create).ToHaveTextAsync("Create 1 issue");
        await detail.Create.ClickAsync();

        await Assertions.Expect(detail.Notice).ToHaveTextAsync("Created 1 issue.");
        Assert.Equal(2, Fixture.IssueProvider.CreatedIssues.Count);
        // Second fix was created first (first click), First fix was created second (retry)
        Assert.Equal("Second fix", Fixture.IssueProvider.CreatedIssues[0].Title);
        Assert.Equal("First fix", Fixture.IssueProvider.CreatedIssues[1].Title);
        // Queue was not checked — both issues have exactly agent:generated (not agent:next)
        Assert.Contains(AgentLabels.Generated, Fixture.IssueProvider.CreatedIssues[0].Labels!);
        Assert.DoesNotContain(AgentLabels.Next, Fixture.IssueProvider.CreatedIssues[0].Labels!);
        Assert.Contains(AgentLabels.Generated, Fixture.IssueProvider.CreatedIssues[1].Labels!);
        Assert.DoesNotContain(AgentLabels.Next, Fixture.IssueProvider.CreatedIssues[1].Labels!);
    }

    [Fact]
    public async Task WhenCreateIsRepeatedForRecordedDrafts_ShouldNotCreateAgain()
    {
        var (id, api) = await StartWithResultAsync(TriageTestData.Draft("d1", "Fix it"));
        using var _ = api;

        // Draft id is rewritten to "a1-d1" by PostgresTriageStore.ApplyResult (mirrored in
        // TriageTestData.ReportResultAsync via StartWithResultAsync).
        var request = new CreateTriageIssuesRequest { DraftIds = ["a1-d1"] };

        var r1 = await api.PostAsJsonAsync(
            $"/api/ui/triages/{id}/create-issues", request, PipelineJsonOptions.Default);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        var r2 = await api.PostAsJsonAsync(
            $"/api/ui/triages/{id}/create-issues", request, PipelineJsonOptions.Default);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        // The idempotency guard at TriageIssueCreator.cs:81-85 returns the already-recorded
        // TriageCreatedIssue without calling CreateIssueAsync a second time.
        var result2 = await r2.Content.ReadFromJsonAsync<CreateTriageIssuesResult>(
            PipelineJsonOptions.Default);
        Assert.Single(result2!.Created);
        Assert.Empty(result2.Errors);

        Assert.Single(Fixture.IssueProvider.CreatedIssues);

        var triageDetail = await api.GetFromJsonAsync<TriageDetail>(
            $"/api/ui/triages/{id}", PipelineJsonOptions.Default);
        Assert.Single(triageDetail!.Record.CreatedIssues);
    }

    // ════════════════════════════════════════════════════════════════════
    // Roles (T9)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenAGlobalReaderOpensATriage_ShouldShowNoActions()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        var id = await TriageTestData.SeedRecordAsync(
            Fixture,
            "Reader sees this",
            TriageState.NeedsReview,
            TriageTestData.CauseFound(TriageTestData.Draft("d1", "Fix it")));

        var browser = await Fixture.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await E2ETestSignIn.SignInAsync(page, BaseUrl, E2ETestSignIn.ReaderUser);

        // ── List page ─────────────────────────────────────────────────
        var list = new TriageListPage(page, BaseUrl);
        await list.NavigateAsync();
        await Assertions.Expect(
            list.Rows.Filter(new LocatorFilterOptions { HasText = "Reader sees this" }))
            .ToBeVisibleAsync();
        // triage-new is guarded by Access.Grant.HasAnyOperator (Triage.razor:31)
        await Assertions.Expect(list.NewTriageLink).ToHaveCountAsync(0);

        // ── Detail page ───────────────────────────────────────────────
        var detail = new TriageDetailPage(page, BaseUrl);
        await detail.NavigateAsync(id);
        await detail.WaitForLoadedAsync();

        await Assertions.Expect(detail.Verdict).ToBeVisibleAsync();
        await Assertions.Expect(detail.Draft).ToHaveCountAsync(1);
        // CanAct requires operator; a global reader gets none of these controls
        await Assertions.Expect(detail.Actions).ToHaveCountAsync(0);
        await Assertions.Expect(detail.DraftSelect).ToHaveCountAsync(0);
        await Assertions.Expect(detail.DraftEdit).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAGlobalReaderOpensAnInconclusiveTriage_ShouldShowQuestionsReadOnly()
    {
        var result = new TriageResult
        {
            Verdict = TriageVerdict.Inconclusive,
            Summary = "Could not determine the root cause.",
            Questions = [new TriageQuestion { Question = "Which environment?" }],
        };
        var id = await TriageTestData.SeedRecordAsync(
            Fixture, "Inconclusive triage", TriageState.NeedsInput, result);

        var browser = await Fixture.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await E2ETestSignIn.SignInAsync(page, BaseUrl, E2ETestSignIn.ReaderUser);

        var detail = new TriageDetailPage(page, BaseUrl);
        await detail.NavigateAsync(id);
        await detail.WaitForLoadedAsync();

        // ShowQuestions = !answering; answering = NeedsInput && CanAct — false for reader
        await Assertions.Expect(detail.ReportQuestions).ToContainTextAsync("Which environment?");
        // The answer form (triage-questions) is inside @if (answering) — not shown to a reader
        await Assertions.Expect(detail.Questions).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAReaderCallsAnAction_ShouldBeRefused()
    {
        // Headless within E2ETestBase — no browser page needed
        var id = await TriageTestData.SeedRecordAsync(
            Fixture,
            "Protected triage",
            TriageState.NeedsReview,
            TriageTestData.CauseFound(TriageTestData.Draft("d1", "Fix it")));

        // POST dismiss as a global ReadOnly user
        using var readerApi = Fixture.CreateUiApiClient(
            new AccessGrant(AccessRole.ReadOnly, new Dictionary<string, AccessRole>(), []));
        var resp = await readerApi.PostAsJsonAsync(
            $"/api/ui/triages/{id}/dismiss",
            new DismissTriageRequest(),
            PipelineJsonOptions.Default);

        // UiTriageEndpoints.cs:29: action group requires UiPolicies.AnyOperator
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // Record must be unchanged
        using var adminApi = Fixture.CreateUiApiClient();
        var triageDetail = await adminApi.GetFromJsonAsync<TriageDetail>(
            $"/api/ui/triages/{id}", PipelineJsonOptions.Default);
        Assert.Equal(TriageState.NeedsReview, triageDetail!.Record.State);
    }

    [Fact]
    public async Task WhenAScopedOperatorOpensAnotherProjectsTriage_ShouldSeeNotFound()
    {
        // Other project: seeded in the Default project; alice cannot read it
        var otherId = await TriageTestData.SeedRecordAsync(
            Fixture, "Other project", TriageState.New);

        // Own project: seeded in the Team project; alice is operator there
        var ownId = await TriageTestData.SeedRecordAsync(
            Fixture,
            "My project",
            TriageState.NeedsReview,
            TriageTestData.CauseFound(TriageTestData.Draft("d1", "Fix it")),
            projectId: E2ETestSignIn.TeamProjectId);

        // Pitfall: sign in as alice BEFORE reading the list; the admin's default all-projects
        // scope would show both records and break the single-row assertion.
        var browser = await Fixture.GetBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await E2ETestSignIn.SignInAsync(page, BaseUrl, "alice", E2ETestSignIn.TeamGroup);

        var detail = new TriageDetailPage(page, BaseUrl);

        // ── Other project: not found ───────────────────────────────────
        // UserAccess.cs:28 returns 404 for unreadable projects; UiApiClient.FindAsync maps it to
        // null; TriagePage.razor:34 shows triage-not-found.
        await detail.NavigateAsync(otherId);
        await Assertions.Expect(detail.NotFound).ToHaveTextAsync("Triage not found.");

        // ── Own project: actions visible (positive control) ────────────
        await detail.NavigateAsync(ownId);
        await detail.WaitForLoadedAsync();
        await Assertions.Expect(detail.Actions).ToBeVisibleAsync();

        // ── List: only the own-project row ─────────────────────────────
        var list = new TriageListPage(page, BaseUrl);
        await list.NavigateAsync();
        await Assertions.Expect(list.Rows).ToHaveCountAsync(1);
        await Assertions.Expect(list.Rows.First).ToContainTextAsync("My project");
    }
}
