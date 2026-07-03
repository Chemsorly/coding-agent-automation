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
/// Tests that validate monitoring page interactions: run detail modal, queue management,
/// and agent status display.
/// </summary>
[Trait("Category", "E2E")]
public sealed class MonitoringInteractionTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public MonitoringInteractionTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Monitoring_ActiveRun_ShowsInTable()
    {
        // Arrange: seed template, issue, profile, and connect an agent
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Monitor Template",
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
            Identifier = "70",
            Title = "Monitoring active run test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await using var fakeAgent = new FakeAgentClient("monitor-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Dispatch the issue
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Monitor Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("70");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        // Wait for the run to reflect the step in server state
        var runService = Fixture.Factory.Services.GetRequiredService<IOrchestratorRunService>();
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == "70" && r.CurrentStep == PipelineStep.GeneratingCode));

        // Act: navigate to monitoring page
        var monitoringPage = new AgentMonitoringPage(Page, BaseUrl);
        await monitoringPage.NavigateAsync();

        // Assert: active run is visible in the table
        var activeRunsHeader = await Page.TextContentAsync("h2:has-text('Active Runs')");
        Assert.NotNull(activeRunsHeader);
        Assert.DoesNotContain("(0)", activeRunsHeader); // Should have at least 1 active run

        // Verify the issue identifier is shown
        var issueCell = await Page.QuerySelectorAsync("td:has-text('#70')");
        Assert.NotNull(issueCell);
    }

    [Fact]
    public async Task Monitoring_RunDetailModal_OpensOnRowClick()
    {
        // Arrange: seed template, issue, profile, and connect an agent
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Modal Template",
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
            Identifier = "71",
            Title = "Modal test issue",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await using var fakeAgent = new FakeAgentClient("modal-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Dispatch and get the run active
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Modal Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("71");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        // Wait for the run to reflect the step in server state
        var runService = Fixture.Factory.Services.GetRequiredService<IOrchestratorRunService>();
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == "71" && r.CurrentStep == PipelineStep.GeneratingCode));

        // Act: navigate to monitoring and click the active run row
        var monitoringPage = new AgentMonitoringPage(Page, BaseUrl);
        await monitoringPage.NavigateAsync();

        // Wait for the clickable row to appear (ARM runners can be slow to render)
        await Page.WaitForSelectorAsync("tr.monitoring-row-clickable", new() { Timeout = 15_000 });
        await Page.ClickAsync("tr.monitoring-row-clickable");

        // Wait for modal to open
        await Page.WaitForSelectorAsync(".modal-overlay", new() { Timeout = 5_000 });

        // Assert: modal is open
        var modal = Page.Locator(".modal-overlay");
        var modalCount = await modal.CountAsync();
        Assert.True(modalCount > 0, "Run detail modal should open when clicking an active run row");

        // Verify modal contains issue info
        var modalText = await Page.TextContentAsync(".modal-card");
        Assert.Contains("#71", modalText);
    }

    [Fact]
    public async Task Monitoring_RunDetailModal_ClosesOnEscape()
    {
        // Arrange: same setup as above — get an active run
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Escape Template",
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
            Identifier = "72",
            Title = "Escape modal test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await using var fakeAgent = new FakeAgentClient("escape-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Escape Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("72");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        // Wait for the run to reflect the step in server state
        var runService = Fixture.Factory.Services.GetRequiredService<IOrchestratorRunService>();
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == "72" && r.CurrentStep == PipelineStep.GeneratingCode));

        // Navigate to monitoring and open modal
        var monitoringPage = new AgentMonitoringPage(Page, BaseUrl);
        await monitoringPage.NavigateAsync();
        await Page.WaitForSelectorAsync("tr.monitoring-row-clickable", new() { Timeout = 15_000 });
        await Page.ClickAsync("tr.monitoring-row-clickable");
        await Page.WaitForSelectorAsync(".modal-overlay", new() { Timeout = 5_000 });

        // Act: focus the modal overlay (tabindex="-1" makes it focusable) then press Escape
        await Page.FocusAsync(".modal-overlay");
        await Page.Keyboard.PressAsync("Escape");

        // Wait for modal to close
        await Page.WaitForSelectorAsync(".modal-overlay", new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        // Assert: modal is closed
        var modalCount = await Page.Locator(".modal-overlay").CountAsync();
        Assert.Equal(0, modalCount);
    }

    [Fact]
    public async Task Monitoring_AgentStatus_ShowsBusyDuringJob()
    {
        // Arrange: connect an agent and give it a job
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Status Template",
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
            Identifier = "73",
            Title = "Status test issue",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await using var fakeAgent = new FakeAgentClient("status-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Dispatch and accept job
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Status Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("73");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        // Wait for the run to reflect the step in server state
        var runService = Fixture.Factory.Services.GetRequiredService<IOrchestratorRunService>();
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == "73" && r.CurrentStep == PipelineStep.GeneratingCode));

        // Act: navigate to monitoring
        var monitoringPage = new AgentMonitoringPage(Page, BaseUrl);
        await monitoringPage.NavigateAsync();

        // Assert: agent shows "Busy" status (poll DOM until rendered — timer-driven refresh)
        await monitoringPage.WaitForAgentStatusAsync("status-agent-1", "Busy", timeoutMs: 15_000);
    }

    [Fact]
    public async Task Monitoring_UnassignedRun_ShowsInQueueOnly_NotActiveRuns()
    {
        // Arrange: seed template, issue, profile — but do NOT connect any agent
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

        // Act: dispatch the issue (no agent available → queued)
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Queue Only Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("80");
        await codingPage.ClickStartPipelineAsync();

        // Wait for dispatch to complete (queued result is still "success" in the UI)
        await Page.WaitForSelectorAsync(".settings-status.status-success, .inline-status-success", new() { Timeout = 30_000 });

        // Navigate to monitoring page
        var monitoringPage = new AgentMonitoringPage(Page, BaseUrl);
        await monitoringPage.NavigateAsync();

        // Assert: Active Runs should be 0 (no agent assigned)
        var activeRunCount = await monitoringPage.GetActiveRunCountAsync();
        Assert.Equal(0, activeRunCount);

        // Assert: Job Queue should contain the issue
        var jobQueueText = await Page.TextContentAsync("h2:has-text('Job Queue')");
        Assert.NotNull(jobQueueText);
        Assert.DoesNotContain("(0)", jobQueueText);

        // Assert: issue #80 is NOT in Active Runs section
        var activeRunsSection = await Page.QuerySelectorAsync("section:has(h2:has-text('Active Runs'))");
        Assert.NotNull(activeRunsSection);
        var activeRunsHtml = await activeRunsSection.InnerHTMLAsync();
        Assert.DoesNotContain("#80", activeRunsHtml);

        // Assert: issue #80 IS in Job Queue section
        var jobQueueSection = await Page.QuerySelectorAsync("section:has(h2:has-text('Job Queue'))");
        Assert.NotNull(jobQueueSection);
        var jobQueueHtml = await jobQueueSection.InnerHTMLAsync();
        Assert.Contains("#80", jobQueueHtml);

        // Now connect an agent and verify the job moves to Active Runs
        await using var fakeAgent = new FakeAgentClient("late-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Wait for the agent to receive and accept the job
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        // Wait for server state to reflect
        var runService = Fixture.Factory.Services.GetRequiredService<IOrchestratorRunService>();
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r =>
            r.IssueIdentifier == "80" && r.AgentId == "late-agent-1"));

        // Refresh monitoring page (wait for 2s refresh cycle)
        await Page.WaitForTimeoutAsync(3000);

        // Assert: Active Runs now shows the issue
        var updatedActiveRunCount = await monitoringPage.GetActiveRunCountAsync();
        Assert.True(updatedActiveRunCount >= 1,
            $"Expected at least 1 active run after agent connected, got {updatedActiveRunCount}");

        var updatedActiveSection = await Page.QuerySelectorAsync("section:has(h2:has-text('Active Runs'))");
        var updatedActiveHtml = await updatedActiveSection!.InnerHTMLAsync();
        Assert.Contains("#80", updatedActiveHtml);
    }
}
