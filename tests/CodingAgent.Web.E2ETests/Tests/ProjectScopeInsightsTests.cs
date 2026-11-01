using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Verifies that Insights, Attention, and Knowledge pages follow the project-scope switcher —
/// the gap documented at ProjectSwitcherTests.cs:271 and :325.
/// Uses the admin session (E2ETestBase). SeedAsync must be called from each test body after
/// InitializeAsync resets the fixture (E2ETestBase.InitializeAsync is non-virtual).
/// Issue identifiers reserved for this file: 9411–9414.
/// Project IDs: P1 = 44444444-...-0001, P2 = 44444444-...-0002.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "ProjectSwitcher")]
[Collection(E2ECollection.Name)]
public sealed class ProjectScopeInsightsTests : E2ETestBase
{
    private const string P1 = "44444444-0000-0000-0000-000000000001";
    private const string P2 = "44444444-0000-0000-0000-000000000002";

    public ProjectScopeInsightsTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Seeds two projects and four terminal runs: P1 has issues 9411/9412 (Completed) and 9413
    /// (Failed); P2 has issue 9414 (Failed). All runs start within the default 24-hour window.
    /// Each run has a unique IssueIdentifier so Attention groups them separately.
    /// Must be called at the start of each test because E2ETestBase.InitializeAsync resets state.
    /// </summary>
    private async Task SeedAsync()
    {
        await Fixture.ConfigStore.SaveProjectAsync(
            new PipelineProject { Id = P1, Name = "Scope One", Enabled = true },
            CancellationToken.None);
        await Fixture.ConfigStore.SaveProjectAsync(
            new PipelineProject { Id = P2, Name = "Scope Two", Enabled = true },
            CancellationToken.None);

        var now = DateTimeOffset.UtcNow;

        // P1: run 9411 — Completed
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9411"),
            IssueTitle = "Scope run 9411",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            ProjectId = P1,
            StartedAtOffset = now.AddHours(-2),
#pragma warning disable CS0618
            StartedAt = now.AddHours(-2).DateTime,
#pragma warning restore CS0618
            CompletedAtOffset = now.AddHours(-2).AddMinutes(30)
        }, CancellationToken.None);

        // P1: run 9412 — Completed
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9412"),
            IssueTitle = "Scope run 9412",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            ProjectId = P1,
            StartedAtOffset = now.AddMinutes(-100),
#pragma warning disable CS0618
            StartedAt = now.AddMinutes(-100).DateTime,
#pragma warning restore CS0618
            CompletedAtOffset = now.AddMinutes(-70)
        }, CancellationToken.None);

        // P1: run 9413 — Failed
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9413"),
            IssueTitle = "Scope run 9413",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            ProjectId = P1,
            StartedAtOffset = now.AddMinutes(-90),
#pragma warning disable CS0618
            StartedAt = now.AddMinutes(-90).DateTime,
#pragma warning restore CS0618
            CompletedAtOffset = now.AddMinutes(-60)
        }, CancellationToken.None);

        // P2: run 9414 — Failed
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9414"),
            IssueTitle = "Scope run 9414",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            ProjectId = P2,
            StartedAtOffset = now.AddMinutes(-45),
#pragma warning disable CS0618
            StartedAt = now.AddMinutes(-45).DateTime,
#pragma warning restore CS0618
            CompletedAtOffset = now.AddMinutes(-30)
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Insights_FollowsTheSwitcher()
    {
        await SeedAsync();

        var insights = new InsightsPage(Page, BaseUrl);
        await insights.NavigateAsync();
        var switcher = new ProjectSwitcher(Page);
        await switcher.SelectAllProjectsAsync();

        // All projects: 4 runs total (3 P1 + 1 P2)
        await Assertions.Expect(Page.Locator("[data-testid='insights-total']")).ToHaveTextAsync("4");

        // P1 only: 3 runs — 2 succeeded, 1 failed
        await switcher.SelectByValueAsync(P1);
        await Assertions.Expect(Page.Locator("[data-testid='insights-total']")).ToHaveTextAsync("3");
        await Assertions.Expect(Page.Locator("[data-testid='insights-failed']")).ToHaveTextAsync("1");
        await Assertions.Expect(Page.Locator("[data-testid='insights-succeeded']")).ToHaveTextAsync("2");

        // P2 only: 1 run — 1 failed
        await switcher.SelectByValueAsync(P2);
        await Assertions.Expect(Page.Locator("[data-testid='insights-total']")).ToHaveTextAsync("1");
        await Assertions.Expect(Page.Locator("[data-testid='insights-failed']")).ToHaveTextAsync("1");
    }

    [Fact]
    public async Task Attention_FollowsTheSwitcher()
    {
        await SeedAsync();

        var attentionPage = new AttentionPage(Page, BaseUrl);
        await attentionPage.NavigateAsync();
        var switcher = new ProjectSwitcher(Page);
        await switcher.SelectAllProjectsAsync();

        // All projects: both failed rows visible
        await Assertions.Expect(Page.Locator("[data-testid='attention-row-9413']")).ToBeVisibleAsync();
        await Assertions.Expect(Page.Locator("[data-testid='attention-row-9414']")).ToBeVisibleAsync();

        // P1: 9413 visible (positive first), 9414 absent
        await switcher.SelectByValueAsync(P1);
        await Assertions.Expect(Page.Locator("[data-testid='attention-row-9413']")).ToBeVisibleAsync();
        await Assertions.Expect(Page.Locator("[data-testid='attention-row-9414']")).ToHaveCountAsync(0);

        // P2: 9414 visible (positive first), 9413 absent, section count = 1
        await switcher.SelectByValueAsync(P2);
        await Assertions.Expect(Page.Locator("[data-testid='attention-row-9414']")).ToBeVisibleAsync();
        await Assertions.Expect(Page.Locator("[data-testid='attention-row-9413']")).ToHaveCountAsync(0);
        await Assertions.Expect(Page.Locator("[data-testid='attention-section-count-failed-runs']"))
            .ToHaveTextAsync("1");
    }

    [Fact]
    public async Task Knowledge_FollowsTheSwitcher()
    {
        await SeedAsync();

        var knowledge = new KnowledgePage(Page, BaseUrl);
        await knowledge.NavigateAsync();
        var switcher = new ProjectSwitcher(Page);
        await switcher.SelectAllProjectsAsync();

        // Polls until the stat tile shows the expected value after each project switch.
        // Knowledge.razor uses RunHistoryQuery(ProjectId: State.SelectedProjectId) — the
        // mutation check confirms this parameter must not be null for the test to pass.
        var runsRecent = Page.Locator(".cockpit-stat:has(.cockpit-stat-l:has-text('Runs · recent')) .cockpit-stat-v");
        await Assertions.Expect(runsRecent).ToHaveTextAsync("4");

        await switcher.SelectByValueAsync(P1);
        await Assertions.Expect(runsRecent).ToHaveTextAsync("3");

        await switcher.SelectByValueAsync(P2);
        await Assertions.Expect(runsRecent).ToHaveTextAsync("1");
    }

    [Fact]
    public async Task SelectionPersists_ToAReload_OnInsights()
    {
        await SeedAsync();

        var insights = new InsightsPage(Page, BaseUrl);
        await insights.NavigateAsync();
        var switcher = new ProjectSwitcher(Page);
        await switcher.SelectByValueAsync(P2);

        await Page.ReloadCockpitPageAsync();
        await Page.WaitForProjectSwitcherRestoredAsync();
        await Assertions.Expect(Page.Locator("[data-testid='insights-total']")).ToHaveTextAsync("1");
    }
}
