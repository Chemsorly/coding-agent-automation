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
/// began with an early-return guard on <c>IsBrainButtonDisabledAsync</c>, so they reported green whenever the button they
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
        var pageText = await Page.TextContentAsync(".cockpit-page");
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
        // Arrange: seed a template but do NOT connect any agent and seed no route, so the trigger is refused.
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

        // Assert: the refusal names its reason (spec 053 req 2.9): with no route, the API refuses to create the
        // Pending WorkItem, and the page shows the routing text instead of a generic rejection.
        var message = await page.GetStatusMessageAsync();
        Assert.NotNull(message);
        Assert.Contains(CodingAgent.Pipeline.Services.AgentRouteRefusalReason.NoRouteText, message);
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
        // Arrange: seed a template but do NOT connect any agent and seed no route, so the trigger is refused.
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

        Assert.False(await page.IsBrainButtonDisabledAsync("Failure Template"),
            "The Brain Consolidation button must be enabled: nothing is running and the brain is not read-only");

        await page.ClickBrainConsolidationAsync("Failure Template");

        // Wait for status message
        await page.WaitForStatusMessageAsync();

        // Assert: the refusal names its reason (spec 053 req 2.9): with no route, the API refuses to create the
        // Pending WorkItem, and the page shows the routing text instead of a generic rejection.
        var message = await page.GetStatusMessageAsync();
        Assert.NotNull(message);
        Assert.Contains(CodingAgent.Pipeline.Services.AgentRouteRefusalReason.NoRouteText, message);
        Assert.True(await page.IsStatusMessageErrorAsync());
    }

    // ── Run-to-completion scenarios (issue #3088) ─────────────────────────────
    //
    // Architecture note: The Blazor host's ConsolidationService.OnChange is never triggered
    // by API-host operations (they run in another process). After
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

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-consol-s1",
            DisplayName = "S1 Profile",
            JobTemplate = E2ERouting.Key([]),
            Enabled = true
        });

        await using var agent = new FakeAgentClient("agent-consol-s1");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Act: trigger brain consolidation
        await page.ClickBrainConsolidationAsync("S1 Template");

        // Wait for FakeJobController to dispatch and agent to receive assignment.
        // JobAssigned.Task completes at the end of StartAssignedWorkItemAsync, after the
        // RegisterAgent InvokeAsync returns — meaning the server-side ActiveJobId is set before
        // this await returns. Using ReceivedJobIds.Count > 0 instead would be racy: ReceivedJobIds
        // is populated after the RegisterAgent call, so the server may not have confirmed the
        // ActiveJobId when ReportConsolidationCompleteAsync fires, causing the authorization
        // filter to reject the report and the test to time out waiting for Succeeded.
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(25));
        var jobId = assignment.JobId;

        // Agent reports success with v2 BrainOutcome (one promoted line) — spec 054 10.3
        await agent.ReportConsolidationCompleteAsync(new ConsolidationJobResult
        {
            JobId = jobId,
            Success = true,
            Summary = "Consolidated 3 files",
            BrainOutcome = new BrainConsolidationOutcome
            {
                Promoted = 1,
                Appended = 0,
                Retired = 0,
                MissedAgain = 0,
                Lines = [new BrainReviewLine { Action = "promoted", Scope = "general", File = "general/SKILL.md", Text = "use parameterized queries" }],
                InboxFilesRead = 1,
            }
        });

        // Wait for server-side state to reflect completion.
        // Poll the WorkItem status via IPipelineApiWorkItemClient — this is the authoritative
        // record after issue #3028 moved consolidation completions to the PipelineRun/WorkItem
        // store. IConsolidationService.GetRunHistoryAsync reads IConsolidationRunStore, which the
        // hub completion path (HandleConsolidationCompleteAsync → IRunLifecycleManager.CompleteRunAsync)
        // never writes to; polling it would always time out.
        var workItemGuid = Guid.Parse(jobId);
        await WaitUntilAsync(async () =>
        {
            var status = await Fixture.WorkItems.GetStatusAsync(workItemGuid);
            return status == WorkItemStatus.Succeeded || status == WorkItemStatus.Failed;
        });

        // Reload page to trigger LoadDataAsync, then assert DOM
        await page.NavigateAsync();
        await page.WaitForRunHistoryCountAsync(1);

        var rowText = await page.GetRunHistoryRowTextAsync(0);
        Assert.NotNull(rowText);
        Assert.Contains("Brain Consolidation", rowText);
        Assert.Contains("Succeeded", rowText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await Page.Locator(".monitoring-table tbody tr").Nth(0).Locator(".consolidation-status-succeeded").CountAsync());
        Assert.Contains("Consolidated 3 files", rowText);

        // Assert: template card shows new last-run status (Scenario 1 requirement).
        // The card renders a <span class="consolidation-status-succeeded"> under the Brain
        // Consolidation row for the template. Wait for the card to reflect the updated status
        // because NavigateAsync may return before LoadDataAsync populates _lastRuns.
        // Use Locator.WaitForAsync (which supports :has-text()) instead of WaitForFunctionAsync
        // (which runs in the browser's native JS engine and does not understand :has-text()).
        // The locator is scoped to the "S1 Template" card, eliminating the TOCTOU gap between
        // the wait and the assertion.
        var cardStatusLocator = Page.Locator(
            ".consolidation-card:has(.consolidation-card-title:has-text('S1 Template')) .consolidation-status-succeeded");
        await cardStatusLocator.WaitForAsync(new() { Timeout = 30_000 });
        Assert.True(await cardStatusLocator.CountAsync() > 0);

        // Assert: run page shows brain-review-lines to the admin (spec 054 10.3)
        await Page.GotoCockpitPageAsync($"{BaseUrl}/runs/{jobId}");
        var reviewLines = Page.Locator("[data-testid='brain-review-lines']");
        await reviewLines.WaitForAsync(new() { Timeout = 30_000 });
        Assert.True(await reviewLines.CountAsync() > 0, "brain-review-lines must be visible to the admin on the run page");
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

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-consol-s2",
            DisplayName = "S2 Profile",
            JobTemplate = E2ERouting.Key([]),
            Enabled = true
        });

        await using var agent = new FakeAgentClient("agent-consol-s2");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Act: trigger
        await page.ClickBrainConsolidationAsync("S2 Template");

        // Wait for FakeJobController to dispatch and agent to receive assignment.
        // JobAssigned.Task completes at the end of StartAssignedWorkItemAsync, after the
        // RegisterAgent InvokeAsync returns — meaning the server-side ActiveJobId is set before
        // this await returns. Using ReceivedJobIds.Count > 0 instead would be racy (see Scenario 1).
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(25));
        var jobId = assignment.JobId;

        // Agent reports failure
        await agent.ReportConsolidationCompleteAsync(new ConsolidationJobResult
        {
            JobId = jobId,
            Success = false,
            ErrorMessage = "Brain provider unavailable"
        });

        // Wait for server-side state to reflect failure.
        // Poll the WorkItem status via IPipelineApiWorkItemClient — this is the authoritative
        // record after issue #3028. IConsolidationService.GetRunHistoryAsync reads
        // IConsolidationRunStore, which the hub failure path (FailRunAsync) never writes to;
        // polling it would always time out.
        var workItemGuid = Guid.Parse(jobId);
        await WaitUntilAsync(async () =>
        {
            var status = await Fixture.WorkItems.GetStatusAsync(workItemGuid);
            return status == WorkItemStatus.Succeeded || status == WorkItemStatus.Failed;
        });

        // Reload and assert
        await page.NavigateAsync();
        await page.WaitForRunHistoryCountAsync(1);

        var rowText = await page.GetRunHistoryRowTextAsync(0);
        Assert.NotNull(rowText);
        Assert.Contains("Brain Consolidation", rowText);
        Assert.Contains("Failed", rowText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await Page.Locator(".monitoring-table tbody tr").Nth(0).Locator(".consolidation-status-failed").CountAsync());
        Assert.Contains("Brain provider unavailable", rowText);

        // Nothing should remain Pending or Running: verify via WorkItem status (not the
        // ConsolidationRuns store, which is no longer authoritative after issue #3028).
        var finalStatus = await Fixture.WorkItems.GetStatusAsync(workItemGuid);
        Assert.Equal(WorkItemStatus.Failed, finalStatus);
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
        Assert.True(await page.WaitForRefactoringModalAsync(), "Refactoring modal should be visible after clicking Refactoring Scan");
        await page.CancelRefactoringModalAsync();
        await WaitUntilAsync(async () => !await page.IsRefactoringModalVisibleAsync(), timeout: TimeSpan.FromSeconds(5));

        // CancelRefactoringModal is a synchronous Blazor click: it calls StateHasChanged and
        // sets _showRefactoringModal = false with no async dispatch path. There is no server-side
        // call that could create a WorkItem, so we assert immediately rather than waiting.
        // Run one explicit FakeJobController dispatch pass to confirm that even after a full poll
        // cycle no item was enqueued by a stray trigger.
        await Fixture.JobController.DispatchOnceAsync();

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

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-consol-s3b",
            DisplayName = "S3b Profile",
            JobTemplate = E2ERouting.Key([]),
            Enabled = true
        });

        await using var agent = new FakeAgentClient("agent-consol-s3b");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Act: open modal, confirm to start scan
        await page.ClickRefactoringScanAsync("S3b Template");
        Assert.True(await page.WaitForRefactoringModalAsync());
        await page.ConfirmRefactoringModalAsync();

        // Wait for FakeJobController to dispatch and agent to receive assignment.
        // JobAssigned.Task completes at the end of StartAssignedWorkItemAsync, after the
        // RegisterAgent InvokeAsync returns — meaning the server-side ActiveJobId is set before
        // this await returns. Using ReceivedJobIds.Count > 0 instead would be racy (see Scenario 1).
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(25));
        var jobId = assignment.JobId;

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

        // Wait for server-side completion.
        // Poll the WorkItem status via IPipelineApiWorkItemClient — this is the authoritative
        // record after issue #3028. IConsolidationService.GetRunHistoryAsync reads
        // IConsolidationRunStore, which the hub refactoring completion path never writes to;
        // polling it would always time out.
        var workItemGuid = Guid.Parse(jobId);
        await WaitUntilAsync(async () =>
        {
            var status = await Fixture.WorkItems.GetStatusAsync(workItemGuid);
            return status == WorkItemStatus.Succeeded || status == WorkItemStatus.Failed;
        });

        // Reload and assert
        await page.NavigateAsync();
        await page.WaitForRunHistoryCountAsync(1);

        var rowText = await page.GetRunHistoryRowTextAsync(0);
        Assert.NotNull(rowText);
        Assert.Contains("Succeeded", rowText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await Page.Locator(".monitoring-table tbody tr").Nth(0).Locator(".consolidation-status-succeeded").CountAsync());

        // Assert: the two created issues were processed by the hub.
        // The Consolidation page does not render CreatedIssueInfo identifiers in the history row
        // (only run.Summary is shown, which is null for this result). The observable side-effect
        // of CreatedIssues is the badge increment: 2 issues → BadgeCount == 2.
        // Resolve from the API host: HubConsolidationOperations increments the badge service
        // registered in the API host's DI container, not the Web host's.
        var badgeService = Fixture.ApiServices.GetRequiredService<ConsolidationBadgeService>();
        Assert.Equal(2, badgeService.BadgeCount);
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

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-consol-s5",
            DisplayName = "S5 Profile",
            JobTemplate = E2ERouting.Key([]),
            Enabled = true
        });

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

        // Wait explicitly for the status message to contain "cancel" rather than calling
        // WaitForStatusMessageAsync() (which resolves on any .consolidation-status-message,
        // including the stale "Triggered" message left from the initial trigger). If the Blazor
        // component does not clear the message on NavigateAsync, WaitForStatusMessageAsync would
        // return immediately on the old text and Assert.Contains("cancelled") would fail.
        // WaitForFunctionAsync polls the DOM until the text criterion is met, which is both
        // race-free and guarantees we are reading the cancel result rather than the trigger message.
        await Page.WaitForFunctionAsync(
            "() => { var el = document.querySelector('.consolidation-status-message'); return el && el.textContent.toLowerCase().includes('cancel'); }",
            null,
            new() { Timeout = 15_000 });

        // Assert: status message confirms cancellation (not an error)
        var msg = await page.GetStatusMessageAsync();
        Assert.NotNull(msg);
        Assert.Contains("cancelled", msg, StringComparison.OrdinalIgnoreCase);
        Assert.False(await page.IsStatusMessageErrorAsync());

        // Assert: no Pending work items remain (cancelled item no longer fetchable by FakeJobController)
        var pending = await Fixture.WorkItems.GetPendingAsync(10);
        Assert.Empty(pending);

        // Assert: the run-history row reflects Cancelled status after a page reload.
        // This verifies the acceptance criterion "The WorkItem ends Cancelled" at the UI level,
        // not just the transient toast. We reload the page to force LoadDataAsync to re-read the
        // PipelineRun store, then assert the row contains the terminal state.
        await page.NavigateAsync();
        await page.WaitForRunHistoryCountAsync(1);
        var cancelledRowText = await page.GetRunHistoryRowTextAsync(0);
        Assert.NotNull(cancelledRowText);
        Assert.Contains("Cancelled", cancelledRowText, StringComparison.OrdinalIgnoreCase);

        // Assert: connecting an agent now does NOT dispatch the cancelled item.
        // ConnectAsync uses InvokeAsync("RegisterAgent") which is request-response: when it
        // returns the agent IS in the registry with Idle status. The cancelled item is no longer
        // in GetPendingAsync (confirmed above), so FakeJobController will never claim it. We run
        // one explicit dispatch pass to confirm deterministically — no timing assumption required.
        await using var lateAgent = new FakeAgentClient("agent-consol-s5-late");
        await lateAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // One explicit dispatch pass covers the full poll cycle. If the cancelled item were
        // somehow dispatchable, DispatchOnceAsync would claim it and lateAgent would receive a job.
        await Fixture.JobController.DispatchOnceAsync();
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

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-consol-s6",
            DisplayName = "S6 Profile",
            JobTemplate = E2ERouting.Key([]),
            Enabled = true
        });

        var page = new ConsolidationPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Act: two rapid clicks — the second will hit the DB unique index and be deduplicated.
        // The first click may disable the button before the second fires. Use Force=true on
        // the second click to bypass Playwright's enabled-check and simulate the rapid
        // double-click that the dedup path is designed to handle.
        await page.ClickBrainConsolidationAsync("S6 Template");
        await page.ClickBrainConsolidationForcedAsync("S6 Template");

        // Wait deterministically: poll until at least one WorkItem exists in a non-terminal state
        // (either Pending or Dispatched/Running if FakeJobController claimed it). Using GetPendingAsync
        // alone is insufficient — the background poll can claim the item between WaitUntilAsync and the
        // assertion, producing an empty pending list even though exactly one item exists.
        await WaitUntilAsync(async () =>
        {
            var p = await Fixture.WorkItems.GetPendingAsync(10);
            if (p.Any()) return true;
            // Also check active (Dispatched/Running) in case the background poll already claimed it.
            var a = await Fixture.WorkItems.GetActiveAsync(olderThanSeconds: -3600);
            return a.Any();
        });

        // Assert: exactly one non-terminal WorkItem exists — dedup collapsed the second insert.
        // Query both Pending and Active (Dispatched/Running) so the count is correct regardless of
        // whether FakeJobController claimed the item between WaitUntilAsync and these calls.
        // The dedup invariant is "exactly one item in any non-terminal state", not "one Pending item",
        // so combining the two sets is the correct check.
        var pending = await Fixture.WorkItems.GetPendingAsync(10);
        var active = await Fixture.WorkItems.GetActiveAsync(olderThanSeconds: -3600);
        Assert.Equal(1, pending.Count + active.Count);
    }

}
