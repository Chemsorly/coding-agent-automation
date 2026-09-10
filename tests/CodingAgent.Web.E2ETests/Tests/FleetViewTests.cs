using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for Fleet view changes introduced in issue #2337:
/// - Busy/Idle/Utilization stat tiles removed
/// - Active work column shows issue, run, and PR links
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class FleetViewTests : E2ETestBase
{
    public FleetViewTests(E2EFixture fixture) : base(fixture) { }

    private async Task<string> SeedAndDispatchAsync(
        FakeAgentClient agent, string templateName, string issueId,
        PipelineStep step = PipelineStep.GeneratingCode)
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-fleet",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-fleet",
            DisplayName = "Fleet E2E Profile",
            MatchLabels = ["e2e"],
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = $"Fleet test issue {issueId}",
            Description = "E2E test",
            Labels = ["enhancement"],
            Url = $"https://github.com/test/repo/issues/{issueId}"
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

    // ── Tile removal ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Fleet_BusyIdleUtilizationTiles_AreAbsent()
    {
        // Even with no agents connected, the stat strip must not contain the removed tiles.
        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();

        Assert.False(await fleet.IsStatTilePresentAsync("Busy"),
            "Busy tile must be removed from Fleet view");
        Assert.False(await fleet.IsStatTilePresentAsync("Idle"),
            "Idle tile must be removed from Fleet view");
        Assert.False(await fleet.IsStatTilePresentAsync("Utilization"),
            "Utilization tile must be removed from Fleet view");
    }

    [Fact]
    public async Task Fleet_AgentsTileAndKiroCredentialsTile_ArePresent()
    {
        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();

        Assert.True(await fleet.IsStatTilePresentAsync("Agents"),
            "Agents tile must remain on Fleet view after tile removal");
    }

    // ── Active work links ──────────────────────────────────────────────────────

    [Fact]
    public async Task Fleet_BusyAgent_ShowsIssueLinkAndRunLink()
    {
        await using var agent = new FakeAgentClient("fleet-link-agent-1", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var runId = await SeedAndDispatchAsync(agent, "Fleet Link Template", "2337-link");

        await Fixture.ForceAgentRegistryRefreshAsync();

        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();
        await fleet.WaitForAgentStatusAsync("fleet-link-agent-1", "Busy", timeoutMs: 15_000);

        var issueLink = await fleet.GetActiveIssueLinkAsync("fleet-link-agent-1");
        Assert.NotNull(issueLink);
        Assert.Contains("2337-link", issueLink);

        var runLink = await fleet.GetActiveRunLinkAsync("fleet-link-agent-1");
        Assert.NotNull(runLink);
        Assert.Contains(runId, runLink);
    }

    [Fact]
    public async Task Fleet_IdleAgent_ShowsDash_NoIssueLink()
    {
        await using var agent = new FakeAgentClient("fleet-idle-agent-1", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        await Fixture.ForceAgentRegistryRefreshAsync();

        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();
        await fleet.WaitForAgentStatusAsync("fleet-idle-agent-1", "Idle", timeoutMs: 15_000);

        // Idle agents must not show issue or run links.
        var issueLink = await fleet.GetActiveIssueLinkAsync("fleet-idle-agent-1");
        Assert.Null(issueLink);
        var runLink = await fleet.GetActiveRunLinkAsync("fleet-idle-agent-1");
        Assert.Null(runLink);
    }

    [Fact]
    public async Task Fleet_BusyAgent_WithPr_ShowsPrLink()
    {
        await using var agent = new FakeAgentClient("fleet-pr-agent-1", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var runId = await SeedAndDispatchAsync(agent, "Fleet PR Template", "2337-pr");

        // Simulate the agent reporting a PR URL via step transition metadata.
        await agent.ReportStepAsync(runId,
            PipelineStep.CreatingPullRequest,
            new Dictionary<string, string>
            {
                ["PullRequestUrl"] = "https://github.com/test/repo/pull/42"
            });

        var runService = Fixture.RunService;
        await WaitUntilAsync(() =>
        {
            var run = runService.GetActiveRuns().FirstOrDefault(r => r.IssueIdentifier == "2337-pr");
            return run?.PullRequestUrl != null;
        });

        await Fixture.ForceAgentRegistryRefreshAsync();

        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();
        await fleet.WaitForAgentStatusAsync("fleet-pr-agent-1", "Busy", timeoutMs: 15_000);

        var prLink = await fleet.GetActivePrLinkAsync("fleet-pr-agent-1");
        Assert.NotNull(prLink);
        Assert.Contains("pull/42", prLink);
    }
}
