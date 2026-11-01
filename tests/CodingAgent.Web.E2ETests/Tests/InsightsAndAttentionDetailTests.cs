using System.Net.Http.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests covering gaps in the Insights page (1h and 6h windows, cost and token cards,
/// feedback cards) and the Attention page (MCP unavailability, MCP OK clears section, triage
/// needing review).
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class InsightsAndAttentionDetailTests : E2ETestBase
{
    public InsightsAndAttentionDetailTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NewRunId() => Guid.NewGuid().ToString();

    private async Task SeedCompletedRunAsync(
        string runId,
        string issue,
        DateTimeOffset started,
        long totalTokens = 0,
        decimal? totalCost = null,
        RunFeedback? feedback = null,
        IReadOnlyList<string>? mcpServers = null,
        IReadOnlyDictionary<string, PhaseUsage>? phaseBreakdown = null)
    {
        var completed = started + TimeSpan.FromMinutes(1);
#pragma warning disable CS0618
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier(issue),
            IssueTitle = $"Issue {issue}",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = started,
            StartedAt = started.DateTime,
            CompletedAtOffset = completed,
            CompletedAt = completed.DateTime,
            TotalTokens = totalTokens,
            TotalCost = totalCost,
            Feedback = feedback,
            McpServers = mcpServers,
            PhaseBreakdown = phaseBreakdown,
        });
#pragma warning restore CS0618
    }

    // ── Insights: short windows count only their runs ─────────────────────────

    [Fact]
    public async Task ShortWindows_CountOnlyTheirRuns()
    {
        var now = DateTimeOffset.UtcNow;

        // 30 minutes ago: always in both the 1h and 6h windows
        await SeedCompletedRunAsync(NewRunId(), "WIN-30M", now.AddMinutes(-30));
        // 3 hours ago: in the 6h window but not in the 1h window
        await SeedCompletedRunAsync(NewRunId(), "WIN-3H", now.AddHours(-3));
        // 23 hours ago: in the 24h window but not in the 1h or 6h windows
        await SeedCompletedRunAsync(NewRunId(), "WIN-23H", now.AddHours(-23));

        var insights = new InsightsPage(Page, BaseUrl);
        await insights.NavigateAsync();

        // Default 24h window: all 3. Poll until the value settles — insights-total renders
        // as 0 during the initial Blazor load and SelectWindowAsync relies on a fixed 500ms
        // sleep that can be insufficient under CI load.
        await WaitUntilAsync(async () => await insights.GetTotalAsync() == 3);
        Assert.Equal(3, await insights.GetTotalAsync());

        // 6h window: 30-min and 3h runs (30 min ago is always in; 3h ago depends on hour boundary
        // but the issue guarantees that with exactly these offsets: select 6 → 2)
        await insights.SelectWindowAsync("6");
        await WaitUntilAsync(async () => await insights.GetTotalAsync() == 2);
        Assert.Equal(2, await insights.GetTotalAsync());

        // 1h window: only the 30-min run (issue guarantees: select 1 → 1)
        await insights.SelectWindowAsync("1");
        await WaitUntilAsync(async () => await insights.GetTotalAsync() == 1);
        Assert.Equal(1, await insights.GetTotalAsync());
    }

    // ── Insights: cost and token tiles show when a run reports them ────────────

    [Fact]
    public async Task CostAndTokens_ShowWhenARunReportsThem()
    {
        var now = DateTimeOffset.UtcNow;

        // Three completed runs; only one has tokens and cost
        await SeedCompletedRunAsync(NewRunId(), "CT-1", now.AddMinutes(-10), totalTokens: 3000, totalCost: 1.50m);
        await SeedCompletedRunAsync(NewRunId(), "CT-2", now.AddMinutes(-11));
        await SeedCompletedRunAsync(NewRunId(), "CT-3", now.AddMinutes(-12));

        var insights = new InsightsPage(Page, BaseUrl);
        await insights.NavigateAsync();

        // Token tile: average = 3000 / 3 = 1000 tokens → "1.0K"
        var tokenStat = Page.Locator(".cockpit-stat:has-text('Avg tokens / run') .cockpit-stat-v");
        await Assertions.Expect(tokenStat).ToHaveTextAsync("1.0K");

        // Cost card: $1.50 total, 1 of 3 runs has cost → 33%
        var costCard = Page.Locator(".cockpit-card:has(h2:text-is('Cost'))");
        await Assertions.Expect(costCard).ToContainTextAsync("$1.50");
        await Assertions.Expect(costCard).ToContainTextAsync("33%");
    }

    // ── Insights: no usage hides cost and token tiles ──────────────────────────

    [Fact]
    public async Task NoUsage_HidesCostAndTokens()
    {
        var now = DateTimeOffset.UtcNow;

        await SeedCompletedRunAsync(NewRunId(), "NOUSAGE-1", now.AddMinutes(-5));

        var insights = new InsightsPage(Page, BaseUrl);
        await insights.NavigateAsync();

        // Assert h1 is visible first to confirm the page has rendered (avoid count-0 false pass)
        await Page.WaitForSelectorAsync("h1");

        Assert.Equal(0, await Page.Locator("h2:text-is('Cost')").CountAsync());
        Assert.Equal(0, await Page.Locator(".cockpit-stat:has-text('Avg tokens / run')").CountAsync());
    }

    // ── Insights: feedback cards group by category and link to the run ─────────

    [Fact]
    public async Task FeedbackCards_GroupByCategory_AndLinkToTheRun()
    {
        var now = DateTimeOffset.UtcNow;
        var runId = NewRunId();

        var feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Failure,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback
            {
                Category = "tools",
                StuckReason = "Tool missing"
            }
        };

        var completed = now - TimeSpan.FromMinutes(1);
