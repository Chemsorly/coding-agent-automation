using System.Text.RegularExpressions;
using CodingAgent.Contracts;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the agent states on /fleet that <see cref="FleetViewTests"/> does not cover:
/// Disconnected, Disabled, the expanded row, the empty list and the credential pool banner.
///
/// <para>
/// Each test seeds before it navigates, so the Fleet page needs no refresh. Rows are located by
/// agent id because the table is ordered by status and then id.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class FleetAgentStatesTests : E2ETestBase
{
    private static readonly string[] s_e2eMatchLabels = ["e2e"];
    private static readonly string[] s_enhancementLabels = ["enhancement"];

    private static readonly TimeSpan s_pageTimeout = TimeSpan.FromSeconds(15);

    public FleetAgentStatesTests(E2EFixture fixture) : base(fixture) { }

    // ── Seed helpers ──────────────────────────────────────────────────────

    private async Task<string> SeedAndDispatchAsync(FakeAgentClient agent, string issueId)
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-fleet-states",
            Name = "Fleet States Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-fleet-states",
            DisplayName = "Fleet States E2E Profile",
            JobTemplate = E2ERouting.Key(s_e2eMatchLabels),
            Enabled = true
        });

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = $"Fleet states issue {issueId}",
            Description = "E2E test",
            Labels = s_enhancementLabels,
            Url = $"https://github.com/test/repo/issues/{issueId}"
        });

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Fleet States Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueId);
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        var runService = Fixture.RunService;
        await WaitUntilAsync(() =>
            runService.GetActiveRuns().Any(r => r.IssueIdentifier == issueId && r.CurrentStep == PipelineStep.GeneratingCode));
        return runService.GetActiveRuns().First(r => r.IssueIdentifier == issueId).RunId;
    }

    /// <summary>Seeds a Dispatched work item that holds the given credential (PVC) slot.</summary>
    private async Task SeedClaimedSlotAsync(string issueIdentifier, string claimedPvcName)
    {
        await using var db = await Fixture.DbContextFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Dispatched,
            Payload = "{}",
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeoutSeconds = 3600,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
            ClaimedPvcName = claimedPvcName,
        });
        await db.SaveChangesAsync();
    }

    // ── Locators ──────────────────────────────────────────────────────────

    private ILocator AgentRow(string agentId) => Page.Locator($"tr.monitoring-row-clickable:has-text('{agentId}')");

    /// <summary>
    /// Clicks the agent cell to toggle the row. A click in the middle of the row can land in the
    /// Active work cell, which stops propagation so that its links do not toggle the row.
    /// </summary>
    private Task ToggleRowAsync(string agentId) => AgentRow(agentId).Locator("td").First.ClickAsync();

    private ILocator DetailRow => Page.Locator("tr.fleet-detail-row");

    private ILocator CredentialsTile => Page.Locator(".cockpit-stat:has-text('Kiro Credentials') .cockpit-stat-v");

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisconnectedAgent_ShowsDisconnected_AndItsTimeInTheExpandedRow()
    {
        var agent = new FakeAgentClient("fleet-u06-1", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await agent.DisposeAsync();
        await WaitUntilAsync(() => Fixture.AgentRegistry.GetByAgentId("fleet-u06-1")?.Status == AgentStatus.Disconnected);

        await new FleetPage(Page, BaseUrl).NavigateAsync();

        var status = Page.Locator("tr:has-text('fleet-u06-1') .monitoring-status");
        await Assertions.Expect(status).ToHaveTextAsync("Disconnected");
        await Assertions.Expect(status).ToHaveClassAsync(new Regex("monitoring-status-disconnected"));

        await ToggleRowAsync("fleet-u06-1");
        await Assertions.Expect(DetailRow).ToContainTextAsync("Registered");
        await Assertions.Expect(DetailRow).ToContainTextAsync("Disconnected");

        await ToggleRowAsync("fleet-u06-1");
        await Assertions.Expect(DetailRow).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DisabledAgent_ShowsTheChip()
    {
        await using var disabledAgent = new FakeAgentClient("fleet-u06-2", "e2e");
        await disabledAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await using var enabledAgent = new FakeAgentClient("fleet-u06-3", "e2e");
        await enabledAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        await Fixture.AgentRegistry.UpdateAgentFieldAsync(new AgentId("fleet-u06-2"), AgentFieldNames.Disabled, "true");
        await WaitUntilAsync(() => Fixture.AgentRegistry.GetByAgentId("fleet-u06-2")?.Disabled == true);

        await new FleetPage(Page, BaseUrl).NavigateAsync();

        await Assertions.Expect(AgentRow("fleet-u06-2").Locator(".cockpit-chip:has-text('Disabled')")).ToHaveCountAsync(1);
        await Assertions.Expect(AgentRow("fleet-u06-3")).ToBeVisibleAsync();
        await Assertions.Expect(AgentRow("fleet-u06-3").Locator(".cockpit-chip:has-text('Disabled')")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task BusyAgent_ExpandedRow_ShowsBusySinceAndActiveRun()
    {
        await using var agent = new FakeAgentClient("fleet-u06-4", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var runId = await SeedAndDispatchAsync(agent, "9514");

        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();
        await fleet.WaitForAgentStatusAsync("fleet-u06-4", "Busy", timeoutMs: 15_000);

        await ToggleRowAsync("fleet-u06-4");

        await Assertions.Expect(DetailRow).ToContainTextAsync("Busy since");
        await Assertions.Expect(DetailRow).ToContainTextAsync("Active issue");
        await Assertions.Expect(DetailRow).ToContainTextAsync("Active run");
        await Assertions.Expect(DetailRow.Locator("a")).ToHaveAttributeAsync("href", $"/runs/{runId}");
    }

    [Fact]
    public async Task NoAgents_ShowsTheEmptyText()
    {
        await new FleetPage(Page, BaseUrl).NavigateAsync();

        await Assertions.Expect(Page.Locator(".cockpit-empty"))
            .ToHaveTextAsync("No agents registered.", new() { Timeout = (float)s_pageTimeout.TotalMilliseconds });
    }

    [Fact]
    public async Task AllCredentialSlotsClaimed_ShowsTheBanner()
    {
        await SeedClaimedSlotAsync("9515", "fake-pvc-0");
        await SeedClaimedSlotAsync("9516", "fake-pvc-1");

        await new FleetPage(Page, BaseUrl).NavigateAsync();

        await Assertions.Expect(Page.Locator("body"))
            .ToContainTextAsync("Credential pool exhausted — all 2 slots are claimed");
        await Assertions.Expect(CredentialsTile).ToHaveTextAsync("0/2");
    }

    [Fact]
    public async Task OneCredentialSlotFree_ShowsNoBanner()
    {
        await SeedClaimedSlotAsync("9515", "fake-pvc-0");

        await new FleetPage(Page, BaseUrl).NavigateAsync();

        // The tile proves the pool has loaded before the absence of the banner is asserted.
        await Assertions.Expect(CredentialsTile).ToHaveTextAsync("1/2");
        await Assertions.Expect(Page.GetByText("Credential pool exhausted")).ToHaveCountAsync(0);
    }
}
