using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Headless E2E tests for triage re-runs, covering:
/// - Draft-id namespacing across re-runs (mutation check),
/// - Older results and CreatedIssues surviving a re-run,
/// - Feedback/answer carry-through in TriageContextMarkdown,
/// - 409 guards while an attempt is active,
/// - Status resolution for Inconclusive / NotABug / Duplicate verdicts.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class TriageRerunE2ETests : HeadlessE2ETestBase
{
    public TriageRerunE2ETests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task WhenRerunWithFeedback_ShouldReplaceDrafts_AndKeepTheEarlierResultAndCreatedIssues()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        await using var agent = new FakeAgentClient("agent-triage-rerun", TriageTestData.AgentLabel);
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        using var api = Fixture.CreateUiApiClient();

        // ── Arrange: first attempt ────────────────────────────────────────
        var triage = await TriageTestData.StartOperatorTriageAsync(api, "cache-bug");

        var firstResult = new TriageResult
        {
            Verdict = TriageVerdict.CauseFound,
            Summary = "First summary",
            Investigated =
            [
                new TriageCheck { Check = "pool config", Where = "checkout-api", Result = "misconfigured" },
            ],
            Drafts =
            [
                TriageTestData.Draft("d1", "First fix"),
                TriageTestData.Draft("d2", "Second fix"),
            ],
        };
        await TriageTestData.ReportResultAsync(agent, firstResult);
        await TriageTestData.WaitForStatusAsync(api, triage.Record.Id, TriageStatus.NeedsReview);

        // Create an issue from the first-attempt draft so we can verify it survives the re-run
        var createResp = await api.PostAsJsonAsync(
            $"/api/ui/triages/{triage.Record.Id}/create-issues",
            new CreateTriageIssuesRequest { DraftIds = ["a1-d1"], Queue = false },
            PipelineJsonOptions.Default);
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        // create-issues returns CreateTriageIssuesResult, NOT TriageDetail — confirm via separate GET
        await TriageTestData.WaitForStatusAsync(api, triage.Record.Id, TriageStatus.IssuesCreated);

        // ── Act: re-run with feedback ─────────────────────────────────────
        // ResetJobAssigned MUST come before POST rerun so the TCS is fresh when the dispatcher fires
        agent.ResetJobAssigned();

        var rerunResp = await api.PostAsJsonAsync(
            $"/api/ui/triages/{triage.Record.Id}/rerun",
            new RerunTriageRequest { Feedback = "Look at the pool instead" },
            PipelineJsonOptions.Default);
        Assert.Equal(HttpStatusCode.OK, rerunResp.StatusCode);

        // ── Assert: rerun response ────────────────────────────────────────
        var rerunDetail = (await rerunResp.Content.ReadFromJsonAsync<TriageDetail>(PipelineJsonOptions.Default))!;
        Assert.Equal(TriageStatus.Investigating, rerunDetail.Status);
        Assert.Equal(2, rerunDetail.Record.Attempts.Count);
        Assert.Equal("Look at the pool instead", rerunDetail.Record.Attempts[1].Feedback!.Text);
        Assert.Equal("tester", rerunDetail.Record.Attempts[1].Feedback!.Author);

        // ── Assert: next assignment carries context markdown ──────────────
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("## Earlier attempts of this triage", assignment.TriageContextMarkdown);
        Assert.Contains("First summary", assignment.TriageContextMarkdown);
        Assert.Contains("## Feedback for this attempt", assignment.TriageContextMarkdown);
        Assert.Contains("Look at the pool instead", assignment.TriageContextMarkdown);

        // ── Act: second attempt ───────────────────────────────────────────
        var secondResult = new TriageResult
        {
            Verdict = TriageVerdict.CauseFound,
            Summary = "Second summary",
            Investigated =
            [
                new TriageCheck { Check = "pool config", Where = "checkout-api", Result = "confirmed" },
            ],
            Drafts = [TriageTestData.Draft("d1", "Third fix")],
        };
        await TriageTestData.ReportResultAsync(agent, secondResult);
        var final = await TriageTestData.WaitForStatusAsync(api, triage.Record.Id, TriageStatus.NeedsReview);

        // ── Assert: final state ───────────────────────────────────────────
        Assert.Equal(2, final.Record.Attempts.Count);
        Assert.Equal("First summary", final.Record.Attempts[0].Result!.Summary);
        Assert.Equal(TriageAttemptOutcome.Completed, final.Record.Attempts[0].Outcome);
        Assert.Equal("Second summary", final.Record.Attempts[1].Result!.Summary);
        Assert.Equal("Second summary", final.Record.LatestResult!.Summary);

        var draft = Assert.Single(final.Record.Drafts);
        Assert.Equal("a2-d1", draft.Current.Id);   // ← mutation check: this fails if ApplyResult uses d.Id instead of $"a{n}-{d.Id}"
        Assert.Equal("Third fix", draft.Current.Title);
        Assert.Equal(final.Record.Attempts[1].WorkItemId, final.Record.DraftsFromWorkItemId);

        var createdIssue = Assert.Single(final.Record.CreatedIssues);
        Assert.Equal("a1-d1", createdIssue.DraftId);
    }

    [Fact]
    public async Task WhenRerunWithAnswers_ShouldCarryThemIntoTheNextAttempt()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        await using var agent = new FakeAgentClient("agent-triage-rerun", TriageTestData.AgentLabel);
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        using var api = Fixture.CreateUiApiClient();

        // ── Arrange: Inconclusive result with a question ─────────────────
        var triage = await TriageTestData.StartOperatorTriageAsync(api, "env-question");

        // Inconclusive does NOT require an Investigated entry.
        // TriageResult.Questions is IReadOnlyList<TriageQuestion>, not IReadOnlyList<string>.
        var inconclusiveResult = new TriageResult
        {
            Verdict = TriageVerdict.Inconclusive,
            Summary = "Cannot reproduce without knowing the environment",
            Questions = [new TriageQuestion { Question = "Which environment?" }],
        };
        await TriageTestData.ReportResultAsync(agent, inconclusiveResult);
        await TriageTestData.WaitForStatusAsync(api, triage.Record.Id, TriageStatus.NeedsInput);

        // ── Act: re-run with answers ──────────────────────────────────────
        agent.ResetJobAssigned();

        var rerunResp = await api.PostAsJsonAsync(
            $"/api/ui/triages/{triage.Record.Id}/rerun",
            new RerunTriageRequest
            {
                Answers = [new TriageAnswer { Question = "Which environment?", Answer = "production" }],
            },
            PipelineJsonOptions.Default);
        Assert.Equal(HttpStatusCode.OK, rerunResp.StatusCode);

        // ── Assert: feedback carries the answers ──────────────────────────
        var detail = (await rerunResp.Content.ReadFromJsonAsync<TriageDetail>(PipelineJsonOptions.Default))!;
        var feedback = detail.Record.Attempts[1].Feedback!;
        Assert.Single(feedback.Answers);
        Assert.Equal("Which environment?", feedback.Answers[0].Question);
        Assert.Equal("production", feedback.Answers[0].Answer);

        // ── Assert: next assignment carries Q/A in context markdown ───────
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("**Q:** Which environment?", assignment.TriageContextMarkdown);
        Assert.Contains("**A:** production", assignment.TriageContextMarkdown);
    }

    [Fact]
    public async Task WhenAnAttemptIsRunning_ShouldRefuseRerunAndDismiss()
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        // No FakeAgentClient connected in this test: the WorkItem stays Pending (no agent with
        // the triage-e2e label is registered), so GetActiveWorkItemIdAsync returns it as active.
        using var api = Fixture.CreateUiApiClient();

        var triage = await TriageTestData.StartOperatorTriageAsync(api, "active-triage");

        // ── Rerun should 409 ──────────────────────────────────────────────
        var rerunResp = await api.PostAsJsonAsync(
            $"/api/ui/triages/{triage.Record.Id}/rerun",
            new RerunTriageRequest { Feedback = "some feedback" },
            PipelineJsonOptions.Default);
        Assert.Equal(HttpStatusCode.Conflict, rerunResp.StatusCode);
        // RefusalAsJsonAsync serialises the message as a raw JSON string (quoted)
        var rerunMsg = JsonSerializer.Deserialize<string>(
            await rerunResp.Content.ReadAsStringAsync(),
            PipelineJsonOptions.Default);
        Assert.Equal("An attempt is already running", rerunMsg);

        // ── Dismiss should 409 ────────────────────────────────────────────
        var dismissResp = await api.PostAsJsonAsync(
            $"/api/ui/triages/{triage.Record.Id}/dismiss",
            new DismissTriageRequest { Reason = "test" },
            PipelineJsonOptions.Default);
        Assert.Equal(HttpStatusCode.Conflict, dismissResp.StatusCode);
        var dismissMsg = JsonSerializer.Deserialize<string>(
            await dismissResp.Content.ReadAsStringAsync(),
            PipelineJsonOptions.Default);
        Assert.Equal("An attempt is running; cancel it first", dismissMsg);
    }

    [Theory]
    [InlineData(TriageVerdict.Inconclusive, TriageStatus.NeedsInput)]
    [InlineData(TriageVerdict.NotABug,      TriageStatus.NotABug)]
    [InlineData(TriageVerdict.Duplicate,    TriageStatus.Duplicate)]
    public async Task WhenAVerdictIsReported_ShouldResolveItsStatus(
        TriageVerdict verdict, TriageStatus expected)
    {
        await TriageTestData.SeedTemplateAndRouteAsync(Fixture);
        await using var agent = new FakeAgentClient("agent-triage-rerun", TriageTestData.AgentLabel);
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        using var api = Fixture.CreateUiApiClient();

        var triage = await TriageTestData.StartOperatorTriageAsync(api, $"verdict-{verdict}");

        // CauseFound requires an Investigated entry, but it is not in this theory's cases.
        // Inconclusive/NotABug/Duplicate need no Investigated, no Drafts, no Questions.
        await TriageTestData.ReportResultAsync(agent, new TriageResult
        {
            Verdict = verdict,
            Summary = "Test summary",
        });

        var detail = await TriageTestData.WaitForStatusAsync(api, triage.Record.Id, expected);

        Assert.Equal(verdict, detail.Record.LatestResult!.Verdict);
        Assert.Empty(detail.Record.Drafts);

        if (verdict == TriageVerdict.Inconclusive)
        {
            // TriageResultValidator injects one generic question when Questions is empty and
            // the verdict is Inconclusive (TriageResultValidator.cs:50-54)
            Assert.Single(detail.Record.LatestResult!.Questions);
        }
    }
}
