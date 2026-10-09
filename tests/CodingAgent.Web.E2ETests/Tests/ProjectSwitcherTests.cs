using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the cockpit project-scope switcher (issue #3103 / part of #3082).
///
/// <para>
/// The switcher is a <c>&lt;select aria-label="Project scope"&gt;</c> in the cockpit top bar.
/// It stores the selection in <c>localStorage</c> under <c>cockpit.selectedProjectId</c> and
/// scopes every cockpit page (Overview, Work, Runs, Insights, Attention, Knowledge). Fleet is
/// intentionally excluded because agents are shared across projects.
/// </para>
///
/// <list type="number">
///   <item>Scenario 1 — All projects: every page shows all items.</item>
///   <item>Scenario 2 — P1 selected: every page shows only P1's items.</item>
///   <item>Scenario 3 — Persistence: P2 is still selected after navigation and reload.</item>
///   <item>Scenario 4 — Stale value: a deleted project ID in localStorage falls back to "All projects".</item>
///   <item>Scenario 5 — Narrow viewport (375 px): the switcher is reachable and functional.</item>
/// </list>
///
/// Seeding convention: two projects P1 and P2, each with one template, one active run, one
/// queued item and two finished runs. Identifiers used in NO other file: 3101–3110.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "ProjectSwitcher")]
[Collection(E2ECollection.Name)]
public sealed class ProjectSwitcherTests : E2ETestBase
{
    // ── Stable identifiers ────────────────────────────────────────────────

    private const string P1Id = "11111111-1111-1111-1111-111111111111";
    private const string P1Name = "Project One";
    private const string P1TemplateId = "tmpl-proj1";

    private const string P2Id = "22222222-2222-2222-2222-222222222222";
    private const string P2Name = "Project Two";
    private const string P2TemplateId = "tmpl-proj2";

    // Issue identifiers — not used in any other test file
    private const string P1ActiveIssue = "3101";
    private const string P1QueuedIssue = "3102";
    private const string P1RunA = "3103";
    private const string P1RunB = "3104";
    private const string P2ActiveIssue = "3105";
    private const string P2QueuedIssue = "3106";
    private const string P2RunA = "3107";
    private const string P2RunB = "3108";

    public ProjectSwitcherTests(E2EFixture fixture) : base(fixture) { }

    // ── Data seeding ──────────────────────────────────────────────────────

    /// <summary>
    /// Seeds two projects (P1, P2) each with:
    /// - 1 active (In-flight) WorkItem
    /// - 1 pending (Queued) WorkItem
    /// - 2 completed runs in history
    /// </summary>
    private async Task SeedTwoProjectsAsync()
    {
        // TODO: [WARNING] CancellationToken.None is used for all async seed calls. The test runner
        // cannot cancel a hung seed operation, which could cause CI jobs to hang indefinitely if
        // a seed call blocks. Consider threading the xUnit cancellation token through if the
        // test base exposes one.
        var ct = CancellationToken.None;

        // ── Projects ─────────────────────────────────────────────────────
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = P1Id,
            Name = P1Name,
            Enabled = true,
            // TODO: [WARNING] `new List<string>()` is a mutable concrete type. If PipelineProject.TemplateIds
            // is typed as IReadOnlyList<string> the assignment compiles but silently retains mutability at
            // the call site. If it is typed as List<string> in production, consider using an immutable
            // collection (e.g. Array.Empty<string>() or ImmutableList<string>.Empty) to match the intent.
            TemplateIds = new List<string>()
        }, ct);

        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = P2Id,
            Name = P2Name,
            Enabled = true,
            TemplateIds = new List<string>()
        }, ct);

        // ── Work items — Active (in-flight) ────────────────────────────
        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            db.WorkItems.AddRange(
                new CodingAgent.Infrastructure.Persistence.Entities.WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    TaskType = WorkItemTaskType.Implementation,
                    IssueIdentifier = P1ActiveIssue,
                    IssueProviderConfigId = "issue-e2e",
                    Status = WorkItemStatus.Dispatched,
                    Payload = "{}",
                    AgentSelector = "kiro,dotnet",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
                    TimeoutSeconds = 3600,
                    ProjectId = Guid.Parse(P1Id)
                },
                new CodingAgent.Infrastructure.Persistence.Entities.WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    TaskType = WorkItemTaskType.Implementation,
                    IssueIdentifier = P2ActiveIssue,
                    IssueProviderConfigId = "issue-e2e",
                    Status = WorkItemStatus.Dispatched,
                    Payload = "{}",
                    AgentSelector = "kiro,dotnet",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                    DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                    TimeoutSeconds = 3600,
                    ProjectId = Guid.Parse(P2Id)
                },
                // ── Work items — Pending (queued) ──────────────────────
                new CodingAgent.Infrastructure.Persistence.Entities.WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    TaskType = WorkItemTaskType.Implementation,
                    IssueIdentifier = P1QueuedIssue,
                    IssueProviderConfigId = "issue-e2e",
                    Status = WorkItemStatus.Pending,
                    Payload = "{}",
                    AgentSelector = "kiro,dotnet",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                    TimeoutSeconds = 3600,
                    ProjectId = Guid.Parse(P1Id)
                },
                new CodingAgent.Infrastructure.Persistence.Entities.WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    TaskType = WorkItemTaskType.Implementation,
                    IssueIdentifier = P2QueuedIssue,
                    IssueProviderConfigId = "issue-e2e",
                    Status = WorkItemStatus.Pending,
                    Payload = "{}",
                    AgentSelector = "kiro,dotnet",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    TimeoutSeconds = 3600,
                    ProjectId = Guid.Parse(P2Id)
                }
            );
            await db.SaveChangesAsync(ct);
        }

        // ── Finished runs in history ───────────────────────────────────
        var now = DateTimeOffset.UtcNow;

        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier(P1RunA),
            IssueTitle = $"P1 Run A",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            ProjectId = P1Id,
            StartedAtOffset = now.AddHours(-2),
