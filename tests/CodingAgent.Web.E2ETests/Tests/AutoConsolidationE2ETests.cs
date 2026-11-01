using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Scheduler.Services;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for <see cref="AutoConsolidationService"/>, the background service that
/// automatically triggers brain consolidation runs when a staleness threshold is reached.
///
/// <para>
/// These tests extend <see cref="E2ETestBase"/> because some scenarios assert on the
/// <c>/consolidation</c> page counter, which requires a live browser context. Scenarios
/// that only assert on DB state still extend this class so they share the same setup pattern.
/// </para>
///
/// <para>
/// <b>WorkItem isolation:</b> <see cref="ConsolidationItemsAsync"/> filters by the scope key
/// <c>"BrainConsolidation:brain-e2e"</c> AND by <c>CreatedAt &gt;= _testStartedAt</c>, where
/// <c>_testStartedAt</c> is captured when the test class is constructed (once per test in xUnit).
/// The shared in-memory DB is not truncated between tests; without the timestamp filter, a
/// Pending WorkItem created by <c>WhenBrainRunsReachTheThreshold</c> or
/// <c>WhenTheRouteIsRefused</c> would persist and inflate the expected-zero counts in subsequent
/// tests that run in the same process.
/// </para>
///
/// <para>
/// <b>Route isolation:</b> routes are seeded via
/// <see cref="E2ERouting.Key(IEnumerable{string})"/> with <c>"jc-e2e"</c>, a job template
/// registered in <see cref="E2ETestDefaults.InstallJobTemplates"/> but never matched by a
/// connected <see cref="FakeAgentClient"/>. Created WorkItems therefore stay in
/// <see cref="WorkItemStatus.Pending"/> so status assertions are stable.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class AutoConsolidationE2ETests : E2ETestBase
{
    private const string BrainId = "brain-e2e";

    /// <summary>
    /// The IssueIdentifier that AutoConsolidationService assigns to brain consolidation WorkItems
    /// when triggered with BrainProviderId = <see cref="BrainId"/>.
    /// </summary>
    private static readonly string BrainScopeKey =
        ConsolidationService.ScopeKey(ConsolidationRunType.BrainConsolidation, BrainId, null);

    /// <summary>
    /// Captured at construction time (once per test, since xUnit creates a fresh class instance
    /// per test method). Used by <see cref="ConsolidationItemsAsync"/> to exclude WorkItems
    /// created by earlier tests that persist in the shared in-memory DB.
    /// </summary>
    private readonly DateTimeOffset _testStartedAt = DateTimeOffset.UtcNow;

    public AutoConsolidationE2ETests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Sets <c>AutoBrainConsolidationAfterRuns = threshold</c> and
    /// <c>AutoRefactoringScanAfterClosedIssues = 0</c>, then saves an enabled template
    /// "template-auto" (issue-e2e, repo-e2e, BrainProviderId = brain-e2e) — matching the
    /// seeding pattern of <c>ConsolidationPageTests.cs:76-90</c>.
    /// </summary>
    private async Task SeedAsync(int threshold)
    {
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with
            {
                AutoBrainConsolidationAfterRuns = threshold,
                AutoRefactoringScanAfterClosedIssues = 0
            },
            CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-auto",
            Name = "template-auto",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = BrainId,
            Enabled = true,
        }, CancellationToken.None);
    }

    /// <summary>
    /// Adds a completed Implementation run summary with <c>BrainInboxFile = "inbox/x.md"</c> and
    /// <c>BrainProviderConfigId = brain-e2e</c>. Each call uses a fresh RunId so the staleness
    /// counter increments correctly.
    /// </summary>
    private async Task AddRunWithInboxAsync()
    {
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier(Guid.NewGuid().ToString()),
            IssueTitle = "Implementation run",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            BrainInboxFile = "inbox/x.md",
            BrainProviderConfigId = BrainId,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAtOffset = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>Invokes one auto-consolidation check cycle on the Scheduler singleton directly.</summary>
    private Task RunOnceAsync() =>
        Fixture.SchedulerFactory.Services
            .GetRequiredService<AutoConsolidationService>()
            .RunOnceAsync(CancellationToken.None);

    /// <summary>
    /// Counts WorkItems with <c>TaskType == Consolidation</c> and
    /// <c>IssueIdentifier == "BrainConsolidation:brain-e2e"</c> created during this test.
    /// Filters by <c>IssueIdentifier</c> (to exclude other test classes' Consolidation items)
    /// and by <c>CreatedAt &gt;= _testStartedAt</c> (to exclude items created by earlier tests
    /// in this class that persist in the shared in-memory DB across tests).
    /// </summary>
    private async Task<int> ConsolidationItemsAsync()
    {
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        return await db.WorkItems.AsNoTracking()
            .CountAsync(w =>
                w.TaskType == WorkItemTaskType.Consolidation &&
                w.IssueIdentifier == BrainScopeKey &&
                w.CreatedAt >= _testStartedAt);
    }

    /// <summary>Seeds the global route to the "jc-e2e" job template so the trigger is routed.</summary>
    private async Task SeedRouteAsync()
    {
        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "auto-consol-profile",
            DisplayName = "Auto Consolidation Profile",
            JobTemplate = E2ERouting.Key("jc-e2e"),
            Enabled = true,
        });
    }

    // ── S5a — triggers at threshold ───────────────────────────────────────

    /// <summary>
    /// S5a: once the brain run count reaches the configured threshold, AutoConsolidationService
    /// creates exactly one consolidation WorkItem. A second <c>RunOnceAsync</c> call is
    /// idempotent — the partial unique index returns AlreadyExists and no second item is created.
    /// The Consolidation page shows the count and the "· auto" suffix when a valid route exists.
    ///
    /// <para>
    /// <b>Mutation check:</b> changing <c>count &gt;= threshold</c> to <c>count &gt; threshold</c>
    /// in <c>TryTriggerIfThresholdReachedAsync</c> makes this test fail (2 runs, threshold 2
    /// → 2 &gt; 2 is false → no WorkItem created).
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenBrainRunsReachTheThreshold_ShouldStartOneRun_AndShowTheCounter()
    {
        // Arrange
        await SeedAsync(threshold: 2);
        await SeedRouteAsync();
        await AddRunWithInboxAsync();
        await AddRunWithInboxAsync();

        // Assert (page, before trigger): counter shows 2/2 with "· auto" suffix
        var consolPage = new ConsolidationPage(Page, BaseUrl);
        await consolPage.NavigateAsync();
        var counterText = await Page.Locator("[data-testid=auto-counter-brain]").TextContentAsync();
        Assert.NotNull(counterText);
        Assert.Contains("2 / 2 runs with inbox since last review", counterText);
        Assert.Contains("· auto", counterText);

        // Act: trigger twice — second call must not throw (AlreadyExists is handled internally)
        await RunOnceAsync();
        await RunOnceAsync();

        // Assert (DB): exactly one Pending consolidation WorkItem for the brain scope
        Assert.Equal(1, await ConsolidationItemsAsync());

        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking()
            .SingleAsync(w =>
                w.TaskType == WorkItemTaskType.Consolidation &&
                w.IssueIdentifier == BrainScopeKey &&
                w.CreatedAt >= _testStartedAt);
        Assert.Equal(WorkItemStatus.Pending, item.Status);
    }

    // ── S5b — stays below threshold ───────────────────────────────────────

    /// <summary>
    /// S5b: when the run count is below the threshold, no WorkItem is created.
    /// The Consolidation page counter reflects the real count.
    /// </summary>
    [Fact]
    public async Task WhenBelowTheThreshold_ShouldNotStart()
    {
        // Arrange
        await SeedAsync(threshold: 2);
        await SeedRouteAsync();
        await AddRunWithInboxAsync();    // only 1 run, threshold is 2

        // Act
        await RunOnceAsync();

        // Assert (DB)
        Assert.Equal(0, await ConsolidationItemsAsync());

        // Assert (page): counter shows 1/2, "· auto" still present (route exists)
        var consolPage = new ConsolidationPage(Page, BaseUrl);
        await consolPage.NavigateAsync();
        var counterText = await Page.Locator("[data-testid=auto-counter-brain]").TextContentAsync();
        Assert.NotNull(counterText);
        Assert.Contains("1 / 2 runs with inbox since last review", counterText);
        Assert.Contains("· auto", counterText);
    }

    // ── S5c — route is refused, then routed ──────────────────────────────

    /// <summary>
    /// S5c: when no route is configured, <c>TriggerAutomaticAsync</c> throws
    /// <see cref="WorkItemRefusedException"/>. <c>TryTriggerBrainScopeAsync</c> catches it —
    /// <c>RunOnceAsync</c> does NOT propagate. The page shows "auto refused:". Once a valid
    /// route is added, the next call succeeds.
    /// </summary>
    [Fact]
    public async Task WhenTheRouteIsRefused_ShouldStartNothing_ShowWhy_AndStartOnceRouted()
    {
        // Arrange: threshold=1, one run, NO route seeded
        await SeedAsync(threshold: 1);
        await AddRunWithInboxAsync();

        // Act 1: trigger without a route — must not throw
        await RunOnceAsync();

        // Assert 1: nothing was created
        Assert.Equal(0, await ConsolidationItemsAsync());

        // Assert (page): "auto refused:" appears, "· auto" is absent
        var consolPage = new ConsolidationPage(Page, BaseUrl);
        await consolPage.NavigateAsync();

        var blockedText = await Page.Locator("[data-testid=auto-blocked-brain]").TextContentAsync();
        Assert.NotNull(blockedText);
        Assert.Contains("auto refused:", blockedText);

        var counterText = await Page.Locator("[data-testid=auto-counter-brain]").TextContentAsync();
        Assert.NotNull(counterText);
        Assert.DoesNotContain("· auto", counterText);

        // Act 2: seed the route and trigger again
        await SeedRouteAsync();
        await RunOnceAsync();

        // Assert 2: one item created after the route was added
        Assert.Equal(1, await ConsolidationItemsAsync());
    }

    // ── S5d — last run failed (paused) ────────────────────────────────────

    /// <summary>
    /// S5d: when the most recent consolidation run for the brain scope has
    /// <see cref="PipelineStep.Failed"/>, <c>IsPausedAsync</c> returns true and
    /// <c>TryTriggerIfThresholdReachedAsync</c> exits early — no WorkItem is created.
    /// </summary>
    [Fact]
    public async Task WhenTheLastRunFailed_ShouldNotStart()
    {
        // Arrange: threshold=1, one inbox run, and a failed consolidation run for the scope
        await SeedAsync(threshold: 1);
        await SeedRouteAsync();
        await AddRunWithInboxAsync();

        // Seed a failed consolidation run with the brain scope key so IsPausedAsync returns true.
        // Note: IssueIdentifier must match BrainScopeKey exactly.
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier(BrainScopeKey),
            IssueTitle = "Brain consolidation (failed)",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Consolidation,
            ConsolidationType = ConsolidationRunType.BrainConsolidation,
            StartedAtOffset = DateTimeOffset.UtcNow,
        });

        // Act
        await RunOnceAsync();

        // Assert: no WorkItem — the scope is paused after a failure
        Assert.Equal(0, await ConsolidationItemsAsync());
    }
}
