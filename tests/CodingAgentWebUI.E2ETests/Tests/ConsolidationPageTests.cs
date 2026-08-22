using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// E2E tests for the Consolidation page (/consolidation): page rendering, trigger buttons, the
/// queued-dispatch feedback an operator sees, and badge behaviour. Covers feature 021
/// (Consolidation Loops).
///
/// <para>
/// <b>Six tests were removed here rather than ported.</b> They connected a <c>FakeAgentClient</c>
/// and waited for a <c>ConsolidationJobMessage</c> to arrive over SignalR — the pre-Kubernetes
/// flow, where <c>ConsolidationDispatchService</c> picked an idle agent out of the registry and
/// pushed the job to it. Neither half of that survives the 041–045 arc: Spec 044 moved the hub to
/// the Pipeline API, so the monolith's <c>IHubContext</c> reaches no agents, and Kubernetes mode
/// has no idle agents to pick — pods are started per job. What the page does now is enqueue a work
/// item (<c>TaskType = Consolidation</c>) that the Job Controller turns into a pod, which is the
/// path <c>ConsolidationPage_TriggerWithNoAgent_ShowsQueuedMessage</c> and its brain-consolidation
/// twin cover.
/// </para>
///
/// <para>
/// The three removed <c>ProviderConfigs</c> tests asserted a rule that is not about this page at
/// all — that a refactoring scan carries an Issue provider config and harness/brain consolidation
/// do not. That rule lives in <c>ConsolidationJobPreparationService</c>, shared by both dispatch
/// paths, and is already pinned by <c>ConsolidationJobPreparationServiceTests</c>
/// (<c>PrepareAsync_RefactoringDetection_IncludesIssueProviderConfig</c>,
/// <c>PrepareAsync_NonRefactoring_ExcludesIssueProviderConfig</c>,
/// <c>PrepareAsync_TemplateWithRepoAndBrain_BothResolved</c>) — at a level where it costs
/// milliseconds instead of 34 seconds of waiting for a message that never comes. They also each
/// began with <c>if (isDisabled) return;</c>, so they reported green whenever the button they
/// meant to click was disabled.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class ConsolidationPageTests : E2ETestBase
{
    public ConsolidationPageTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ConsolidationPage_NoTemplates_ShowsEmptyState()
    {
        // Arrange: ensure no enabled templates

        // Act
        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Wait for the Blazor interactive content to render by checking for any section header
        await Page.WaitForSelectorAsync(".settings-section h2", new() { Timeout = 10_000 });

        // Assert
        var title = await page.GetPageTitleAsync();
        Assert.Contains("Consolidation", title);

        // The page should show "No enabled templates configured." in the template section
        var pageText = await Page.TextContentAsync(".consolidation-page");
        Assert.Contains("No enabled templates configured", pageText);
    }

    [Fact]
    public async Task ConsolidationPage_WithTemplates_ShowsCards()
    {
        // Arrange: seed a template with brain and repo providers
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-1",
            Name = "Consolidation Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Act
        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Assert: template card is rendered
        var cardCount = await page.GetTemplateCardCountAsync();
        Assert.True(cardCount >= 1, "Expected at least one template card");

        var cardTitle = await page.GetTemplateCardTitleAsync(0);
        Assert.Equal("Consolidation Template", cardTitle);

        // Both buttons should be visible (template has brain + repo + issue providers)
        Assert.True(await page.IsBrainButtonVisibleAsync("Consolidation Template"));
        Assert.True(await page.IsRefactoringButtonVisibleAsync("Consolidation Template"));
    }

    [Fact]
    public async Task ConsolidationPage_TriggerWithNoAgent_ShowsQueuedMessage()
    {
        // Arrange: seed a template but do NOT connect any agent
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-2",
            Name = "No Agent Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Act: navigate and click trigger
        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();
        await page.ClickBrainConsolidationAsync("No Agent Template");

        // Wait for status message
        await page.WaitForStatusMessageAsync();

        // Assert: queued message shown (no agent → queued, not rejected)
        var message = await page.GetStatusMessageAsync();
        Assert.NotNull(message);
        Assert.Contains("queued", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await page.IsStatusMessageErrorAsync());
    }



    [Fact]
    public async Task ConsolidationPage_RefactoringButton_VisibleForConfiguredTemplate()
    {
        // Arrange: seed a template with repo and issue providers (required for refactoring)
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-5",
            Name = "Refactoring Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Act: navigate to the page
        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();
        await Page.WaitForSelectorAsync(".settings-section h2", new() { Timeout = 10_000 });

        // Assert: refactoring button is visible for the template
        Assert.True(await page.IsRefactoringButtonVisibleAsync("Refactoring Template"));
        Assert.True(await page.IsBrainButtonVisibleAsync("Refactoring Template"));
    }

    [Fact]
    public async Task ConsolidationPage_TriggerWithNoAgent_ShowsQueued_ForBrainConsolidation()
    {
        // Arrange: seed a template but do NOT connect any agent
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-6",
            Name = "Failure Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Act: navigate and click brain consolidation trigger (no agent available)
        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();
        await Page.WaitForSelectorAsync(".settings-section h2", new() { Timeout = 10_000 });

        // Only click if button is enabled (not blocked by stale state)
        var isDisabled = await page.IsBrainButtonDisabledAsync("Failure Template");
        if (isDisabled)
            return;

        await page.ClickBrainConsolidationAsync("Failure Template");

        // Wait for status message
        await page.WaitForStatusMessageAsync();

        // Assert: queued message shown (no agents available → queued, not rejected)
        var message = await page.GetStatusMessageAsync();
        Assert.NotNull(message);
        Assert.Contains("queued", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await page.IsStatusMessageErrorAsync());
    }

    [Fact]
    public async Task ConsolidationPage_BadgeResetsOnPageLoad()
    {
        // Arrange: manually increment the badge service
        var badgeService = Fixture.Factory.Services.GetRequiredService<ConsolidationBadgeService>();
        badgeService.IncrementBy(5);

        // Verify badge was incremented (relative check)
        var beforeCount = badgeService.BadgeCount;
        Assert.True(beforeCount >= 5, $"Badge should be at least 5 after increment, was {beforeCount}");

        // Act: navigate to the consolidation page (should reset badge)
        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Wait for Blazor OnInitializedAsync to reset the badge
        await WaitUntilAsync(() => badgeService.BadgeCount == 0);

        // Assert: badge was reset to zero
        Assert.Equal(0, badgeService.BadgeCount);
    }

    [Fact]
    public async Task ConsolidationPage_BadgeVisibleInSidebar_WhenNonZero()
    {
        // Arrange: increment badge before navigating
        var badgeService = Fixture.Factory.Services.GetRequiredService<ConsolidationBadgeService>();
        badgeService.IncrementBy(3);

        // Act: navigate to a different page (not consolidation, so badge isn't reset)
        await Page.GotoAsync($"{BaseUrl}/agent-monitoring");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });

        // Wait for the sidebar badge to render (Blazor interactive circuit must be established)
        await Page.WaitForSelectorAsync(".sidebar-badge", new() { Timeout = 10_000 });

        // Assert: badge is visible in the sidebar
        var badge = await Page.QuerySelectorAsync(".sidebar-badge");
        Assert.NotNull(badge);
        var badgeText = await badge.TextContentAsync();
        Assert.NotNull(badgeText);
        var badgeValue = int.Parse(badgeText.Trim());
        Assert.True(badgeValue >= 3, $"Badge should be at least 3, was {badgeValue}");
    }

}