#pragma warning disable CS0618
            StartedAt = now.AddHours(-2).DateTime,
#pragma warning restore CS0618
        }, ct);

        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier(P1RunB),
            IssueTitle = $"P1 Run B",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            ProjectId = P1Id,
            StartedAtOffset = now.AddHours(-1),
#pragma warning disable CS0618
            StartedAt = now.AddHours(-1).DateTime,
#pragma warning restore CS0618
        }, ct);

        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier(P2RunA),
            IssueTitle = $"P2 Run A",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            ProjectId = P2Id,
            StartedAtOffset = now.AddHours(-2),
#pragma warning disable CS0618
            StartedAt = now.AddHours(-2).DateTime,
#pragma warning restore CS0618
        }, ct);

        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier(P2RunB),
            IssueTitle = $"P2 Run B",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            ProjectId = P2Id,
            StartedAtOffset = now.AddHours(-1),
#pragma warning disable CS0618
            StartedAt = now.AddHours(-1).DateTime,
#pragma warning restore CS0618
        }, ct);
    }

    // ── Scenario 1 — All projects ─────────────────────────────────────────

    /// <summary>
    /// Scenario 1: When "All projects" is selected, every cockpit page shows items from both
    /// projects. Verified on Overview (stat strip), Work (in-flight + queue rows), and Runs (run rows).
    /// </summary>
    [Fact]
    public async Task S1_AllProjects_ShowsItemsFromBothProjects()
    {
        await SeedTwoProjectsAsync();

        // Navigate to overview (brings up CockpitLayout which loads projects for the switcher).
        var overview = new OverviewPage(Page, BaseUrl);
        await overview.NavigateAsync();

        var switcher = new ProjectSwitcher(Page);

        // Confirm switcher offers both projects.
        var options = await switcher.GetOptionTextsAsync();
        Assert.Contains(P1Name, options);
        Assert.Contains(P2Name, options);

        // Ensure "All projects" is the current selection.
        await switcher.SelectAllProjectsAsync();
        Assert.Equal("", await switcher.GetSelectedValueAsync());

        // Overview stat strip: Queue = 2 (both projects' pending items).
        // NOTE: The "Active" stat counts in-memory orchestrator runs, not DB-persisted
        // WorkItemEntity rows. E2E tests cannot seed in-memory runs, so only the queue
        // count (sourced from WorkItems.GetPendingAsync) is asserted here.
        var queueCount = await overview.GetQueueCountAsync();
        Assert.Equal(2, queueCount);

        // Work page: both active issues visible.
        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        Assert.True(await work.IsIssueInFlightAsync(P1ActiveIssue),
            $"Work/All: P1 active issue #{P1ActiveIssue} should be in-flight");
        Assert.True(await work.IsIssueInFlightAsync(P2ActiveIssue),
            $"Work/All: P2 active issue #{P2ActiveIssue} should be in-flight");
        Assert.True(await work.IsIssueQueuedAsync(P1QueuedIssue),
            $"Work/All: P1 queued issue #{P1QueuedIssue} should be queued");
        Assert.True(await work.IsIssueQueuedAsync(P2QueuedIssue),
            $"Work/All: P2 queued issue #{P2QueuedIssue} should be queued");

        // Runs page: runs from both projects visible.
        var runs = new RunsPage(Page, BaseUrl);
        await runs.NavigateAsync();

        Assert.True(await runs.IsRunVisibleAsync(P1RunA),
            $"Runs/All: P1 run #{P1RunA} should be visible");
        Assert.True(await runs.IsRunVisibleAsync(P2RunA),
            $"Runs/All: P2 run #{P2RunA} should be visible");
        // TODO: [WARNING] Insights, Attention, and Knowledge pages are not checked in this scenario.
        // The acceptance criteria require all six scoped pages to show items from both projects when
        // "All projects" is selected. Add assertions on InsightsPage, AttentionPage, and KnowledgePage
        // once suitable page-object helpers (e.g. item-count getters) are available.
    }

    // ── Scenario 2 — P1 selected ──────────────────────────────────────────

    /// <summary>
    /// Scenario 2: When P1 is selected, each page shows only P1's items. P2's items must not
    /// appear. Verified on Work (in-flight + queue) and Runs (run rows).
    /// </summary>
    [Fact]
    public async Task S2_P1Selected_ShowsOnlyP1Items()
    {
        await SeedTwoProjectsAsync();

        // Navigate to the overview page first to establish the Blazor circuit.
        await new OverviewPage(Page, BaseUrl).NavigateAsync();

        var switcher = new ProjectSwitcher(Page);

        // Select P1.
        await switcher.SelectByValueAsync(P1Id);
        Assert.Equal(P1Id, await switcher.GetSelectedValueAsync());

        // Work page: navigate AFTER switching so the page loads with the scoped state.
        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        // P1 items must be present.
        Assert.True(await work.IsIssueInFlightAsync(P1ActiveIssue),
            $"Work/P1: #{P1ActiveIssue} should be in-flight under P1 scope");
        Assert.True(await work.IsIssueQueuedAsync(P1QueuedIssue),
            $"Work/P1: #{P1QueuedIssue} should be queued under P1 scope");

        // P2 items must be absent.
        Assert.False(await work.IsIssueInFlightAsync(P2ActiveIssue),
            $"Work/P1: #{P2ActiveIssue} must NOT appear under P1 scope");
        Assert.False(await work.IsIssueQueuedAsync(P2QueuedIssue),
            $"Work/P1: #{P2QueuedIssue} must NOT appear under P1 scope");

        // Runs page: P1 runs present, P2 runs absent.
        var runs = new RunsPage(Page, BaseUrl);
        await runs.NavigateAsync();

        Assert.True(await runs.IsRunVisibleAsync(P1RunA),
            $"Runs/P1: #{P1RunA} should be visible under P1 scope");
        Assert.True(await runs.IsRunVisibleAsync(P1RunB),
            $"Runs/P1: #{P1RunB} should be visible under P1 scope");
        Assert.False(await runs.IsRunVisibleAsync(P2RunA),
            $"Runs/P1: #{P2RunA} must NOT appear under P1 scope");
        Assert.False(await runs.IsRunVisibleAsync(P2RunB),
            $"Runs/P1: #{P2RunB} must NOT appear under P1 scope");
        // TODO: [WARNING] Overview, Insights, Attention, and Knowledge pages are not checked
        // for P1 scoping. The acceptance criteria and issue description explicitly require all
        // six cockpit pages to scope correctly. A regression where any of these pages ignores
        // SelectedProjectId would silently pass this test. Extend the scenario once suitable
        // page-object helpers are available for those pages.
    }

    // ── Scenario 3 — Persistence ──────────────────────────────────────────

    /// <summary>
    /// Scenario 3: P2 selection is persisted in localStorage and survives a full-page reload.
    /// After the reload the switcher shows P2 and pages remain scoped.
    /// </summary>
    [Fact]
    public async Task S3_Persistence_SelectedProjectSurvivesReload()
    {
        await SeedTwoProjectsAsync();

        // Navigate and select P2.
        await new OverviewPage(Page, BaseUrl).NavigateAsync();
        var switcher = new ProjectSwitcher(Page);
        await switcher.SelectByValueAsync(P2Id);
        Assert.Equal(P2Id, await switcher.GetSelectedValueAsync());

        // Confirm localStorage was written.
        var stored = await switcher.ReadLocalStorageAsync();
        Assert.Equal(P2Id, stored);

        // Navigate to another page to exercise inter-page persistence without a full reload.
        var runsBeforeReload = new RunsPage(Page, BaseUrl);
        await runsBeforeReload.NavigateAsync();

        var switcherOnRuns = new ProjectSwitcher(Page);
        Assert.Equal(P2Id, await switcherOnRuns.GetSelectedValueAsync());

        // Full page reload — restores from localStorage.
        await Page.ReloadAsync();
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        await Page.WaitForBlazorAsync(15_000);
        // After a reload, CockpitLayout.OnAfterRenderAsync restores the saved project.
        // Poll until the switcher reflects P2Id instead of sleeping a fixed duration.
        var switcherAfterReload = new ProjectSwitcher(Page);
        await WaitUntilAsync(
            async () => await switcherAfterReload.GetSelectedValueAsync() == P2Id,
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(100));

        // Switcher must display P2 again.
        var valueAfterReload = await switcherAfterReload.GetSelectedValueAsync();
        Assert.Equal(P2Id, valueAfterReload);

        // And pages must still be scoped to P2: P1 runs absent.
        var runsAfterReload = new RunsPage(Page, BaseUrl);
        await runsAfterReload.NavigateAsync();
        // After navigating to a new page, CockpitLayout.OnAfterRenderAsync runs again and restores
        // the project from localStorage asynchronously. Wait until the switcher's DOM value matches
        // localStorage before asserting row visibility, to avoid reading stale all-projects data
        // that was loaded before the restoration fired.
        await Page.WaitForProjectSwitcherRestoredAsync();

        Assert.False(await runsAfterReload.IsRunVisibleAsync(P1RunA),
            $"Runs after reload: P1 run #{P1RunA} must NOT appear when P2 is selected");
        Assert.True(await runsAfterReload.IsRunVisibleAsync(P2RunA),
            $"Runs after reload: P2 run #{P2RunA} should be visible when P2 is selected");
        // TODO: [WARNING] Post-reload content check covers only Runs. The issue lists six scoped pages
        // (Overview, Work, Runs, Insights, Attention, Knowledge). A regression where CockpitState.SelectedProjectId
        // is not propagated to data calls on other pages after reload would not be caught here.
        // Extend the scenario to verify at least one additional data-bearing page (e.g. Work) after reload.
    }

    // ── Scenario 4 — Stale value ──────────────────────────────────────────

    /// <summary>
    /// Scenario 4: When <c>cockpit.selectedProjectId</c> in localStorage contains a project ID
    /// that no longer exists, the app must fall back to "All projects" without displaying an
    /// error and must clear the stale key.
    /// </summary>
    [Fact]
    public async Task S4_StaleProjectId_FallsBackToAllProjects_WithoutError()
    {
        await SeedTwoProjectsAsync();

        // Write a non-existent project ID to localStorage BEFORE navigating.
        // We do this by navigating first to get a page context, then setting localStorage,
        // then reloading so the layout's OnAfterRenderAsync sees the stale value.
        await Page.GotoAsync($"{BaseUrl}/overview");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });

        var switcher = new ProjectSwitcher(Page);
        await switcher.WriteLocalStorageAsync("ffffffff-ffff-ffff-ffff-ffffffffffff");

        // Reload — CockpitLayout should detect the missing project, clear the key, and stay on "All".
        await Page.ReloadAsync();
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        await Page.WaitForBlazorAsync(15_000);
        // TODO: [WARNING] Fixed 2000 ms sleep is fragile on slow/fast CI. Replace with a
        // deterministic wait (e.g. poll GetSelectedValueAsync() until "" or timeout fires).
        await Page.WaitForTimeoutAsync(2000);

        // No Blazor error UI should be visible.
        var blazorError = Page.Locator("#blazor-error-ui");
        Assert.False(await blazorError.IsVisibleAsync(),
            "No Blazor error UI should be visible after encountering a stale project ID");

        // The switcher should have fallen back to "All projects".
        var switcherAfter = new ProjectSwitcher(Page);
        var valueAfter = await switcherAfter.GetSelectedValueAsync();
        Assert.Equal("", valueAfter);

        // localStorage should have been cleared (set to "" by the layout's stale-value handler).
        var stored = await switcherAfter.ReadLocalStorageAsync();
        // The layout sets it to "" when the project is not found.
        // TODO: [WARNING] The dual-path assertion `stored == "" || stored == null` is intentionally
        // loose: the production code sets the key to "" on stale fallback (as noted in the comment
        // above), so the correct post-condition is `stored == ""`. The null branch was added to
        // tolerate a future implementation that removes the key entirely, but that divergence would
        // be silent. If the production contract is pinned to "" (set, not remove), tighten this to
        // Assert.Equal("", stored). If either form is acceptable, document that explicitly here.
        Assert.True(stored == "" || stored == null,
            $"localStorage should be cleared or empty, but was '{stored}'");
        // TODO: [WARNING] S4 does not assert page content after the stale-value fallback. A regression
        // where the layout clears the UI control but leaves CockpitState.SelectedProjectId at the
        // deleted GUID (causing pages to show no items) would not be caught. Add assertions on
        // at least one data-bearing page (e.g. RunsPage or WorkPage) to confirm items from both
        // projects are visible after the fallback.
    }

    // ── Scenario 5 — Narrow viewport ─────────────────────────────────────

    /// <summary>
    /// Scenario 5: At 375 px width (mobile), the project switcher is reachable and functional.
    /// The top bar is always visible; the sidebar is hidden behind the hamburger toggle.
    /// </summary>
    [Fact]
    public async Task S5_NarrowViewport_SwitcherIsReachableAndFunctional()
    {
        await SeedTwoProjectsAsync();

        // Set narrow viewport.
        await Page.SetViewportSizeAsync(375, 812);

        await new OverviewPage(Page, BaseUrl).NavigateAsync();

        var switcher = new ProjectSwitcher(Page);

        // The switcher must be visible (it lives in the top bar, not the sidebar).
        Assert.True(await switcher.IsVisibleAsync(),
            "Project switcher must be visible at 375 px width");

        // Select P1 via the switcher.
        await switcher.SelectByValueAsync(P1Id);
        Assert.Equal(P1Id, await switcher.GetSelectedValueAsync());

        // Navigate to Work page and verify P1 scoping works at narrow width.
        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        Assert.True(await work.IsIssueInFlightAsync(P1ActiveIssue),
            $"Work/375px/P1: #{P1ActiveIssue} should be visible");
        Assert.False(await work.IsIssueInFlightAsync(P2ActiveIssue),
            $"Work/375px/P1: #{P2ActiveIssue} must NOT appear");

        // Switch back to "All projects" and verify both items return.
        // Navigate back to overview first to reload the switcher in context.
        await Page.GotoAsync($"{BaseUrl}/overview");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        await Page.WaitForBlazorAsync(15_000);
        await Page.WaitForTimeoutAsync(1000);

        var switcherBack = new ProjectSwitcher(Page);
        await switcherBack.SelectAllProjectsAsync();
        Assert.Equal("", await switcherBack.GetSelectedValueAsync());

        var workAll = new WorkPage(Page, BaseUrl);
        await workAll.NavigateAsync();

        Assert.True(await workAll.IsIssueInFlightAsync(P1ActiveIssue),
            $"Work/375px/All: #{P1ActiveIssue} should be visible");
        Assert.True(await workAll.IsIssueInFlightAsync(P2ActiveIssue),
            $"Work/375px/All: #{P2ActiveIssue} should be visible");
    }
}
