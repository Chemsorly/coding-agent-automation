using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Run/agent interaction coverage, migrated from the retired /agent-monitoring page to the cockpit
/// pages that replaced it: active + queued work on /work, the live run detail on /runs/{id}, and
/// agent status on /fleet. The dispatch + fake-agent arrange is unchanged; only the UI assertions
/// were retargeted. (The old run-detail modal is now a full page, so the "closes on Escape" modal
/// test was retired — RunPage is navigated to and away from, not opened/closed as an overlay.)
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class MonitoringInteractionTests : E2ETestBase
{
    public MonitoringInteractionTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Seeds a template/profile/issue, dispatches it, then has the connected <paramref name="agent"/>
    /// accept the job and report <paramref name="step"/>. Returns the active run's id once the server
    /// reflects the step. Mirrors the arrange the old monitoring tests repeated inline.
    /// </summary>
    private async Task<string> SeedDispatchAndActivateAsync(
        FakeAgentClient agent, string templateName, string issueId, PipelineStep step = PipelineStep.GeneratingCode)
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = $"Issue {issueId} test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync(templateName);
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueId);
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, step);

        var runService = Fixture.RunService;
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == issueId && r.CurrentStep == step));
        return runService.GetActiveRuns().First(r => r.IssueIdentifier == issueId).RunId;
    }

    [Fact]
    public async Task Work_ActiveRun_ShowsInFlight()
    {
        await using var fakeAgent = new FakeAgentClient("monitor-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await SeedDispatchAndActivateAsync(fakeAgent, "Monitor Template", "70");

        // Assert: the in-flight run is visible on /work.
        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();
        await work.WaitForInFlightAsync("70", timeoutMs: 15_000);
        Assert.True(await work.IsIssueInFlightAsync("70"), "Active run #70 should appear in the Work 'In flight' table");
    }

    [Fact]
    public async Task ActiveRun_RowClick_OpensRunDetailPage()
    {
        await using var fakeAgent = new FakeAgentClient("modal-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var runId = await SeedDispatchAndActivateAsync(fakeAgent, "Modal Template", "71");

        // Act: the Overview "Active runs" card lists runs and navigates to /runs/{id} on click.
        await Page.GotoAsync($"{BaseUrl}/overview");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        var runRow = Page.Locator(".cockpit-run-row").Filter(new() { HasTextString = "#71" });
        await runRow.First.WaitForAsync(new() { Timeout = 15_000 });

        // Click and wait for navigation together to avoid a timing race on slow CI runners.
        await Page.RunAndWaitForNavigationAsync(
            async () => await runRow.First.ClickAsync(),
            new() { UrlString = $"**/runs/{runId}", Timeout = 30_000 });
        var pageText = await Page.TextContentAsync("body");
        Assert.Contains("#71", pageText);
    }

    [Fact]
    public async Task RunDetailPage_ShowsPipelineProgress_ForActiveRun()
    {
        await using var fakeAgent = new FakeAgentClient("detail-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var runId = await SeedDispatchAndActivateAsync(fakeAgent, "Detail Template", "74");

        // The run detail page renders the live "Pipeline progress" card (PipelineSidebar) for active runs.
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);
        await detail.PipelineProgressCard.First.WaitForAsync(new() { Timeout = 15_000 });
        Assert.True(await detail.PipelineProgressCard.CountAsync() > 0, "Run detail page should show the Pipeline progress card for an active run");

        var pageText = await detail.GetPageTextAsync();
        Assert.Contains("#74", pageText);
    }

    [Fact]
    public async Task Fleet_AgentStatus_ShowsBusyDuringJob()
    {
        await using var fakeAgent = new FakeAgentClient("status-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await SeedDispatchAndActivateAsync(fakeAgent, "Status Template", "73");

        // Assert: the agent shows "Busy" on /fleet (poll — the fleet view auto-refreshes on a timer).
        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();
        await fleet.WaitForAgentStatusAsync("status-agent-1", "Busy", timeoutMs: 15_000);
        Assert.True(await fleet.IsAgentVisibleAsync("status-agent-1"), "Busy agent should be visible on Fleet");
    }

    [Fact(Skip = "Requires DB/SignalR mode (pending queue). Legacy mode fails dispatch immediately when no agent is available.")]
    public async Task Work_UnassignedRun_ShowsInQueueOnly_NotInFlight()
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Queue Only Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "80",
            Title = "Unassigned queue test",
            Description = "Test issue for queue-only display",
            Labels = new[] { "enhancement" }
        });

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Queue Only Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("80");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync("div.dispatch-drawer-overlay.open",
            new() { State = WaitForSelectorState.Hidden, Timeout = 30_000 });

        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        // Queued but not in flight (no agent assigned yet).
        Assert.True(await work.IsIssueQueuedAsync("80"), "Issue #80 should appear in the Queue");
        Assert.False(await work.IsIssueInFlightAsync("80"), "Issue #80 should NOT be in flight while unassigned");

        // Connect an agent; the job should move to in flight.
        await using var fakeAgent = new FakeAgentClient("late-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        var runService = Fixture.RunService;
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == "80" && r.AgentId == "late-agent-1"));

        await work.NavigateAsync();
        await work.WaitForInFlightAsync("80", timeoutMs: 15_000);
        Assert.True(await work.IsIssueInFlightAsync("80"), "Issue #80 should move to In flight after an agent picks it up");
    }
}
