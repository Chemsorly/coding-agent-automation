using System.Net.Http.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Browser E2E tests for the triage detail page (<c>/triage/{id}</c>) covering:
/// - NeedsInput state: answer form, re-run-with-answers flow,
/// - NotABug and Duplicate verdict rendering,
/// - Failed and Cancelled attempt display,
/// - Earlier result remaining visible when a later attempt fails.
/// No agent is connected in any test; state is seeded directly or via API.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class TriageVerdictPageTests : E2ETestBase
{
    public TriageVerdictPageTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task WhenInconclusive_ShouldAskForAnswers_AndStartTheNextAttempt()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);

        // Seed a NeedsInput record with one question
        var id = await TriageTestData.SeedRecordAsync(
            Fixture, "env-question",
            TriageState.NeedsInput,
            result: new TriageResult
            {
                Verdict = TriageVerdict.Inconclusive,
                Summary = "Cannot reproduce",
                Questions = [new TriageQuestion { Question = "Which environment?" }],
            });

        var page = new TriageDetailPage(Page, BaseUrl);
        await page.NavigateAsync(id);
        await page.WaitForLoadedAsync();

        // ── Assert initial state ──────────────────────────────────────────
        await Assertions.Expect(page.Status).ToHaveTextAsync("Needs input");
        await Assertions.Expect(page.VerdictPill).ToHaveTextAsync("Inconclusive");
        await Assertions.Expect(page.Questions).ToBeVisibleAsync();
        await Assertions.Expect(page.RerunAnswers).ToBeDisabledAsync();

        // ── Act: fill the answer and submit ──────────────────────────────
        await page.AnswerAt(0).FillAsync("production");
        await page.RerunAnswers.ClickAsync();

        // ── Assert: triage transitions to Investigating ───────────────────
        await Assertions.Expect(page.Status).ToHaveTextAsync("Investigating");
        await Assertions.Expect(page.Running).ToContainTextAsync("Attempt 2 is investigating");
        await Assertions.Expect(page.Attempts).ToContainTextAsync("Which environment? → production");
    }

    [Fact]
    public async Task WhenNotABug_ShouldShowTheVerdict_AndOfferARerun()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);

        var id = await TriageTestData.SeedRecordAsync(
            Fixture, "works-as-designed",
            TriageState.NotABug,
            result: new TriageResult
            {
                Verdict = TriageVerdict.NotABug,
                Summary = "Works as designed",
            });

        var page = new TriageDetailPage(Page, BaseUrl);
        await page.NavigateAsync(id);
        await page.WaitForLoadedAsync();

        await Assertions.Expect(page.Status).ToHaveTextAsync("Not a bug");
        await Assertions.Expect(page.Verdict).ToContainTextAsync("Works as designed");
        await Assertions.Expect(page.Actions).ToContainTextAsync("No issue drafts in this result.");
        await Assertions.Expect(page.RerunOpen).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WhenDuplicate_ShouldNameTheDuplicate()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);

        var id = await TriageTestData.SeedRecordAsync(
            Fixture, "duplicate-case",
            TriageState.Duplicate,
            result: new TriageResult
            {
                Verdict = TriageVerdict.Duplicate,
                Summary = "dup",
                DuplicateOf = "#123",
            });

        var page = new TriageDetailPage(Page, BaseUrl);
        await page.NavigateAsync(id);
        await page.WaitForLoadedAsync();

        await Assertions.Expect(page.Status).ToHaveTextAsync("Duplicate");
        await Assertions.Expect(page.Verdict).ToContainTextAsync("Duplicate of");
        await Assertions.Expect(page.Verdict).ToContainTextAsync("#123");
    }

    [Fact]
    public async Task WhenTheRunFails_ShouldShowFailed_AndOfferARerun()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        using var api = Fixture.CreateUiApiClient();

        // Start a triage (creates a Pending WorkItem; no agent connected so it stays Pending)
        var triage = await TriageTestData.StartOperatorTriageAsync(api, "crash-pod");

        // TriageAttempt.WorkItemId is string; PostStatusAsync requires Guid
        var wiId = Guid.Parse(triage.Record.Attempts[0].WorkItemId);

        await Fixture.WorkItems.PostStatusAsync(wiId, new WorkItemStatusUpdate
        {
            Status = nameof(WorkItemStatus.Failed),
            ErrorMessage = "Agent pod terminated",
            FailureReason = "ExitCodeFailure",
        }, CancellationToken.None);

        // Poll until the WorkItem row reflects the terminal status before calling backfill
        await WaitUntilAsync(async () =>
        {
            await using var ctx = await Fixture.DbContextFactory.CreateDbContextAsync();
            var wi = await ctx.WorkItems.FindAsync(wiId);
            return wi?.Status is WorkItemStatus.Failed;
        });

        // BackfillEndedAttemptsAsync copies the WorkItem outcome onto the attempt in the
        // TriageRecord; without it the page cannot distinguish Failed from Cancelled
        await Fixture.ApiServices.GetRequiredService<ITriageStore>()
            .BackfillEndedAttemptsAsync(CancellationToken.None);

        var detailPage = new TriageDetailPage(Page, BaseUrl);
        await detailPage.NavigateAsync(triage.Record.Id);
        await detailPage.WaitForLoadedAsync();

        // ── Assert: failed state shown ────────────────────────────────────
        await Assertions.Expect(detailPage.Status).ToHaveTextAsync("Failed");
        await Assertions.Expect(detailPage.Failed).ToContainTextAsync("Attempt 1 ended without a result");
        await Assertions.Expect(detailPage.Failed).ToContainTextAsync("Agent pod terminated");

        // ── Act: re-run via the UI ────────────────────────────────────────
        await detailPage.RerunOpen.ClickAsync();
        await detailPage.Feedback.FillAsync("Try a different pod");
        await detailPage.Rerun.ClickAsync();

        // ── Assert: status transitions to Investigating ───────────────────
        await Assertions.Expect(detailPage.Status).ToHaveTextAsync("Investigating");
    }

    [Fact]
    public async Task WhenTheRunIsCancelled_ShouldShowCancelled()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        using var api = Fixture.CreateUiApiClient();

        var triage = await TriageTestData.StartOperatorTriageAsync(api, "cancelled-pod");
        var wiId = Guid.Parse(triage.Record.Attempts[0].WorkItemId);

        // Cancelled with no failure fields
        await Fixture.WorkItems.PostStatusAsync(wiId, new WorkItemStatusUpdate
        {
            Status = nameof(WorkItemStatus.Cancelled),
        }, CancellationToken.None);

        await WaitUntilAsync(async () =>
        {
            await using var ctx = await Fixture.DbContextFactory.CreateDbContextAsync();
            var wi = await ctx.WorkItems.FindAsync(wiId);
            return wi?.Status is WorkItemStatus.Cancelled;
        });

        await Fixture.ApiServices.GetRequiredService<ITriageStore>()
            .BackfillEndedAttemptsAsync(CancellationToken.None);

        var detailPage = new TriageDetailPage(Page, BaseUrl);
        await detailPage.NavigateAsync(triage.Record.Id);
        await detailPage.WaitForLoadedAsync();

        await Assertions.Expect(detailPage.Status).ToHaveTextAsync("Cancelled");
        // TriagePage.razor renders "was cancelled" when Attempt.Outcome == Cancelled
        await Assertions.Expect(detailPage.Failed).ToContainTextAsync("Attempt 1 was cancelled");
        await Assertions.Expect(detailPage.Attempts).ToContainTextAsync("Cancelled");
        await Assertions.Expect(detailPage.RerunOpen).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WhenALaterAttemptFails_ShouldKeepTheEarlierResultVisible()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        using var api = Fixture.CreateUiApiClient();

        // SeedRecordAsync bypasses the validator, so CauseFound needs no Investigated entry
        var id = await TriageTestData.SeedRecordAsync(
            Fixture, "earlier-ok",
            TriageState.NeedsReview,
            result: new TriageResult
            {
                Verdict = TriageVerdict.CauseFound,
                Summary = "Earlier result",
            });

        // Re-run via the API; the response already contains the new attempt with its WorkItemId
        var rerunResp = await api.PostAsJsonAsync(
            $"/api/ui/triages/{id}/rerun",
            new RerunTriageRequest { Feedback = "Second pass" },
            PipelineJsonOptions.Default);
        rerunResp.EnsureSuccessStatusCode();
        var rerunDetail = (await rerunResp.Content.ReadFromJsonAsync<TriageDetail>(PipelineJsonOptions.Default))!;

        // TriageAttempt.WorkItemId is string; PostStatusAsync requires Guid
        var wiId2 = Guid.Parse(rerunDetail.Record.Attempts[1].WorkItemId);

        await Fixture.WorkItems.PostStatusAsync(wiId2, new WorkItemStatusUpdate
        {
            Status = nameof(WorkItemStatus.Failed),
            ErrorMessage = "Second attempt died",
            FailureReason = "ExitCodeFailure",
        }, CancellationToken.None);

        // Poll until the WorkItem row is terminal before calling backfill
        await WaitUntilAsync(async () =>
        {
            await using var ctx = await Fixture.DbContextFactory.CreateDbContextAsync();
            var wi = await ctx.WorkItems.FindAsync(wiId2);
            return wi?.Status is WorkItemStatus.Failed;
        });

        await Fixture.ApiServices.GetRequiredService<ITriageStore>()
            .BackfillEndedAttemptsAsync(CancellationToken.None);

        var detailPage = new TriageDetailPage(Page, BaseUrl);
        await detailPage.NavigateAsync(id);
        await detailPage.WaitForLoadedAsync();

        // TriageStatusResolver uses only the LAST attempt: last has no result → Failed
        await Assertions.Expect(detailPage.Status).ToHaveTextAsync("Failed");
        // The page names the failing attempt (N=2) and the earlier result (K=1)
        await Assertions.Expect(detailPage.Failed).ToContainTextAsync("Attempt 2 ended without a result");
        await Assertions.Expect(detailPage.Failed).ToContainTextAsync("Below is the result of attempt 1.");
        // TriageReport renders the latest result attempt's verdict/summary (the seeded first attempt)
        await Assertions.Expect(detailPage.Verdict).ToContainTextAsync("Earlier result");
    }
}
