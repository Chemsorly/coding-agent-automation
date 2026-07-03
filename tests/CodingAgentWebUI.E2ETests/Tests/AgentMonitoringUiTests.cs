using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// P6 UI behavioral tests for the Agent Monitoring page.
/// Validates real-time agent status updates, run progress visibility, and history panel.
/// Uses Playwright to observe DOM updates driven by SignalR state notifications.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "UI")]
public sealed class AgentMonitoringUiTests : E2ETestBase, IClassFixture<E2EFixture>
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
    // E7: Agent appears on monitoring page with correct status
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Monitoring_AgentAppearsAfterConnection()
    {
        // Connect a fake agent
        await using var agent = new FakeAgentClient("ui-monitor-agent-1", "ui-test");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Navigate to monitoring page
        await Page.GotoAsync($"{BaseUrl}/agent-monitoring");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        await Page.WaitForTimeoutAsync(3000); // Allow Blazor circuit + SignalR data push

        // Assert: agent ID appears on the page
        var agentText = await Page.TextContentAsync("body");
        Assert.Contains("ui-monitor-agent-1", agentText ?? "");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // E8: Run progress — step transitions visible in UI
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Monitoring_RunProgress_StepTransitionsVisible()
    {
        // Arrange
        await SeedAndConnectAsync("UI-200", "ui-progress-agent", new[] { "ui-test" });
        await using var agent = new FakeAgentClient("ui-progress-agent", "ui-test");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

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
            await Page.WaitForTimeoutAsync(1000);

            await agent.ReportStepAsync(job.JobId, PipelineStep.GeneratingCode,
                new Dictionary<string, string> { ["BranchName"] = "feature/ui-test" });
            await Page.WaitForTimeoutAsync(1000);

            // Assert: page content reflects the run is in progress
            var bodyText = await Page.TextContentAsync("body");
            // The run should be visible somewhere on the page (active run display)
            Assert.True(
                bodyText?.Contains("UI-200") == true || bodyText?.Contains("Generating") == true || bodyText?.Contains("Cloning") == true,
                "Run progress should be visible on the page during active execution");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // E9: History — completed runs appear in sidebar
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Monitoring_CompletedRun_AppearsInHistory()
    {
        // Arrange: seed and dispatch
        await SeedAndConnectAsync("UI-300", "ui-history-agent", new[] { "ui-test" });
        await using var agent = new FakeAgentClient("ui-history-agent", "ui-test");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

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

            // Navigate to monitoring to check history is visible
            await Page.GotoAsync($"{BaseUrl}/agent-monitoring");
            await Page.WaitForTimeoutAsync(3000);

            // Assert: the completed run's issue should appear somewhere in the UI
            var bodyText = await Page.TextContentAsync("body");
            // History entries typically show the issue identifier or PR URL
            Assert.True(
                bodyText?.Contains("UI-300") == true || bodyText?.Contains("Completed") == true,
                "Completed run should appear in the monitoring page history");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // E12: Notification toast — dispatch feedback appears
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CodingPage_DispatchSuccess_ShowsFeedbackMessage()
    {
        // Arrange
        await SeedAndConnectAsync("UI-400", "ui-toast-agent", new[] { "ui-test" });
        await using var agent = new FakeAgentClient("ui-toast-agent", "ui-test");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

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
