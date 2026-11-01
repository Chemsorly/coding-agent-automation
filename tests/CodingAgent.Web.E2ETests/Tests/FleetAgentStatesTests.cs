using CodingAgent.Contracts;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for Fleet agent states not covered by <see cref="FleetViewTests"/>:
/// Disconnected, Disabled, expanded busy row, empty state, and credential pool exhaustion.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class FleetAgentStatesTests : E2ETestBase
{
    public FleetAgentStatesTests(E2EFixture fixture) : base(fixture) { }

    private static readonly string[] s_e2eMatchLabels = ["e2e"];
    private static readonly string[] s_enhancementLabels = ["enhancement"];

    // ── Seed helper (mirrors FleetViewTests.SeedAndDispatchAsync) ─────────────

    /// <summary>
    /// Seeds the standard template, profile, and issue, then dispatches the issue via the UI
    /// and activates the agent job at <paramref name="step"/>.
    /// Returns the run id once the server reflects the active step.
    /// </summary>
    private async Task<string> SeedAndDispatchAsync(
        FakeAgentClient agent, string templateName, string issueId,
        PipelineStep step = PipelineStep.GeneratingCode)
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-fleet-states",
            Name = templateName,
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
            Title = $"Fleet states test issue {issueId}",
            Description = "E2E test",
            Labels = s_enhancementLabels,
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
        await WaitUntilAsync(() =>
            runService.GetActiveRuns().Any(r => r.IssueIdentifier == issueId && r.CurrentStep == step));

        return runService.GetActiveRuns().First(r => r.IssueIdentifier == issueId).RunId;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A disconnected agent shows the "Disconnected" status badge with the
    /// <c>monitoring-status-disconnected</c> CSS class. Clicking the row expands a detail row
    /// that shows "Registered" and "Disconnected" labels; clicking again collapses it.
    /// </summary>
    [Fact]
    public async Task DisconnectedAgent_ShowsDisconnected_AndItsTimeInTheExpandedRow()
    {
        // Arrange: connect then immediately disconnect.
        // The nested await using scope ensures DisposeAsync is called even if ConnectAsync throws,
        // preventing a leaked SignalR/WebSocket connection resource.
        {
            await using var agent = new FakeAgentClient("fleet-u06-1", "e2e");
            await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        } // DisposeAsync called here, triggering the disconnect

        // Wait for the registry to reflect the disconnection (API host's registry)
        await WaitUntilAsync(() =>
            Fixture.AgentRegistry.GetByAgentId("fleet-u06-1")?.Status == AgentStatus.Disconnected);

        // Act: navigate to /fleet and wait for the agent row to show "Disconnected"
        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();
        await fleet.WaitForAgentStatusAsync("fleet-u06-1", "Disconnected", timeoutMs: 15_000);

        // Assert: status text and CSS class
        var statusLocator = Page.Locator("tr:has-text('fleet-u06-1') .monitoring-status");
        await Assertions.Expect(statusLocator).ToHaveTextAsync("Disconnected", new() { Timeout = 5_000 });

        // Wait for the disconnected CSS class to be applied
        await Page.Locator("tr:has-text('fleet-u06-1') .monitoring-status.monitoring-status-disconnected")
            .WaitForAsync(new() { Timeout = 5_000 });

        // Act: click the row to expand the detail
        await Page.Locator("tr.monitoring-row-clickable")
            .Filter(new() { HasTextString = "fleet-u06-1" })
            .ClickAsync();

        // Assert: detail row appears and contains "Registered" and "Disconnected" labels
        var detailRow = Page.Locator("tr.fleet-detail-row");
        await Assertions.Expect(detailRow).ToBeVisibleAsync(new() { Timeout = 5_000 });
        await Assertions.Expect(detailRow).ToContainTextAsync("Registered", new() { Timeout = 5_000 });
        await Assertions.Expect(detailRow).ToContainTextAsync("Disconnected", new() { Timeout = 5_000 });

        // Act: click the row again to collapse the detail
        await Page.Locator("tr.monitoring-row-clickable")
            .Filter(new() { HasTextString = "fleet-u06-1" })
            .ClickAsync();

        // Assert: detail row is gone
        await Assertions.Expect(Page.Locator("tr.fleet-detail-row"))
            .ToHaveCountAsync(0, new() { Timeout = 5_000 });
    }

    /// <summary>
    /// A disabled agent shows a "Disabled" chip; an enabled agent does not.
    /// </summary>
    [Fact]
    public async Task DisabledAgent_ShowsTheChip()
    {
        // Connect two agents and keep them alive
        await using var agent2 = new FakeAgentClient("fleet-u06-2", "e2e");
        await agent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        await using var agent3 = new FakeAgentClient("fleet-u06-3", "e2e");
        await agent3.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Disable agent2; leave agent3 enabled
        // Signature: UpdateAgentFieldAsync(AgentId agentId, string field, string? value)
        // AgentId has an implicit operator from string, so new AgentId(...) is used for clarity
        await Fixture.AgentRegistry.UpdateAgentFieldAsync(
            new AgentId("fleet-u06-2"), AgentFieldNames.Disabled, "true");

        // Act: navigate to /fleet and wait for both agents to appear
        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();
        await fleet.WaitForAgentStatusAsync("fleet-u06-2", "Idle", timeoutMs: 15_000);
        await fleet.WaitForAgentStatusAsync("fleet-u06-3", "Idle", timeoutMs: 5_000);

        // Assert: agent2 row has a "Disabled" chip
        await Assertions.Expect(
            Page.Locator("tr:has-text('fleet-u06-2') .cockpit-chip:has-text('Disabled')"))
            .ToBeVisibleAsync(new() { Timeout = 5_000 });

        // Assert: agent3 row has no "Disabled" chip
        await Assertions.Expect(
            Page.Locator("tr:has-text('fleet-u06-3') .cockpit-chip:has-text('Disabled')"))
            .ToHaveCountAsync(0, new() { Timeout = 5_000 });
    }

    /// <summary>
    /// Expanding the row of a busy agent shows "Busy since", "Active issue", and "Active run"
    /// detail labels; the run link has the correct /runs/{runId} href.
    /// </summary>
    [Fact]
    public async Task BusyAgent_ExpandedRow_ShowsBusySinceAndActiveRun()
    {
        // Connect the agent before dispatch so FakeJobController can find it
        await using var agent = new FakeAgentClient("fleet-u06-4", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var runId = await SeedAndDispatchAsync(agent, "Fleet States Busy Template", "fleet-busy-4001");

        // Act: navigate to /fleet and wait for the busy state
        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();
        await fleet.WaitForAgentStatusAsync("fleet-u06-4", "Busy", timeoutMs: 15_000);

        // Act: click the row to expand the detail
        await Page.Locator("tr.monitoring-row-clickable")
            .Filter(new() { HasTextString = "fleet-u06-4" })
            .ClickAsync();

        // Assert: detail row shows expected labels
        var detailRow = Page.Locator("tr.fleet-detail-row");
        await Assertions.Expect(detailRow).ToBeVisibleAsync(new() { Timeout = 5_000 });
        await Assertions.Expect(detailRow).ToContainTextAsync("Busy since", new() { Timeout = 5_000 });
        await Assertions.Expect(detailRow).ToContainTextAsync("Active issue", new() { Timeout = 5_000 });
        await Assertions.Expect(detailRow).ToContainTextAsync("Active run", new() { Timeout = 5_000 });

        // Assert: the run link points to /runs/{runId}
        await detailRow.Locator($"a[href='/runs/{runId}']").WaitForAsync(new() { Timeout = 5_000 });
    }

    /// <summary>
    /// When no agents are registered, the fleet page shows the empty-state message.
    /// </summary>
    [Fact]
    public async Task NoAgents_ShowsTheEmptyText()
    {
        // Do not connect any agent

        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();

        await Assertions.Expect(Page.Locator(".cockpit-empty"))
            .ToHaveTextAsync("No agents registered.", new() { Timeout = 15_000 });
    }

    /// <summary>
    /// When all credential slots are claimed, the Fleet page shows the exhaustion banner and
    /// the Kiro Credentials tile shows "0/2".
    ///
    /// Mutation check: changing <c>if (_pool is { Total: > 0, Available: 0 })</c> in Fleet.razor
    /// to <c>if (false)</c> makes this test fail.
    /// </summary>
    [Fact]
    public async Task AllCredentialSlotsClaimed_ShowsTheBanner()
    {
        // Remove any WorkItems with ClaimedPvcName left by previous tests.
        // ResetAll() does not truncate WorkItem rows; DispatchLifecycleService.QueryAvailablePvcsAsync
        // queries ALL Dispatched/Running WorkItems with a non-null ClaimedPvcName, so stale rows
        // from a prior test would corrupt the available-count calculation.
        {
            await using var cleanupDb = Fixture.DbContextFactory.CreateDbContext();
            var stale = cleanupDb.WorkItems.Where(w => w.ClaimedPvcName != null).ToList();
            if (stale.Count > 0)
            {
                cleanupDb.WorkItems.RemoveRange(stale);
                await cleanupDb.SaveChangesAsync();
            }
        }

        // Seed two Dispatched WorkItems, one per PVC — no agent connected
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "fleet-pvc-test-0",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Dispatched,
            Payload = "{}",
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow,
            DispatchedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            ClaimedPvcName = "fake-pvc-0",
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId)
        });
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "fleet-pvc-test-1",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Dispatched,
            Payload = "{}",
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow,
            DispatchedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            ClaimedPvcName = "fake-pvc-1",
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId)
        });
        await db.SaveChangesAsync();

        // Act: navigate to /fleet
        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();

        // Assert: exhaustion banner is shown
        // The full Fleet.razor text is "Credential pool exhausted — all @_pool.Total slots are claimed,
        // so new work can't dispatch until one frees up." — use contains to match the substring.
        await Assertions.Expect(
            Page.GetByText("Credential pool exhausted — all 2 slots are claimed"))
            .ToBeVisibleAsync(new() { Timeout = 15_000 });

        // Assert: Kiro Credentials tile shows 0/2
        await Assertions.Expect(
            Page.Locator(".cockpit-stat:has-text('Kiro Credentials') .cockpit-stat-v"))
            .ToHaveTextAsync("0/2", new() { Timeout = 5_000 });
    }

    /// <summary>
    /// When one credential slot is free, no exhaustion banner is shown and the tile shows "1/2".
    /// </summary>
    [Fact]
    public async Task OneCredentialSlotFree_ShowsNoBanner()
    {
        // Remove any WorkItems with ClaimedPvcName left by previous tests (same reason as AllCredentialSlotsClaimed).
        {
            await using var cleanupDb = Fixture.DbContextFactory.CreateDbContext();
            var stale = cleanupDb.WorkItems.Where(w => w.ClaimedPvcName != null).ToList();
            if (stale.Count > 0)
            {
                cleanupDb.WorkItems.RemoveRange(stale);
                await cleanupDb.SaveChangesAsync();
            }
        }

        // Seed one Dispatched WorkItem claiming fake-pvc-0 — fake-pvc-1 remains free
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "fleet-pvc-test-0",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Dispatched,
            Payload = "{}",
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow,
            DispatchedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            ClaimedPvcName = "fake-pvc-0",
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId)
        });
        await db.SaveChangesAsync();

        // Act: navigate to /fleet
        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();

        // Assert: Kiro Credentials tile shows 1/2
        await Assertions.Expect(
            Page.Locator(".cockpit-stat:has-text('Kiro Credentials') .cockpit-stat-v"))
            .ToHaveTextAsync("1/2", new() { Timeout = 15_000 });

        // Assert: no exhaustion banner
        await Assertions.Expect(Page.GetByText("Credential pool exhausted"))
            .ToHaveCountAsync(0, new() { Timeout = 5_000 });
    }
}
