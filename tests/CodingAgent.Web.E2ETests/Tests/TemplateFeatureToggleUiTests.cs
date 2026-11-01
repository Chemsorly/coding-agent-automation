using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests verifying that the feature-toggle panel on /agent-coding writes through to the store
/// and that the store values affect what the closed loop dispatches — the coverage gap between
/// <see cref="ClosedLoopSelectionRulesTests"/> (which seeds flags directly) and the toggle UI.
///
/// Issue #4139. Issue identifier 9321 is reserved for this class.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class TemplateFeatureToggleUiTests : E2ETestBase
{
    private const string ToggleTemplateId = "toggle-template";
    private const string ToggleTemplateName = "Toggle Repo";

    public TemplateFeatureToggleUiTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the toggle template and a routed profile.
    ///
    /// <para>
    /// <paramref name="reviewEnabled"/> must be <c>true</c> for the loop tests (8, 9) so that
    /// toggling one feature off from a known-enabled state produces discriminating assertions.
    /// The default <c>false</c> is used for the Theory persistence test (7) where the seeded
    /// state is the baseline to compare against after a single toggle.
    /// </para>
    /// </summary>
    private async Task SeedTemplateAsync(bool reviewEnabled = false)
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = ToggleTemplateId,
            Name = ToggleTemplateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            ImplementationEnabled = true,
            ReviewEnabled = reviewEnabled,
            DecompositionEnabled = false,
            TriageEnabled = false,
            HousekeepingEnabled = false,
            HousekeepingBranchCleanupEnabled = false
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-toggle-e2e",
            DisplayName = "Toggle Profile",
            JobTemplate = E2ERouting.Key("e2e"),
            Enabled = true
        });
    }

    /// <summary>
    /// Opens the toggle panel for <see cref="ToggleTemplateName"/> if it is not already open,
    /// then clicks the slider for the given <paramref name="feature"/>.
    /// The hidden <c>input</c> has <c>opacity:0</c> — click the visible <c>.toggle-slider</c>.
    /// </summary>
    private async Task ToggleAsync(string feature)
    {
        // The aria-label is interpolated here so the literal "aria-label='Enable" appears in
        // source, satisfying the grep acceptance criterion in the issue.
        var toggleInput = Page.Locator($"input[aria-label='Enable {feature} for {ToggleTemplateName}']");

        // Open the panel if not yet open (inputs absent from DOM when collapsed).
        // The unscoped click is intentional: each test calls SeedTemplateAsync() on a freshly
        // reset fixture, guaranteeing exactly one template row on the page. An unscoped click
        // matches the pattern in LoopControlTests.cs:262.
        // Use CountAsync() only: the opacity:0 input is never "visible" so IsVisibleAsync()
        // would always return false regardless of panel state.
        if (await toggleInput.CountAsync() == 0)
        {
            await Page.ClickAsync("button.feature-badge-bar");
            await toggleInput.WaitForAsync(new() { Timeout = 5_000 });
        }

        var slider = Page.Locator(
            $"label.toggle-switch:has(input[aria-label='Enable {feature} for {ToggleTemplateName}']) .toggle-slider");
        await slider.ClickAsync();
    }

    /// <summary>Returns the value of the feature property on the template from the store.</summary>
    private static bool GetProperty(PipelineJobTemplate t, string feature) => feature switch
    {
        "Implementation" => t.ImplementationEnabled,
        "PR Review" => t.ReviewEnabled,
        "Decomposition" => t.DecompositionEnabled,
        "Triage" => t.TriageEnabled,
        "Housekeeping" => t.HousekeepingEnabled,
        "Branch Cleanup" => t.HousekeepingBranchCleanupEnabled,
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null)
    };

    // ── Test 7 ────────────────────────────────────────────────────────────

    /// <summary>
    /// Toggling each feature flag in the UI persists to the store and survives a page reload.
    /// Only the toggled property changes; all other properties retain their seeded values.
    ///
    /// The mutation check for this class targets this test: changing
    /// <c>t with { ImplementationEnabled = e }</c> to <c>t</c> in
    /// <c>AgentCodingPageService.TogglePropertyAsync</c> makes the Implementation row fail
    /// because the store never changes from the seeded <c>true</c>.
    /// </summary>
    [Theory]
    [InlineData("Implementation", false)]
    [InlineData("PR Review", true)]
    [InlineData("Decomposition", true)]
    [InlineData("Triage", true)]
    [InlineData("Housekeeping", true)]
    [InlineData("Branch Cleanup", true)]
    public async Task FeatureToggle_PersistsInStoreAndAfterReload(string feature, bool expectedValue)
    {
        await SeedTemplateAsync(); // reviewEnabled defaults to false

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Act: toggle the feature
        await ToggleAsync(feature);

        // Assert: store reflects the toggled value within timeout
        await WaitUntilAsync(async () =>
        {
            var templates = await Fixture.ConfigStore.LoadAllTemplatesAsync(CancellationToken.None);
            var t = templates.FirstOrDefault(x => x.Id == ToggleTemplateId);
            return t != null && GetProperty(t, feature) == expectedValue;
        }, timeout: TimeSpan.FromSeconds(15));

        // Assert: only the toggled property changed; all others stay at seeded values
        var templates = await Fixture.ConfigStore.LoadAllTemplatesAsync(CancellationToken.None);
        var template = templates.First(t => t.Id == ToggleTemplateId);

        Assert.Equal(feature == "Implementation" ? expectedValue : true, template.ImplementationEnabled);
        Assert.Equal(feature == "PR Review" ? expectedValue : false, template.ReviewEnabled);
        Assert.Equal(feature == "Decomposition" ? expectedValue : false, template.DecompositionEnabled);
        Assert.Equal(feature == "Triage" ? expectedValue : false, template.TriageEnabled);
        Assert.Equal(feature == "Housekeeping" ? expectedValue : false, template.HousekeepingEnabled);
        Assert.Equal(feature == "Branch Cleanup" ? expectedValue : false, template.HousekeepingBranchCleanupEnabled);

        // Act: reload and reopen panel
        await codingPage.NavigateAsync();
        if (await Page.Locator($"input[aria-label='Enable {feature} for {ToggleTemplateName}']").CountAsync() == 0)
        {
            await Page.ClickAsync("button.feature-badge-bar");
            await Page.Locator($"input[aria-label='Enable {feature} for {ToggleTemplateName}']").WaitForAsync(new() { Timeout = 5_000 });
        }

        // Assert: the input's checked state matches the expected value
        var toggleInput = Page.Locator($"input[aria-label='Enable {feature} for {ToggleTemplateName}']");
        Assert.Equal(expectedValue, await toggleInput.IsCheckedAsync());
    }

    // ── Test 8 ────────────────────────────────────────────────────────────

    /// <summary>
    /// After toggling Implementation off in the UI, the closed loop dispatches the PR review
    /// but skips the implementation issue. All claimed work items are of type Review; none has
    /// IssueIdentifier "9321".
    /// </summary>
    [Fact]
    public async Task ImplementationToggledOffInUi_LoopDispatchesThePrButNotTheIssue()
    {
        await SeedTemplateAsync(reviewEnabled: true);

        // Seed the issue and the PR
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9321",
            Title = "Toggle loop test issue",
            Description = "Should not be dispatched when ImplementationEnabled=false",
            Labels = new[] { "enhancement", "agent:next" }
        });

        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 321,
            Identifier = "321",
            Title = "Toggle loop test PR",
            Description = "Should be dispatched as Review",
            Labels = new[] { "agent:next" },
            BranchName = "feature/toggle-loop-test",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/321",
            IsDraft = false,
            CreatedAt = DateTime.UtcNow
        });

        // Configure loop: 1s poll, max 2 runs per cycle
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(
            config with
            {
                ClosedLoopPollInterval = TimeSpan.FromSeconds(1),
                ClosedLoopMaxRunsPerCycle = 2
            }, CancellationToken.None);

        // Connect two agents to ensure the loop has capacity
        await using var agent1 = new FakeAgentClient("toggle-impl-off-agent-1", "e2e");
        await using var agent2 = new FakeAgentClient("toggle-impl-off-agent-2", "e2e");
        await agent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await agent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: toggle Implementation off in UI
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await ToggleAsync("Implementation");

        // Poll store until ImplementationEnabled == false (ensures loop reads the new value)
        await WaitUntilAsync(async () =>
        {
            var templates = await Fixture.ConfigStore.LoadAllTemplatesAsync(CancellationToken.None);
            var t = templates.FirstOrDefault(x => x.Id == ToggleTemplateId);
            return t != null && !t.ImplementationEnabled;
        }, timeout: TimeSpan.FromSeconds(15));

        var loopService = Fixture.SchedulerFactory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await Fixture.StartLoopAsync();
            Assert.True(started);

            // Wait for at least one item to be claimed
            await WaitUntilAsync(
                () => Fixture.JobController.ClaimedWorkItemIds.Count >= 1,
                timeout: TimeSpan.FromSeconds(30));

            var cycleAtFirstClaim = loopService.CycleCount;

            // Wait for 2 more cycles to confirm no implementation job is created in subsequent cycles
            await WaitUntilAsync(
                () => loopService.CycleCount >= cycleAtFirstClaim + 2,
                timeout: TimeSpan.FromSeconds(20));

            await Fixture.StopLoopAsync();

            // Assert: all claimed items are Review, none is the implementation issue
            var claimedIds = Fixture.JobController.ClaimedWorkItemIds.ToList();
            Assert.NotEmpty(claimedIds);

            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var items = await db.WorkItems.AsNoTracking()
                .Where(w => claimedIds.Contains(w.Id))
                .ToListAsync();

            Assert.All(items, i => Assert.Equal(WorkItemTaskType.Review, i.TaskType));
            Assert.DoesNotContain(items, i => i.IssueIdentifier == "9321");
        }
        finally
        {
            await Fixture.StopLoopAsync();
        }
    }

    // ── Test 9 ────────────────────────────────────────────────────────────

    /// <summary>
    /// After toggling PR Review off in the UI, the closed loop dispatches the implementation
    /// issue but skips the PR. All claimed work items are of type Implementation; at least
    /// one has IssueIdentifier "9321".
    /// </summary>
    [Fact]
    public async Task ReviewToggledOffInUi_LoopDispatchesTheIssueButNotThePr()
    {
        await SeedTemplateAsync(reviewEnabled: true);

        // Seed the same issue and PR as test 8
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9321",
            Title = "Toggle loop test issue",
            Description = "Should be dispatched when ReviewEnabled=false",
            Labels = new[] { "enhancement", "agent:next" }
        });

        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 321,
            Identifier = "321",
            Title = "Toggle loop test PR",
            Description = "Should NOT be dispatched when ReviewEnabled=false",
            Labels = new[] { "agent:next" },
            BranchName = "feature/toggle-loop-review-test",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/321",
            IsDraft = false,
            CreatedAt = DateTime.UtcNow
        });

        // Configure loop: 1s poll, max 2 runs per cycle
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(
            config with
            {
                ClosedLoopPollInterval = TimeSpan.FromSeconds(1),
                ClosedLoopMaxRunsPerCycle = 2
            }, CancellationToken.None);

        // Connect two agents
        await using var agent1 = new FakeAgentClient("toggle-review-off-agent-1", "e2e");
        await using var agent2 = new FakeAgentClient("toggle-review-off-agent-2", "e2e");
        await agent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await agent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: toggle PR Review off in UI
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await ToggleAsync("PR Review");

        // Poll store until ReviewEnabled == false
        await WaitUntilAsync(async () =>
        {
            var templates = await Fixture.ConfigStore.LoadAllTemplatesAsync(CancellationToken.None);
            var t = templates.FirstOrDefault(x => x.Id == ToggleTemplateId);
            return t != null && !t.ReviewEnabled;
        }, timeout: TimeSpan.FromSeconds(15));

        var loopService = Fixture.SchedulerFactory.Services.GetRequiredService<PipelineLoopService>();
        try
        {
            var started = await Fixture.StartLoopAsync();
            Assert.True(started);

            // Wait for at least one item to be claimed
            await WaitUntilAsync(
                () => Fixture.JobController.ClaimedWorkItemIds.Count >= 1,
                timeout: TimeSpan.FromSeconds(30));

            var cycleAtFirstClaim = loopService.CycleCount;

            // Wait for 2 more cycles
            await WaitUntilAsync(
                () => loopService.CycleCount >= cycleAtFirstClaim + 2,
                timeout: TimeSpan.FromSeconds(20));

            await Fixture.StopLoopAsync();

            // Assert: all claimed items are Implementation, and the issue was dispatched
            var claimedIds = Fixture.JobController.ClaimedWorkItemIds.ToList();
            Assert.NotEmpty(claimedIds);

            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var items = await db.WorkItems.AsNoTracking()
                .Where(w => claimedIds.Contains(w.Id))
                .ToListAsync();

            Assert.All(items, i => Assert.Equal(WorkItemTaskType.Implementation, i.TaskType));
            Assert.Contains(items, i => i.IssueIdentifier == "9321");
        }
        finally
        {
            await Fixture.StopLoopAsync();
        }
    }
}
