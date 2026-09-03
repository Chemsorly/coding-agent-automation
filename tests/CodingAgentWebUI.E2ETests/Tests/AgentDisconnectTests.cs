using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Agent disconnect and run-cancellation coverage. Migrated from the retired /agent-monitoring page:
/// cancellation now happens from /work (the in-flight table's Cancel button) and agent presence is
/// asserted on /fleet. The disconnect test asserts registry state directly and is page-independent.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class AgentDisconnectTests : E2ETestBase
{
    public AgentDisconnectTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Agent_DisconnectsMidRun_AgentMarkedDisconnected()
    {
        // Arrange: seed template, issue, profile, and connect an agent
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
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
            Identifier = "60",
            Title = "Disconnect test issue",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        var fakeAgent = new FakeAgentClient("disconnect-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: dispatch the issue
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("60");
        await codingPage.ClickStartPipelineAsync();

        // Wait for dispatch and job assignment
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        // Wait for the run to be active with expected step
        var runService = Fixture.RunService;
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == "60" && r.CurrentStep == PipelineStep.GeneratingCode));

        // Simulate agent disconnect by disposing the connection
        await fakeAgent.DisposeAsync();

        // Wait for hub to process disconnect and mark agent as Disconnected.
        // Agents connect to the API hub (Spec 044), so their registry entry lives on the API host
        // — use Fixture.AgentRegistry, not Fixture.Factory.Services (the Blazor host's local registry).
        var registry = Fixture.AgentRegistry;
        await WaitUntilAsync(() => registry.GetByAgentId("disconnect-agent-1")?.Status == AgentStatus.Disconnected);

        // Assert: agent is marked as disconnected in the registry
        var agent = registry.GetByAgentId("disconnect-agent-1");
        Assert.NotNull(agent);
        Assert.Equal(AgentStatus.Disconnected, agent.Status);
    }

    [Fact]
    public async Task Cancel_ActiveRun_FromWorkPage()
    {
        // Arrange: seed template, issue, profile, and connect an agent
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
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
            Identifier = "61",
            Title = "Cancel from work test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await using var fakeAgent = new FakeAgentClient("cancel-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Dispatch the issue
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("61");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        // Wait for the run to be active with expected step
        var runService = Fixture.RunService;
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == "61" && r.CurrentStep == PipelineStep.GeneratingCode));

        // Act: cancel the in-flight run from /work. This posts a Cancelled status transition
        // (RunLifecycleManager.CancelRunAsync), driving the run to a terminal state server-side.
        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();
        await work.WaitForInFlightAsync("61", timeoutMs: 15_000);
        await work.CancelInFlightAsync("61");

        // Assert: the run leaves the active set once the cancellation is applied.
        await WaitUntilAsync(() => !runService.GetActiveRuns().Any(r => r.IssueIdentifier == "61"));
        Assert.DoesNotContain(runService.GetActiveRuns(), r => r.IssueIdentifier == "61");
    }

    [Fact]
    public async Task Fleet_ConnectedAgent_ShowsIdle()
    {
        // Arrange: connect an agent
        await using var fakeAgent = new FakeAgentClient("idle-agent", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Verify agent is registered on the API host's registry (Spec 044).
        var registry = Fixture.AgentRegistry;
        var agentBefore = registry.GetByAgentId("idle-agent");
        Assert.NotNull(agentBefore);
        Assert.NotEqual(AgentStatus.Disconnected, agentBefore.Status);

        // Force the Blazor host's ApiAgentRegistryService to refresh its snapshot so the UI
        // picks up the connected agent — the AgentRegistrySyncService poller is disabled in the E2E harness.
        await Fixture.ForceAgentRegistryRefreshAsync();

        // Act + Assert: the idle agent is visible with an Idle status on /fleet.
        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();
        Assert.True(await fleet.IsAgentVisibleAsync("idle-agent"), "Agent should be visible on Fleet");
        await fleet.WaitForAgentStatusAsync("idle-agent", "Idle", timeoutMs: 15_000);
    }
}