#pragma warning disable CS0618
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier("9631"),
            IssueTitle = "Issue 9631",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = now.AddMinutes(-2),
            StartedAt = (now.AddMinutes(-2)).DateTime,
            CompletedAtOffset = completed,
            CompletedAt = completed.DateTime,
            Feedback = feedback,
        });
#pragma warning restore CS0618

        var insights = new InsightsPage(Page, BaseUrl);
        await insights.NavigateAsync();

        // Harness feedback category card
        var harnessCategoryEl = Page.Locator("[data-testid='insights-harness-feedback-category']");
        await Assertions.Expect(harnessCategoryEl).ToContainTextAsync("tools");
        await Assertions.Expect(harnessCategoryEl).ToContainTextAsync("1 run");
        await Assertions.Expect(harnessCategoryEl).ToContainTextAsync("1 failed");

        // Issue feedback empty state
        var issueFeedbackEmpty = Page.Locator("[data-testid='insights-issue-feedback-empty']");
        await Assertions.Expect(issueFeedbackEmpty).ToHaveTextAsync("No issue feedback in this window.");

        // Click summary to expand the <details>, then click the run row
        await Page.Locator("[data-testid='insights-harness-feedback-category'] summary").ClickAsync();

        var navTask = Page.WaitForURLAsync("**/runs/**",
            new() { WaitUntil = WaitUntilState.Commit, Timeout = 15_000 });
        await Page.Locator("[data-testid='insights-harness-feedback-run']").ClickAsync();
        await navTask;

        Assert.EndsWith($"/runs/{runId}", Page.Url);
    }

    // ── Attention: MCP problem shows in section and links to the run ───────────

    [Fact]
    public async Task McpProblem_ShowsInAttention_AndLinksToTheRun()
    {
        var now = DateTimeOffset.UtcNow;
        var runId = NewRunId();

        var phaseBreakdown = new Dictionary<string, PhaseUsage>
        {
            ["analysis"] = new PhaseUsage(Tokens: 0, Cost: null)
            {
                UnavailableMcpServers = new Dictionary<string, string>
                {
                    ["grafana"] = McpServerStatuses.NeedsAuth
                }
            }
        };

        await SeedCompletedRunAsync(
            runId,
            "9641",
            now.AddMinutes(-5),
            mcpServers: ["grafana"],
            phaseBreakdown: phaseBreakdown);

        var attention = new AttentionPage(Page, BaseUrl);
        await attention.NavigateAsync();

        // MCP section count badge
        var mcpCount = Page.Locator("[data-testid='attention-section-count-mcp']");
        await Assertions.Expect(mcpCount).ToHaveTextAsync("1");

        // MCP row content
        var mcpRow = Page.Locator("[data-testid='attention-mcp-row-grafana']");
        await Assertions.Expect(mcpRow).ToContainTextAsync("unavailable (needs-auth)");
        await Assertions.Expect(mcpRow).ToContainTextAsync("1 run(s) in a row");
        await Assertions.Expect(mcpRow).ToContainTextAsync("last: 9641");

        // Click the MCP row to navigate to the run — it uses @onclick (not <a href>)
        var navTask = Page.WaitForURLAsync("**/runs/**",
            new() { WaitUntil = WaitUntilState.Commit, Timeout = 15_000 });
        await mcpRow.ClickAsync();
        await navTask;

        Assert.EndsWith($"/runs/{runId}", Page.Url);
    }

    // ── Attention: MCP server working again leaves the MCP section empty ───────

    [Fact]
    public async Task McpServerWorkingAgain_LeavesAttention()
    {
        var now = DateTimeOffset.UtcNow;

        // Older run: MCP unavailable
        var phaseWithUnavailable = new Dictionary<string, PhaseUsage>
        {
            ["analysis"] = new PhaseUsage(Tokens: 0, Cost: null)
            {
                UnavailableMcpServers = new Dictionary<string, string>
                {
                    ["grafana"] = McpServerStatuses.NeedsAuth
                }
            }
        };

        await SeedCompletedRunAsync(
            NewRunId(),
            "9641",
            now.AddMinutes(-10),
            mcpServers: ["grafana"],
            phaseBreakdown: phaseWithUnavailable);

        // Newer run: MCP working (has tool calls, no unavailable)
        var phaseWithToolCalls = new Dictionary<string, PhaseUsage>
        {
            ["analysis"] = new PhaseUsage(Tokens: 0, Cost: null)
            {
                ToolCalls = new Dictionary<string, McpCallCounts>
                {
                    ["grafana/query"] = new McpCallCounts { Calls = 2 }
                }
            }
        };

        await SeedCompletedRunAsync(
            NewRunId(),
            "9642",
            now.AddMinutes(-2),
            mcpServers: ["grafana"],
            phaseBreakdown: phaseWithToolCalls);

        var attention = new AttentionPage(Page, BaseUrl);
        await attention.NavigateAsync();

        // Assert h1 is visible first to confirm the page has rendered
        await Page.WaitForSelectorAsync("h1");
        await attention.WaitForLoadCompleteAsync();

        // The MCP section should not be rendered when the server is working
        Assert.Equal(0, await Page.Locator("[data-testid='attention-section-mcp']").CountAsync());
    }

    // ── Attention: triage needing review shows in the triage section ───────────

    [Fact]
    public async Task TriageNeedingReview_ShowsInAttention()
    {
        // Adapted from TriageE2ETests.OperatorTriage_ResultIsRecorded_AndADraftBecomesAnIssue
        // up to the poll for TriageStatus.NeedsReview.
        const string agentLabel = "triage-attention-e2e";

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-triage-attention",
            Name = "checkout-api-attention",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-triage-attention",
            DisplayName = "Triage Attention profile",
            JobTemplate = E2ERouting.Key([agentLabel]),
            Enabled = true,
        });

        await using var agent = new FakeAgentClient("agent-triage-attention-e2e", agentLabel);
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        using var api = Fixture.CreateUiApiClient();

        // Create the triage
        var created = await api.PostAsJsonAsync("/api/ui/triages", new CreateTriageRequest
        {
            ProjectId = WellKnownIds.DefaultProjectId,
            RequestedBy = "anna",
            Request = new TriageRequest
            {
                Title = "Checkout returns 502 after the deploy",
                WhatHappened = "One in five checkouts fails with 502",
                Expected = "Checkout completes",
            },
        }, PipelineJsonOptions.Default);
        Assert.Equal(System.Net.HttpStatusCode.OK, created.StatusCode);
        var triage = (await created.Content.ReadFromJsonAsync<TriageDetail>(PipelineJsonOptions.Default))!;

        // Agent receives and processes the triage
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.Investigating);
        await agent.ReportTriageResultAsync(assignment.JobId, CauseFound());
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.ReportingRca);
        await agent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        // Poll until the triage reaches NeedsReview (using WaitUntilAsync since WaitForAsync<T>
        // is on HeadlessE2ETestBase and not available here)
        await WaitUntilAsync(async () =>
        {
            var d = await api.GetFromJsonAsync<TriageDetail>(
                $"/api/ui/triages/{triage.Record.Id}", PipelineJsonOptions.Default);
            return d?.Status == TriageStatus.NeedsReview;
        }, timeout: TimeSpan.FromSeconds(30));

        // Navigate to the Attention page
        var attention = new AttentionPage(Page, BaseUrl);
        await attention.NavigateAsync();

        // Triage section count badge
        var triageCount = Page.Locator("[data-testid='attention-section-count-triage']");
        await Assertions.Expect(triageCount).ToHaveTextAsync("1");

        // Triage row: contains title and subline, and is an <a> tag with href triage/{id}
        var triageRow = Page.Locator("[data-testid='attention-triage-row']");
        await Assertions.Expect(triageRow).ToContainTextAsync("Checkout returns 502 after the deploy");
        await Assertions.Expect(triageRow).ToContainTextAsync("RCA ready — review 1 issue draft");

        var href = await triageRow.GetAttributeAsync("href");
        Assert.Equal($"triage/{triage.Record.Id}", href);
    }

    // ── Helper: triage result with a draft ────────────────────────────────────

    private static TriageResult CauseFound() => new()
    {
        Verdict = TriageVerdict.CauseFound,
        Confidence = TriageConfidence.High,
        Summary = "The retry policy re-sends a non-idempotent POST and exhausts the database pool.",
        Investigated = [new TriageCheck { Check = "5xx rate", Where = "grafana · Prometheus", Result = "jump at 14:03" }],
        Evidence = [new TriageEvidence { Id = "E1", Claim = "Pool wait p99 29.8 s", Source = "grafana" }],
        Drafts =
        [
            new TriageDraft
            {
                Id = "d1",
                Kind = TriageDraftKind.RootFix,
                TargetRepository = "checkout-api-attention",
                Title = "Do not retry the pay POST",
                Body = "Remove the retry from RetryPolicy.cs."
            },
        ],
    };
}
