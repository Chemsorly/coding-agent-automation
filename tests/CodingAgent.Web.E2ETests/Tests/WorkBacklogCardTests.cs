using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the Provider backlog card on the /work page (Work.razor:263-347).
/// Covers chip text, summary counts, truncation, empty state, and provider failure degradation.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class WorkBacklogCardTests : E2ETestBase
{
    public WorkBacklogCardTests(E2EFixture fixture) : base(fixture) { }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>Seeds the standard backlog template used by most tests in this class.</summary>
    private async Task SeedTemplateAsync()
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "backlog-template",
            Name = "Backlog Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);
    }

    /// <summary>Locates the row for the given issue identifier in the Provider backlog table.</summary>
    private ILocator Row(string id) =>
        Page.Locator($".cockpit-card:has(h2:has-text('Provider backlog')) tbody tr:has-text('#{id}')");

    /// <summary>The summary span in the Provider backlog card header.</summary>
    private ILocator Summary =>
        Page.Locator(".cockpit-card:has(h2:has-text('Provider backlog')) .cockpit-card-header span");

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Theory: each agent label maps to the expected readiness chip text.
    /// </summary>
    [Theory]
    [InlineData("enhancement", "Ready")]
    [InlineData(AgentLabels.Error, "Error")]
    [InlineData(AgentLabels.Done, "Done")]
    [InlineData(AgentLabels.NeedsRefinement, "Needs refinement")]
    [InlineData(AgentLabels.InProgress, "In progress")]
    [InlineData(AgentLabels.Next, "Queued")]
    [InlineData(AgentLabels.WontDo, "Won't do")]
    [InlineData(AgentLabels.Epic, "Epic")]
    [InlineData("backlog", "Parked")]
    public async Task LabelledIssue_ShowsItsChip(string label, string chip)
    {
        await SeedTemplateAsync();
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9501",
            Title = "Backlog chip test issue",
            Description = "",
            Labels = [label]
        });

        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        await Assertions.Expect(Row("9501").Locator(".cockpit-chip"))
            .ToHaveTextAsync(chip, new() { Timeout = 15_000 });
    }

    /// <summary>
    /// A blocked issue shows a "Blocked by #99" chip; an enhancement issue shows "Ready";
    /// the summary correctly counts 1 ready out of 2 open.
    /// </summary>
    [Fact]
    public async Task BlockedIssue_NamesItsDependency_AndIsNotCountedReady()
    {
        await SeedTemplateAsync();

        // Seed issue 9502 with the same blocked description as InsightsAndAttentionTests.cs:271
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9502",
            Title = "Blocked issue",
            Description = "Blocked by #99",
            Labels = ["enhancement"]
        });

        // Seed issue 9503 as a clean Ready issue
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9503",
            Title = "Ready issue",
            Description = "",
            Labels = ["enhancement"]
        });

        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        // Issue 9502 chip starts with "Blocked" and contains "#99".
        // "Blocked by #99" is a more specific substring than just "Blocked" — it proves
        // both the "starts with Blocked" and "contains #99" contract in a single polling assertion.
        var chip9502 = Row("9502").Locator(".cockpit-chip");
        await Assertions.Expect(chip9502).ToContainTextAsync("Blocked by #99", new() { Timeout = 15_000 });

        // Issue 9503 chip is "Ready"
        await Assertions.Expect(Row("9503").Locator(".cockpit-chip"))
            .ToHaveTextAsync("Ready", new() { Timeout = 5_000 });

        // Summary: 1 ready (9503), 2 open total
        await Assertions.Expect(Summary)
            .ToHaveTextAsync("1 ready · 2 open", new() { Timeout = 5_000 });
    }

    /// <summary>
    /// An issue that has a Pending WorkItem should show "Queued" regardless of its labels,
    /// and the ready count is 0 (WorkItem overrides the label readiness).
    /// </summary>
    [Fact]
    public async Task IssueWithPendingWorkItem_ShowsQueued()
    {
        await SeedTemplateAsync();

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9504",
            Title = "Pending work item issue",
            Description = "",
            Labels = ["enhancement"]
        });

        // Seed a Pending WorkItem — no agent connected, so it stays Pending
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "9504",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Pending,
            Payload = "{}",
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId)
        });
        await db.SaveChangesAsync();

        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        // Chip is "Queued" (WorkItem overrides the "Ready" label display)
        await Assertions.Expect(Row("9504").Locator(".cockpit-chip"))
            .ToHaveTextAsync("Queued", new() { Timeout = 15_000 });

        // 0 ready (WorkItem removes it from the ready count), 1 open
        await Assertions.Expect(Summary)
            .ToHaveTextAsync("0 ready · 1 open", new() { Timeout = 5_000 });
    }

    /// <summary>
    /// An issue that has a Dispatched WorkItem should show "Running".
    /// Uses identifier "9508" (not "9504") to avoid sharing a WorkItem identifier with
    /// IssueWithPendingWorkItem_ShowsQueued: ResetAll does not truncate WorkItem rows, so
    /// using the same identifier across tests would leave stale WorkItems that affect later tests.
    /// </summary>
    [Fact]
    public async Task IssueWithDispatchedWorkItem_ShowsRunning()
    {
        await SeedTemplateAsync();

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9508",
            Title = "Dispatched work item issue",
            Description = "",
            Labels = ["enhancement"]
        });

        // Seed a Dispatched WorkItem — no agent connected, so it stays Dispatched
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "9508",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Dispatched,
            Payload = "{}",
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow,
            DispatchedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId)
        });
        await db.SaveChangesAsync();

        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        await Assertions.Expect(Row("9508").Locator(".cockpit-chip"))
            .ToHaveTextAsync("Running", new() { Timeout = 15_000 });
    }

    /// <summary>
    /// When more than 200 issues exist the backlog is truncated to 200 rows and shows the
    /// truncation banner; the summary ends with "200+ open".
    /// </summary>
    [Fact]
    public async Task MoreThan200Issues_IsTruncated()
    {
        await SeedTemplateAsync();

        // Seed 201 issues; BacklogService caps at MaxIssuesPerProvider = 200
        for (var i = 10001; i <= 10201; i++)
        {
            Fixture.IssueProvider.Issues.Add(new IssueDetail
            {
                Identifier = i.ToString(),
                Title = $"Issue {i}",
                Description = "",
                Labels = ["enhancement"]
            });
        }

        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        // Exactly 200 rows (not 201 — the 201st is beyond the cap)
        await Assertions.Expect(
            Page.Locator(".cockpit-card:has(h2:has-text('Provider backlog')) tbody tr"))
            .ToHaveCountAsync(200, new() { Timeout = 30_000 });

        // Truncation message is shown
        await Assertions.Expect(
            Page.GetByText("Showing top 200 issues — more open issues exist in the provider."))
            .ToBeVisibleAsync(new() { Timeout = 5_000 });

        // Summary ends with "200+ open"
        await Assertions.Expect(Summary)
            .ToContainTextAsync("200+ open", new() { Timeout = 5_000 });
    }

    /// <summary>
    /// When exactly one issue exists the backlog is not truncated and the summary shows "1 ready · 1 open".
    /// </summary>
    [Fact]
    public async Task ExactlyOneIssue_IsNotTruncated()
    {
        await SeedTemplateAsync();

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9506",
            Title = "Single issue",
            Description = "",
            Labels = ["enhancement"]
        });

        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        // No truncation banner
        await Assertions.Expect(Page.GetByText("Showing top"))
            .ToHaveCountAsync(0, new() { Timeout = 10_000 });

        // Summary shows exact count
        await Assertions.Expect(Summary)
            .ToHaveTextAsync("1 ready · 1 open", new() { Timeout = 10_000 });
    }

    /// <summary>
    /// When no enabled template is configured, the backlog card shows the empty-state message.
    /// </summary>
    [Fact]
    public async Task NoEnabledTemplate_ShowsTheEmptyText()
    {
        // Do not call SeedTemplateAsync — no template exists

        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        await Assertions.Expect(
            Page.GetByText("No open issues found (or no issue provider configured)."))
            .ToBeVisibleAsync(new() { Timeout = 10_000 });
    }

    /// <summary>
    /// When the issue provider fails, the backlog degrades to the empty state, but the queue
    /// (which reads from the database) remains visible and usable.
    /// </summary>
    [Fact]
    public async Task ProviderFails_DegradesToTheEmptyText_QueueStaysUsable()
    {
        await SeedTemplateAsync();

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9507",
            Title = "Issue that won't be shown because provider fails",
            Description = "",
            Labels = ["enhancement"]
        });

        // Seed a Pending WorkItem in the queue — queue reads from DB, not from the provider
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "9505",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Pending,
            Payload = "{}",
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId)
        });
        await db.SaveChangesAsync();

        // Trigger provider failure BEFORE navigating (backlog loads on first render)
        Fixture.IssueProvider.ShouldFail = true;

        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        // Backlog degrades to empty text
        await Assertions.Expect(
            Page.GetByText("No open issues found (or no issue provider configured)."))
            .ToBeVisibleAsync(new() { Timeout = 10_000 });

        // Queue row for 9505 is still visible (reads from DB, unaffected by provider failure)
        await Assertions.Expect(work.QueueRow("9505")).ToBeVisibleAsync(new() { Timeout = 10_000 });
    }
}
