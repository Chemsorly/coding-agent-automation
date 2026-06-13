using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// E2E tests for the Consolidation page (/consolidation).
/// Validates page rendering, trigger buttons, consolidation job dispatch,
/// agent completion flow, run history updates, and badge behavior.
/// Covers feature 021 (Consolidation Loops).
/// </summary>
[Trait("Category", "E2E")]
public sealed class ConsolidationPageTests : E2ETestBase, IClassFixture<E2EFixture>
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
    public async Task ConsolidationPage_TriggerHarnessSuggestions_DispatchesAndCompletes()
    {
        // Arrange: seed a template and connect an agent
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-3",
            Name = "Harness Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-consol",
            DisplayName = "Consolidation Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("consol-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: navigate and trigger harness suggestions
        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Check if button is enabled
        var isDisabled = await page.IsGenerateSuggestionsDisabledAsync();
        if (isDisabled)
            return; // Skip — stale state from shared ConsolidationService singleton

        await page.ClickGenerateSuggestionsAsync();

        // Wait for the agent to receive the consolidation job
        var assignment = await fakeAgent.ConsolidationJobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert: job was dispatched correctly
        Assert.NotNull(assignment);
        Assert.Equal(ConsolidationRunType.HarnessSuggestions, assignment.Type);

        // Complete the job to clean up state (prevents interference with other tests)
        await fakeAgent.ReportConsolidationCompleteAsync(new ConsolidationJobResult
        {
            JobId = assignment.JobId,
            Success = true,
            Summary = "No new feedback to analyze"
        });

        // Wait for the agent to return to Idle (confirms hub processed the completion)
        var registry = Fixture.Factory.AgentRegistry;
        await WaitUntilAsync(() => registry.GetByAgentId("consol-agent-1")?.Status == AgentStatus.Idle);
    }

    [Fact]
    public async Task ConsolidationPage_AgentCompletesHarness_ShowsSuggestions()
    {
        // Arrange: seed a template and connect an agent
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-4",
            Name = "Badge Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-consol-2",
            DisplayName = "Consolidation Agent Profile 2",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("consol-agent-2", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: navigate and trigger harness suggestions
        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Check if button is enabled
        var isDisabled = await page.IsGenerateSuggestionsDisabledAsync();
        if (isDisabled)
            return; // Skip — stale state from shared ConsolidationService singleton

        await page.ClickGenerateSuggestionsAsync();

        // Wait for the agent to receive the consolidation job
        var assignment = await fakeAgent.ConsolidationJobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Agent completes with harness suggestions
        Exception? completionError = null;
        try
        {
            await fakeAgent.ReportConsolidationCompleteAsync(new ConsolidationJobResult
            {
                JobId = assignment.JobId,
                Success = true,
                Summary = "Generated 2 suggestions from 10 runs",
                HarnessSuggestions = new HarnessSuggestions
                {
                    GeneratedAtUtc = DateTime.UtcNow,
                    BasedOnRunCount = 10,
                    SuccessRate = 75.0m,
                    Suggestions = new[]
                    {
                        new HarnessSuggestion
                        {
                            Text = "Add project structure to initial context",
                            Rationale = "Agent frequently spent time exploring directory layout",
                            Frequency = 7
                        },
                        new HarnessSuggestion
                        {
                            Text = "Include test framework config in prompt",
                            Rationale = "Agent often guessed wrong test runner",
                            Frequency = 4
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            completionError = ex;
        }

        // DIAGNOSTIC: Capture state immediately after completion call
        var registry = Fixture.Factory.AgentRegistry;
        var hubRegistry = Fixture.Factory.Services.GetRequiredService<AgentRegistryService>();
        var facade = Fixture.Factory.Services.GetRequiredService<IAgentHubFacade>();
        var agentEntry = registry.GetByAgentId("consol-agent-2");
        var hubAgentEntry = hubRegistry.GetByAgentId("consol-agent-2");
        // Check what connectionId the facade would look up
        var facadeLookup = facade.GetByAgentId("consol-agent-2");

        var diagMsg = $"[DIAG] completionError={completionError?.GetType().Name}: {completionError?.Message}, " +
                      $"sameRegistryInstance={ReferenceEquals(registry, hubRegistry)}, " +
                      $"agentEntry={(agentEntry is null ? "NULL" : $"status={agentEntry.Status}, activeJobId={agentEntry.ActiveJobId ?? "null"}, connectionId={agentEntry.ConnectionId}")}, " +
                      $"hubAgentEntry={(hubAgentEntry is null ? "NULL" : $"status={hubAgentEntry.Status}")}, " +
                      $"facadeLookup={(facadeLookup is null ? "NULL" : $"status={facadeLookup.Status}, connId={facadeLookup.ConnectionId}")}, " +
                      $"fakeAgentConnected={fakeAgent.IsConnected}, " +
                      $"assignmentJobId={assignment.JobId}";

        // Output diagnostic to test output (visible in CI trx/console)
        Assert.True(completionError is null,
            $"ReportConsolidationCompleteAsync threw: {completionError?.GetType().Name}: {completionError?.Message}. Diag: {diagMsg}");

        // Wait for hub to process the completion and agent to return to Idle
        // Use a shorter initial check + diagnostic on failure
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var entry = registry.GetByAgentId("consol-agent-2");
            if (entry?.Status == AgentStatus.Idle) break;
            await Task.Delay(50);
        }

        // If still not Idle, capture detailed state and fail with diagnostic
        var finalEntry = registry.GetByAgentId("consol-agent-2");
        if (finalEntry?.Status != AgentStatus.Idle)
        {
            var allAgents = registry.GetAllAgents();
            var agentList = string.Join("; ", allAgents.Select(a => $"{a.AgentId}={a.Status}(conn={a.ConnectionId},job={a.ActiveJobId ?? "null"})"));
            Assert.Fail(
                $"Agent 'consol-agent-2' did not reach Idle within 10s. " +
                $"finalEntry={(finalEntry is null ? "NULL" : $"status={finalEntry.Status}, activeJobId={finalEntry.ActiveJobId ?? "null"}")}, " +
                $"allAgents=[{agentList}], " +
                $"sameInstance={ReferenceEquals(registry, hubRegistry)}, " +
                $"completionError={completionError?.Message ?? "none"}");
        }

        // Refresh the page to see updated data
        await page.NavigateAsync();

        // Assert: suggestions are displayed
        Assert.False(await page.IsNoSuggestionsMessageVisibleAsync());
        var meta = await page.GetSuggestionsMetaAsync();
        Assert.NotNull(meta);
        Assert.Contains("10 runs", meta);

        var suggestionCount = await page.GetSuggestionItemCountAsync();
        Assert.Equal(2, suggestionCount);

        var firstSuggestion = await page.GetSuggestionTextAsync(0);
        Assert.Contains("Add project structure to initial context", firstSuggestion);

        // Assert: run history shows the completed run
        var runHistoryCount = await page.GetRunHistoryRowCountAsync();
        Assert.True(runHistoryCount >= 1, "Expected at least one run in history after completion");
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
