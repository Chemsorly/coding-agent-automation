using System.Text.RegularExpressions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the Provider backlog card on /work: the readiness chip per issue, the
/// "{ready} ready · {open} open" header, the truncation notice and the empty text.
///
/// <para>
/// Every test seeds the issue provider, the template and any WorkItems before it navigates: the
/// backlog loads once after the first render. No agent is connected in any test, so the seeded
/// WorkItems keep the status they were seeded with.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class WorkBacklogCardTests : E2ETestBase
{
    private const string EmptyText = "No open issues found (or no issue provider configured).";

    public WorkBacklogCardTests(E2EFixture fixture) : base(fixture) { }

    // ── Seed helpers ──────────────────────────────────────────────────────

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

    private void AddIssue(string identifier, string description, params string[] labels)
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = identifier,
            Title = $"Backlog issue {identifier}",
            Description = description,
            Labels = labels
        });
    }

    private async Task SeedWorkItemAsync(string issueIdentifier, WorkItemStatus status)
    {
        await using var db = await Fixture.DbContextFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "issue-e2e",
            Status = status,
            Payload = "{}",
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            DispatchedAt = status == WorkItemStatus.Pending ? null : DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeoutSeconds = 3600,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
        });
        await db.SaveChangesAsync();
    }

    // ── Locators ──────────────────────────────────────────────────────────

    private ILocator BacklogCard => Page.Locator(".cockpit-card:has(h2:has-text('Provider backlog'))");

    private ILocator Row(string id) => Page.Locator(
        $".cockpit-card:has(h2:has-text('Provider backlog')) tbody tr:has-text('#{id}')");

    private ILocator Summary => Page.Locator(".cockpit-card:has(h2:has-text('Provider backlog')) .cockpit-card-header span");

    // ── Tests ─────────────────────────────────────────────────────────────

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
        AddIssue("9501", "", label);

        await new WorkPage(Page, BaseUrl).NavigateAsync();

        await Assertions.Expect(Row("9501").Locator(".cockpit-chip")).ToHaveTextAsync(chip);
    }

    [Fact]
    public async Task BlockedIssue_NamesItsDependency_AndIsNotCountedReady()
    {
        await SeedTemplateAsync();
        // Same description as InsightsAndAttentionTests.Attention_A7: a dependency on open issue 99.
        AddIssue("9502", "Blocked by #99", "enhancement");
        AddIssue("9503", "", "enhancement");

        await new WorkPage(Page, BaseUrl).NavigateAsync();

        var blockedChip = Row("9502").Locator(".cockpit-chip");
        await Assertions.Expect(blockedChip).ToHaveTextAsync(new Regex("^Blocked"));
        await Assertions.Expect(blockedChip).ToContainTextAsync("#99");
        await Assertions.Expect(Row("9503").Locator(".cockpit-chip")).ToHaveTextAsync("Ready");
        await Assertions.Expect(Summary).ToHaveTextAsync("1 ready · 2 open");
    }

    [Fact]
    public async Task IssueWithPendingWorkItem_ShowsQueued()
    {
        await SeedTemplateAsync();
        AddIssue("9504", "", "enhancement");
        await SeedWorkItemAsync("9504", WorkItemStatus.Pending);

        await new WorkPage(Page, BaseUrl).NavigateAsync();

        await Assertions.Expect(Row("9504").Locator(".cockpit-chip")).ToHaveTextAsync("Queued");
        await Assertions.Expect(Summary).ToHaveTextAsync("0 ready · 1 open");
    }

    [Fact]
    public async Task IssueWithDispatchedWorkItem_ShowsRunning()
    {
        await SeedTemplateAsync();
        AddIssue("9504", "", "enhancement");
        await SeedWorkItemAsync("9504", WorkItemStatus.Dispatched);

        await new WorkPage(Page, BaseUrl).NavigateAsync();

        await Assertions.Expect(Row("9504").Locator(".cockpit-chip")).ToHaveTextAsync("Running");
        await Assertions.Expect(Summary).ToHaveTextAsync("0 ready · 1 open");
    }

    [Fact]
    public async Task MoreThan200Issues_IsTruncated()
    {
        await SeedTemplateAsync();
        for (var n = 10001; n <= 10201; n++)
            AddIssue(n.ToString(), "", "enhancement");

        await new WorkPage(Page, BaseUrl).NavigateAsync();

        // 200 rows take a moment to render: the count assertion polls.
        await Assertions.Expect(BacklogCard.Locator("tbody tr")).ToHaveCountAsync(200);
        await Assertions.Expect(Page.Locator("body"))
            .ToContainTextAsync("Showing top 200 issues — more open issues exist in the provider.");
        await Assertions.Expect(Summary).ToHaveTextAsync(new Regex("200\\+ open$"));
    }

    [Fact]
    public async Task ExactlyOneIssue_IsNotTruncated()
    {
        await SeedTemplateAsync();
        AddIssue("9506", "", "enhancement");

        await new WorkPage(Page, BaseUrl).NavigateAsync();

        // The summary proves the backlog has rendered before the absence is asserted.
        await Assertions.Expect(Summary).ToHaveTextAsync("1 ready · 1 open");
        await Assertions.Expect(Page.GetByText("Showing top")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task NoEnabledTemplate_ShowsTheEmptyText()
    {
        // The provider has an open issue, so only the missing template can explain the empty card.
        AddIssue("9508", "", "enhancement");

        await new WorkPage(Page, BaseUrl).NavigateAsync();

        await Assertions.Expect(Page.Locator("body")).ToContainTextAsync(EmptyText);
    }

    [Fact]
    public async Task ProviderFails_DegradesToTheEmptyText_QueueStaysUsable()
    {
        await SeedTemplateAsync();
        AddIssue("9507", "", "enhancement");
        await SeedWorkItemAsync("9505", WorkItemStatus.Pending);
        // After seeding and before navigating: the queue is read from the database, not the provider.
        Fixture.IssueProvider.ShouldFail = true;

        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        await Assertions.Expect(Page.Locator("body")).ToContainTextAsync(EmptyText);
        await Assertions.Expect(work.QueueRow("9505")).ToBeVisibleAsync();
    }
}
