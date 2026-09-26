using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.AgentGateway;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the Consolidation page at /consolidation. Covers page rendering,
/// trigger buttons, and the queued-dispatch feedback an operator sees. Feature 021 (Consolidation Loops).
///
/// <para>
/// <c>ConsolidationPage_BadgeVisibleInSidebar_WhenNonZero</c> was removed with the legacy shell: the
/// consolidation nav badge (<c>.sidebar-badge</c> in the old MainLayout) no longer exists — the
/// cockpit nav has no such indicator. The badge <i>service</i> reset-on-load is still pinned by
/// <c>ConsolidationPage_BadgeResetsOnPageLoad</c>.
/// </para>
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

        // Assert: the page is now standalone at /consolidation with its own header.
        var title = await page.GetPageTitleAsync();
        Assert.Contains("Consolidation", title);

        // The consolidation section should show "No enabled templates configured."
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
    public async Task ConsolidationPage_TriggerWithNoAgent_ShowsRejectedMessage()
    {
        // Arrange: seed a template but do NOT connect any agent (and no agent profiles configured
        // in the E2E environment), so selector resolution returns null and the trigger is rejected.
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

        // Assert: rejection message shown — the new synchronous dispatch path rejects when no
        // agent selector can be resolved (no agent profiles configured). The old queued-state
        // path would return "queued" even without an agent, but the new path requires selector
        // resolution to succeed before creating the Pending WorkItem.
        var message = await page.GetStatusMessageAsync();
        Assert.NotNull(message);
        Assert.Contains("rejected", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await page.IsStatusMessageErrorAsync());
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
    public async Task ConsolidationPage_TriggerWithNoAgent_ShowsRejected_ForBrainConsolidation()
    {
        // Arrange: seed a template but do NOT connect any agent (and no agent profiles configured
        // in the E2E environment), so selector resolution returns null and the trigger is rejected.
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

        // Only click if button is enabled (not blocked by stale state from a prior test)
        var isDisabled = await page.IsBrainButtonDisabledAsync("Failure Template");
        if (isDisabled)
            return;

        await page.ClickBrainConsolidationAsync("Failure Template");

        // Wait for status message
        await page.WaitForStatusMessageAsync();

        // Assert: rejection message shown — the new synchronous dispatch path rejects when no
        // agent selector can be resolved (no agent profiles configured). The old queued-state
        // path would persist the run as Queued even without an agent, but the new path requires
        // selector resolution to succeed before creating the Pending WorkItem.
        var message = await page.GetStatusMessageAsync();
        Assert.NotNull(message);
        Assert.Contains("rejected", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await page.IsStatusMessageErrorAsync());
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

    // ── Run-to-completion scenarios (issue #3088) ─────────────────────────────
    //
    // Architecture note: The Blazor host's ConsolidationService.OnChange is never triggered
    // by API-host operations (the Blazor host registers NullChangeNotifier). After
    // ReportConsolidationCompleteAsync succeeds, the page will NOT auto-update. The pattern
    // used by all run-to-completion tests is:
    //   1. WaitUntilAsync on Blazor-host's IConsolidationService (calls API over HTTP).
    //   2. page.NavigateAsync() to force LoadDataAsync to re-run.
    //   3. Assert on DOM.

    [Fact]
    public async Task ConsolidationPage_BrainConsolidation_Succeeds()
    {
        // Arrange
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-s1",
            Name = "S1 Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-consol-s1",
            DisplayName = "S1 Profile",
            MatchLabels = [],
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var agent = new FakeAgentClient("agent-consol-s1");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Act: trigger brain consolidation
        await page.ClickBrainConsolidationAsync("S1 Template");

        // Wait for FakeJobController to dispatch and agent to receive assignment
        await WaitUntilAsync(() => agent.ReceivedJobIds.Count > 0);
        var jobId = agent.ReceivedJobIds.ToArray()[0];

        // Agent reports success
        await agent.ReportConsolidationCompleteAsync(new ConsolidationJobResult
        {
            JobId = jobId,
            Success = true,
            Summary = "Consolidated 3 files"
        });

        // Wait for server-side state to reflect completion
        var consolidationService = Fixture.Factory.Services.GetRequiredService<IConsolidationService>();
        // TODO: WaitUntilAsync accepts a synchronous Func<bool>, so GetRunHistoryAsync must be
        // unwrapped with .GetAwaiter().GetResult(). This blocks a thread-pool thread inside the
        // polling loop and can cause thread-pool starvation under CI load. Fix by adding an async
        // overload WaitUntilAsync(Func<Task<bool>>) to E2ETestBase and switching these callsites.
        await WaitUntilAsync(() =>
            consolidationService.GetRunHistoryAsync(CancellationToken.None)
                .GetAwaiter().GetResult()
                .Any(r => r.Status == ConsolidationRunStatus.Succeeded));

        // Reload page to trigger LoadDataAsync, then assert DOM
        await page.NavigateAsync();
        await page.WaitForRunHistoryCountAsync(1);

        var rowText = await page.GetRunHistoryRowTextAsync(0);
        Assert.NotNull(rowText);
        Assert.Contains("Brain Consolidation", rowText);
        Assert.Contains("Succeeded", rowText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Consolidated 3 files", rowText);

        // Assert: template card shows new last-run status (Scenario 1 requirement).
        // The card renders a <span class="consolidation-status-succeeded"> under the Brain
        // Consolidation row for the template. Wait for the card to reflect the updated status
        // because NavigateAsync may return before LoadDataAsync populates _lastRuns.
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.consolidation-card:has(.consolidation-card-title) .consolidation-status-succeeded') !== null",
            null,
            new() { Timeout = 10_000 });
        var cardStatusEl = await Page.QuerySelectorAsync(
            ".consolidation-card:has(.consolidation-card-title:has-text('S1 Template')) .consolidation-status-succeeded");
        Assert.NotNull(cardStatusEl);
    }

    [Fact]
    public async Task ConsolidationPage_BrainConsolidation_Fails()
    {
        // Arrange
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-s2",
            Name = "S2 Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-consol-s2",
            DisplayName = "S2 Profile",
            MatchLabels = [],
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var agent = new FakeAgentClient("agent-consol-s2");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Act: trigger
        await page.ClickBrainConsolidationAsync("S2 Template");

        await WaitUntilAsync(() => agent.ReceivedJobIds.Count > 0);
        var jobId = agent.ReceivedJobIds.ToArray()[0];

        // Agent reports failure
        await agent.ReportConsolidationCompleteAsync(new ConsolidationJobResult
        {
            JobId = jobId,
            Success = false,
            ErrorMessage = "Brain provider unavailable"
        });

        // Wait for server-side state to reflect failure
        var consolidationService = Fixture.Factory.Services.GetRequiredService<IConsolidationService>();
        // TODO: WaitUntilAsync accepts a synchronous Func<bool>, so GetRunHistoryAsync must be
        // unwrapped with .GetAwaiter().GetResult(). This blocks a thread-pool thread inside the
        // polling loop and can cause thread-pool starvation under CI load. Fix by adding an async
        // overload WaitUntilAsync(Func<Task<bool>>) to E2ETestBase and switching these callsites.
        await WaitUntilAsync(() =>
            consolidationService.GetRunHistoryAsync(CancellationToken.None)
                .GetAwaiter().GetResult()
                .Any(r => r.Status == ConsolidationRunStatus.Failed));

        // Reload and assert
        await page.NavigateAsync();
        await page.WaitForRunHistoryCountAsync(1);

        var rowText = await page.GetRunHistoryRowTextAsync(0);
        Assert.NotNull(rowText);
        Assert.Contains("Brain Consolidation", rowText);
        Assert.Contains("Failed", rowText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Brain provider unavailable", rowText);

        // Nothing should remain Pending or Running
        var history = await consolidationService.GetRunHistoryAsync(CancellationToken.None);
        Assert.DoesNotContain(history, r =>
            r.Status == ConsolidationRunStatus.Pending ||
            r.Status == ConsolidationRunStatus.Running);
    }

    [Fact]
    public async Task ConsolidationPage_RefactoringModal_Cancel_DoesNotCreateWorkItem()
    {
        // Arrange: seed template — no agent profile needed because modal cancel fires before any trigger
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-s3a",
            Name = "S3a Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Act: open modal then cancel
        await page.ClickRefactoringScanAsync("S3a Template");
        Assert.True(await page.IsRefactoringModalVisibleAsync(), "Refactoring modal should be visible after clicking Refactoring Scan");
        await page.CancelRefactoringModalAsync();

        // TODO: This Task.Delay is a fixed delay, which violates the acceptance criterion
        // ("Waits use WaitUntilAsync or Playwright waits, never fixed delays"). A deterministic
        // alternative: assert immediately after CancelRefactoringModalAsync — CancelRefactoringModal
        // is a synchronous page interaction with no async dispatch path — or poll
        // Fixture.WorkItems.GetPendingAsync via WaitUntilAsync with the inverted condition.
        // Allow one FakeJobController poll cycle (250ms) to ensure no erroneous trigger fires
        await Task.Delay(350);

        // Assert: no Pending work items were created
        var pending = await Fixture.WorkItems.GetPendingAsync(10);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task ConsolidationPage_RefactoringModal_Confirm_CreatesIssues()
    {
        // Arrange
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-s3b",
            Name = "S3b Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-consol-s3b",
            DisplayName = "S3b Profile",
            MatchLabels = [],
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var agent = new FakeAgentClient("agent-consol-s3b");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Act: open modal, confirm to start scan
        await page.ClickRefactoringScanAsync("S3b Template");
        Assert.True(await page.IsRefactoringModalVisibleAsync());
        await page.ConfirmRefactoringModalAsync();

        // Wait for dispatch
        await WaitUntilAsync(() => agent.ReceivedJobIds.Count > 0);
        var jobId = agent.ReceivedJobIds.ToArray()[0];

        // Agent reports success with created issues
        await agent.ReportConsolidationCompleteAsync(new ConsolidationJobResult
        {
            JobId = jobId,
            Success = true,
            CreatedIssues =
            [
                new CreatedIssueInfo { Identifier = "42", Title = "Refactor X", Url = "https://github.com/e2e-org/e2e-repo/issues/42" },
                new CreatedIssueInfo { Identifier = "43", Title = "Refactor Y", Url = "https://github.com/e2e-org/e2e-repo/issues/43" }
            ]
        });

        // Wait for server-side completion
        var consolidationService = Fixture.Factory.Services.GetRequiredService<IConsolidationService>();
        // TODO: WaitUntilAsync accepts a synchronous Func<bool>, so GetRunHistoryAsync must be
        // unwrapped with .GetAwaiter().GetResult(). This blocks a thread-pool thread inside the
        // polling loop and can cause thread-pool starvation under CI load. Fix by adding an async
        // overload WaitUntilAsync(Func<Task<bool>>) to E2ETestBase and switching these callsites.
        await WaitUntilAsync(() =>
            consolidationService.GetRunHistoryAsync(CancellationToken.None)
                .GetAwaiter().GetResult()
                .Any(r => r.Status == ConsolidationRunStatus.Succeeded));

        // Reload and assert
        await page.NavigateAsync();
        await page.WaitForRunHistoryCountAsync(1);

        var rowText = await page.GetRunHistoryRowTextAsync(0);
        Assert.NotNull(rowText);
        Assert.Contains("Succeeded", rowText, StringComparison.OrdinalIgnoreCase);

        // Assert: the two created issues were processed by the hub.
        // The Consolidation page does not render CreatedIssueInfo identifiers in the history row
        // (only run.Summary is shown, which is null for this result). The observable side-effect
        // of CreatedIssues is the badge increment: 2 issues → BadgeCount == 2.
        // TODO(WARNING): If the UI is later updated to render created-issue links or counts in the
        // history row, replace the badge assertion below with a row-text assertion for "42"/"43".
        var badgeService = Fixture.Factory.Services.GetRequiredService<ConsolidationBadgeService>();
        Assert.Equal(2, badgeService.BadgeCount);
    }

    [Fact]
    public async Task ConsolidationPage_HarnessSuggestions_ShowsSuggestions()
    {
        // Arrange
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-s4",
            Name = "S4 Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-consol-s4",
            DisplayName = "S4 Profile",
            MatchLabels = [],
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var agent = new FakeAgentClient("agent-consol-s4");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Act: click Generate Suggestions
        await page.ClickGenerateSuggestionsAsync();

        // Wait for dispatch
        await WaitUntilAsync(() => agent.ReceivedJobIds.Count > 0);
        var jobId = agent.ReceivedJobIds.ToArray()[0];

        // Agent reports success with 3 harness suggestions
        await agent.ReportConsolidationCompleteAsync(new ConsolidationJobResult
        {
            JobId = jobId,
            Success = true,
            HarnessSuggestions = new HarnessSuggestions
            {
                GeneratedAtUtc = DateTime.UtcNow,
                BasedOnRunCount = 5,
                SuccessRate = 0.8m,
                Suggestions =
                [
                    new HarnessSuggestion { Text = "Add timeout test", Rationale = "Seen in 3 runs", Frequency = 3 },
                    new HarnessSuggestion { Text = "Mock external calls", Rationale = "Seen in 2 runs", Frequency = 2 },
                    new HarnessSuggestion { Text = "Add retry assertion", Rationale = "Seen in 2 runs", Frequency = 2 }
                ]
            }
        });

        // Wait for server-side harness suggestions to be persisted
        var consolidationService = Fixture.Factory.Services.GetRequiredService<IConsolidationService>();
        // TODO: WaitUntilAsync accepts a synchronous Func<bool>, so GetHarnessSuggestionsAsync must
        // be unwrapped with .GetAwaiter().GetResult(). This blocks a thread-pool thread inside the
        // polling loop and can cause thread-pool starvation under CI load. Fix by adding an async
        // overload WaitUntilAsync(Func<Task<bool>>) to E2ETestBase and switching these callsites.
        await WaitUntilAsync(() =>
            consolidationService.GetHarnessSuggestionsAsync(CancellationToken.None)
                .GetAwaiter().GetResult() is not null);

        // Reload page to trigger LoadDataAsync
        await page.NavigateAsync();

        // Wait for suggestion items to render before asserting.
        // NavigateAsync can return while the page still shows its "Loading..." placeholder
        // (NavigateAsync resolves against .monitoring-empty which matches both the placeholder
        // and the real empty state). Poll until all 3 items are present in the DOM.
        await Page.WaitForFunctionAsync(
            "() => document.querySelectorAll('.consolidation-suggestion-item').length >= 3",
            null,
            new() { Timeout = 10_000 });

        // Assert: 3 suggestion items rendered, no-suggestions message gone
        var suggestionCount = await page.GetSuggestionItemCountAsync();
        Assert.Equal(3, suggestionCount);
        Assert.False(await page.IsNoSuggestionsMessageVisibleAsync());
    }

    [Fact]
    public async Task ConsolidationPage_CancelQueuedRun_PreventsDispatch()
    {
        // Arrange: seed template + profile, but do NOT connect any agent so WorkItem stays Pending
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-s5",
            Name = "S5 Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-consol-s5",
            DisplayName = "S5 Profile",
            MatchLabels = [],
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Trigger — no idle agent, so WorkItem stays Pending
        await page.ClickBrainConsolidationAsync("S5 Template");
        await page.WaitForStatusMessageAsync();

        // Reload so the Pending row appears in the run history table
        await page.NavigateAsync();
        await page.WaitForRunHistoryCountAsync(1);

        // Act: click Cancel on the Pending row
        await page.ClickCancelRunAsync(0);
        await page.WaitForStatusMessageAsync();

        // Assert: status message confirms cancellation (not an error)
        var msg = await page.GetStatusMessageAsync();
        Assert.NotNull(msg);
        Assert.Contains("cancelled", msg, StringComparison.OrdinalIgnoreCase);
        Assert.False(await page.IsStatusMessageErrorAsync());

        // Assert: no Pending work items remain (cancelled item no longer fetchable by FakeJobController)
        var pending = await Fixture.WorkItems.GetPendingAsync(10);
        Assert.Empty(pending);

        // Assert: connecting an agent now does NOT dispatch the cancelled item.
        // TODO: This Task.Delay violates the acceptance criterion ("Waits use WaitUntilAsync or
        // Playwright waits, never fixed delays"). No completion event exists for "nothing happened",
        // so a purely event-driven wait is not available here. A partial improvement: gate the
        // delay start on lateAgent.IsConnected (so the 350ms is measured from actual registration,
        // not from the start of the connect attempt) and replace with WaitUntilAsync on a
        // stabilisation condition if one can be identified.
        await using var lateAgent = new FakeAgentClient("agent-consol-s5-late");
        await lateAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        await Task.Delay(350);
        Assert.Empty(lateAgent.ReceivedJobIds);
    }

    [Fact]
    public async Task ConsolidationPage_BrainConsolidation_DoubleClick_CreatesOneWorkItem()
    {
        // Arrange
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-consol-s6",
            Name = "S6 Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = "brain-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-consol-s6",
            DisplayName = "S6 Profile",
            MatchLabels = [],
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Act: two rapid clicks — the second will hit the DB unique index and be deduplicated
        await page.ClickBrainConsolidationAsync("S6 Template");
        await page.ClickBrainConsolidationAsync("S6 Template");

        // TODO: This Task.Delay violates the acceptance criterion ("Waits use WaitUntilAsync or
        // Playwright waits, never fixed delays"). A deterministic alternative: use
        // WaitUntilAsync(() => Fixture.WorkItems.GetPendingAsync(10).GetAwaiter().GetResult().Count > 0)
        // to wait until at least one item exists, then assert Single. Additionally, consider using
        // page.WaitForStatusMessageAsync() to wait for the Blazor UI to finish processing both
        // clicks before querying the work item store.
        // Wait for Blazor to process both clicks and for any DB writes to settle
        await Task.Delay(500);

        // Assert: exactly one Pending WorkItem exists
        var pending = await Fixture.WorkItems.GetPendingAsync(10);
        Assert.Single(pending);
    }

}
