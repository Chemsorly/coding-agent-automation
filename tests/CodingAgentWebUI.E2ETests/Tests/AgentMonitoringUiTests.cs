using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// UI behavioral tests migrated from the retired /agent-monitoring page to the cockpit pages that
/// replaced it: agent presence on /fleet, live run progress on /runs/{id}, and run history on /runs.
/// Uses Playwright to observe DOM updates driven by SignalR state notifications.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "UI")]
[Collection(E2ECollection.Name)]
public sealed class AgentMonitoringUiTests : E2ETestBase
{
    public AgentMonitoringUiTests(E2EFixture fixture) : base(fixture) { }

    private async Task SeedAndConnectAsync(string issueId, string agentId, string[] labels)
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = $"UI test issue {issueId}",
            Description = "## Requirements\nUI test\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-ui-monitor",
            Name = "UI Monitor Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-ui-monitor",
            DisplayName = "UI Monitor Profile",
            MatchLabels = labels,
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Agent appears on Fleet with correct status
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Fleet_AgentAppearsAfterConnection()
    {
        // Connect a fake agent
        await using var agent = new FakeAgentClient("ui-monitor-agent-1", "ui-test");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // The agent registered on the API's hub; the Blazor host reads a snapshot of that registry
        // refreshed on a poll that is disabled in the harness. Pull it forward so the page renders
        // the agent on first load.
        await Fixture.ForceAgentRegistryRefreshAsync();

        // Navigate to the Fleet page
        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();

        // Assert: the agent appears on Fleet.
        await Page.GetByText("ui-monitor-agent-1").First.WaitForAsync(new() { Timeout = 15_000 });
        await Assertions.Expect(Page.GetByText("ui-monitor-agent-1").First).ToBeVisibleAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Run progress — step transitions visible on the run detail page
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunDetailPage_RunProgress_StepTransitionsVisible()
    {
        // Arrange
        await SeedAndConnectAsync("UI-200", "ui-progress-agent", new[] { "ui-test" });
        await using var agent = new FakeAgentClient("ui-progress-agent", "ui-test");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Navigate to coding page to trigger dispatch
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Select template and dispatch
        await codingPage.SelectTemplateAsync("UI Monitor Template");
        await codingPage.ClickBrowseIssuesAsync();
        await Page.WaitForTimeoutAsync(2000);

        // Look for the issue in the drawer and dispatch it
        var issueItem = Page.Locator("[data-testid='issue-item']").First;
        var dispatchBtn = issueItem.Locator("button:has-text('Dispatch')");

        if (await dispatchBtn.CountAsync() > 0)
        {
            await dispatchBtn.ClickAsync();
            await Page.WaitForTimeoutAsync(2000);

            // Agent receives job
            var job = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Report step transitions
            await agent.AcceptJobAsync(job.JobId);
            await agent.ReportStepAsync(job.JobId, PipelineStep.CloningRepository);
            await agent.ReportStepAsync(job.JobId, PipelineStep.GeneratingCode,
                new Dictionary<string, string> { ["BranchName"] = "feature/ui-test" });

            // Wait for the server to reflect the active run, then open its detail page.
            var runService = Fixture.RunService;
            await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == "UI-200"));
            var runId = runService.GetActiveRuns().First(r => r.IssueIdentifier == "UI-200").RunId;

            var detail = new RunDetailPage(Page, BaseUrl);
            await detail.NavigateAsync(runId);

            // Assert: run progress is visible on the detail page (issue id or a live step name).
            var bodyText = await detail.GetPageTextAsync();
            Assert.True(
                bodyText?.Contains("UI-200") == true || bodyText?.Contains("Generating") == true || bodyText?.Contains("Cloning") == true,
                "Run progress should be visible on the run detail page during active execution");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // History — completed runs appear on the Runs page
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Runs_CompletedRun_AppearsInHistory()
    {
        // Arrange: seed and dispatch
        await SeedAndConnectAsync("UI-300", "ui-history-agent", new[] { "ui-test" });
        await using var agent = new FakeAgentClient("ui-history-agent", "ui-test");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("UI Monitor Template");
        await codingPage.ClickBrowseIssuesAsync();
        await Page.WaitForTimeoutAsync(2000);

        var issueItem = Page.Locator("[data-testid='issue-item']").First;
        var dispatchBtn = issueItem.Locator("button:has-text('Dispatch')");

        if (await dispatchBtn.CountAsync() > 0)
        {
            await dispatchBtn.ClickAsync();
            await Page.WaitForTimeoutAsync(2000);

            // Agent completes the job
            var job = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await agent.AcceptAndCompleteJobAsync(job.JobId);

            // Wait for history to update
            await WaitForHistoryAsync(
                r => r.IssueIdentifier == "UI-300" && r.FinalStep == PipelineStep.Completed,
                TimeSpan.FromSeconds(15));

            // Navigate to the Runs history page and confirm the completed run is listed.
            await Page.GotoAsync($"{BaseUrl}/runs");
            await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
            await Page.WaitForTimeoutAsync(2000);

            var bodyText = await Page.TextContentAsync("body");
            Assert.True(
                bodyText?.Contains("UI-300") == true,
                "Completed run should appear on the Runs history page");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Notification toast — dispatch feedback appears (coding page — unchanged)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CodingPage_DispatchSuccess_ShowsFeedbackMessage()
    {
        // Arrange
        await SeedAndConnectAsync("UI-400", "ui-toast-agent", new[] { "ui-test" });
        await using var agent = new FakeAgentClient("ui-toast-agent", "ui-test");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("UI Monitor Template");
        await codingPage.ClickBrowseIssuesAsync();
        await Page.WaitForTimeoutAsync(2000);

        // Dispatch
        var issueItem = Page.Locator("[data-testid='issue-item']").First;
        var dispatchBtn = issueItem.Locator("button:has-text('Dispatch')");

        if (await dispatchBtn.CountAsync() > 0)
        {
            await dispatchBtn.ClickAsync();
            await Page.WaitForTimeoutAsync(2000);

            // Assert: success feedback message appears (toast or inline)
            var bodyText = await Page.TextContentAsync("body");
            Assert.True(
                bodyText?.Contains("Dispatched") == true || bodyText?.Contains("✅") == true,
                "Dispatch success feedback should appear in the UI");
        }
    }
}
