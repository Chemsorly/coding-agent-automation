using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the Cancel and Re-dispatch actions on the Run detail page (/runs/{id}).
/// Covers the full confirm-flow for both actions: choosing "No" leaves the run unchanged,
/// choosing "Yes" drives the pipeline through its server-side terminal path.
///
/// Scenarios from issue #3091:
/// 1. Cancel with confirm: live run is cancelled, WorkItem becomes Cancelled, label agent:cancelled is applied,
///    K8s job is deleted (when K8sJobName is set), and Cancel button disappears.
/// 2. Cancel double-click: two rapid "Yes, cancel" clicks produce exactly one status post.
/// 3. Re-dispatch success: terminal Implementation run is re-dispatched; a new WorkItem is created,
///    the agent receives a new assignment, and the page shows "Re-dispatched successfully".
/// 4. Re-dispatch is hidden: not shown for live runs, Review runs, Decomposition runs, or runs
///    with missing provider IDs.
/// 5. Re-dispatch error path: re-dispatch while an active WorkItem already exists for the same
///    issue shows "Re-dispatch failed:" and does not create a second WorkItem.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RunPageCancelAndRedispatchTests : E2ETestBase
{
    public RunPageCancelAndRedispatchTests(E2EFixture fixture) : base(fixture) { }

    // ── Shared arrange ───────────────────────────────────────────────────

    /// <summary>
    /// Seeds a template, profile, and issue, then dispatches and activates the run at
    /// <see cref="PipelineStep.GeneratingCode"/>. Returns the run's ID.
    /// </summary>
    private async Task<string> ArrangeActiveRunAsync(
        FakeAgentClient agent, string issueId, string issueTitle = "Run page test issue")
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-runpage",
            Name = "Run Page Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-runpage",
            DisplayName = "Run Page Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = issueTitle,
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        // Dispatch through the coding page
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Run Page Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueId);
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        // Wait for the run to be live at GeneratingCode
        await WaitUntilAsync(() =>
            Fixture.RunService.GetActiveRuns()
                .Any(r => r.IssueIdentifier == issueId && r.CurrentStep == PipelineStep.GeneratingCode));

        return Fixture.RunService.GetActiveRuns()
            .First(r => r.IssueIdentifier == issueId).RunId;
    }

    /// <summary>
    /// Completes an issue run at <see cref="PipelineStep.Failed"/> so the re-dispatch button
    /// appears. Returns the run's ID.
    /// </summary>
    private async Task<string> ArrangeFailedRunAsync(FakeAgentClient agent, string issueId,
        string issueTitle = "Re-dispatch test issue")
    {
        // TODO [WARNING]: Both ArrangeActiveRunAsync and ArrangeFailedRunAsync call SaveTemplateAsync/
        // SaveAgentProfileAsync with the same IDs ("template-runpage", "profile-runpage"). When both
        // helpers are called in the same test body (e.g. Scenario 5), the same template ID is written
        // twice. If SaveTemplateAsync is not idempotent on duplicate IDs, the second call could reset
        // or corrupt state. Verify SaveTemplateAsync is an upsert, or extract common seeding into a
        // shared helper that is called only once.
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-runpage",
            Name = "Run Page Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-runpage",
            DisplayName = "Run Page Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = issueTitle,
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Run Page Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueId);
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Complete as Failed with provider IDs populated (required for CanRedispatch)
        await agent.AcceptAndCompleteJobWithPayloadAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            FinalLabel = AgentLabels.Error,
            FailureReason = "Build failed",
            CompletedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            IsDraftPr = false,
            FilesChangedCount = 0,
            LinesAdded = 0,
            LinesRemoved = 0,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>()
        });

        // Wait for the run to appear in history
        var history = await WaitForHistoryAsync(
            r => r.IssueIdentifier == issueId && r.FinalStep == PipelineStep.Failed,
            TimeSpan.FromSeconds(15));
        return history.RunId;
    }

    // ════════════════════════════════════════════════════════════════════
    // Scenario 1: Cancel with confirm
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scenario 1: a live run can be cancelled from the Run page via the confirm-flow.
    /// Asserts:
    /// - choosing "No" leaves the run active and the Cancel button still visible;
    /// - choosing "Yes, cancel" transitions the WorkItem to Cancelled;
    /// - the issue gains the agent:cancelled label;
    /// - a K8s job delete is requested when a K8sJobName is present;
    /// - the Cancel button disappears and the run is shown as Cancelled.
    /// </summary>
    [Fact]
    public async Task Cancel_LiveRun_FromRunPage_ConfirmAndDismissFlow()
    {
        await using var agent = new FakeAgentClient("runpage-cancel-agent-1", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var runId = await ArrangeActiveRunAsync(agent, "3091-cancel-1", "Cancel from run page test");

        // Resolve the WorkItem ID before navigating (we need it for assertions)
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var workItem = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IssueIdentifier == "3091-cancel-1");
        Assert.NotNull(workItem);
        var workItemId = workItem.Id;

        // Set a K8sJobName so the cleanup path has a job to delete
        await SetK8sJobNameAsync(workItemId, $"caa-{workItemId:N}"[..32]);
        var expectedJobName = $"caa-{workItemId:N}"[..32];

        // Act 1: navigate to the run page and dismiss cancel with "No"
        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);

        // The cancel button must be visible while the run is live
        Assert.True(await runPage.IsCancelButtonVisibleAsync(), "Cancel button should be visible for a live run");

        // Click Cancel, then dismiss with No — nothing should change
        await runPage.CancelAsync(confirm: false);

        // The cancel button should still be visible (run still live) after dismissal
        // TODO [WARNING]: Page.WaitForTimeoutAsync(500) is a fixed sleep used to let Blazor re-render
        // the cancel button into its correct post-dismiss state before the assertion fires. On a slow
        // CI runner, 500 ms may be insufficient. Prefer polling with WaitUntilAsync on the specific
        // condition (e.g. CancelConfirmSection.IsHiddenAsync) rather than a fixed delay.
        await Page.WaitForTimeoutAsync(500); // let Blazor render
        Assert.True(await runPage.IsCancelButtonVisibleAsync(), "Cancel button should remain visible after dismissing confirm");
        Assert.True(Fixture.RunService.GetActiveRuns().Any(r => r.IssueIdentifier == "3091-cancel-1"),
            "Run should still be active after dismissing cancel");

        // Act 2: re-navigate and confirm cancel
        await runPage.NavigateAsync(runId);
        await runPage.CancelAsync(confirm: true);

        // Assert: WorkItem transitions to Cancelled
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Cancelled, TimeSpan.FromSeconds(15));

        // Assert: issue gets agent:cancelled label
        await WaitUntilAsync(() =>
            Fixture.IssueProvider.LabelChanges.Any(lc =>
                lc.Identifier == "3091-cancel-1" && lc.Label == AgentLabels.Cancelled && lc.Added),
            TimeSpan.FromSeconds(15));

        // Assert: K8s job delete was requested for the named job
        await WaitUntilAsync(() => Fixture.K8sClient.DeletedJobs.Contains(expectedJobName),
            TimeSpan.FromSeconds(10));

        // Assert: run is no longer active
        await WaitUntilAsync(() => !Fixture.RunService.GetActiveRuns().Any(r => r.IssueIdentifier == "3091-cancel-1"));

        // Assert: the page reflects the Cancelled state and the cancel button is gone
        await runPage.NavigateAsync(runId);
        Assert.False(await runPage.IsCancelButtonVisibleAsync(), "Cancel button should be gone after cancellation");
        var pageText = await runPage.GetPageTextAsync();
        Assert.Contains("Cancelled", pageText);
    }

    // ════════════════════════════════════════════════════════════════════
    // Scenario 2: Cancel double-click guard
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scenario 2: clicking "Yes, cancel" twice rapidly produces exactly one status post and
    /// no error text. The double-click guard (<c>if (_cancelling) return;</c>) blocks the second call.
    ///
    /// The guard is verified via a concrete side-effect count: with a K8sJobName set,
    /// <see cref="KubernetesJobCleanup"/> calls <c>DeleteJobAsync</c> exactly once per successful
    /// cancel. If the guard is absent and two <c>TransitionAsync</c> calls both reach the server,
    /// the job name would appear twice in <see cref="FakeKubernetesJobClient.DeletedJobs"/> — which
    /// this assertion would catch. Asserting only on final WorkItemStatus is insufficient because
    /// idempotent status transitions would leave it Cancelled regardless of how many fired.
    /// </summary>
    [Fact]
    public async Task Cancel_DoubleClick_ProducesOnlyOneStatusPost()
    {
        await using var agent = new FakeAgentClient("runpage-cancel-agent-2", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var runId = await ArrangeActiveRunAsync(agent, "3091-cancel-2", "Double-click cancel test");

        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var workItem = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IssueIdentifier == "3091-cancel-2");
        Assert.NotNull(workItem);
        var workItemId = workItem.Id;

        // Set a K8sJobName so KubernetesJobCleanup fires DeleteJobAsync on cancel.
        // This gives us a concrete side-effect count to assert on: if the double-click guard is
        // absent and two cancels reach the server, the same job name appears twice in DeletedJobs.
        var expectedJobName = $"caa-{workItemId:N}"[..32];
        await SetK8sJobNameAsync(workItemId, expectedJobName);

        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);

        // Open the confirm section
        await runPage.CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        await runPage.CancelButton.ClickAsync();
        await runPage.CancelConfirmSection.WaitForAsync(new() { Timeout = 10_000 });

        // Double-click "Yes, cancel" as fast as Playwright allows
        await runPage.ConfirmCancelButton.ClickAsync();
        try
        {
            // Second click — may succeed or fail depending on render cycle
            await runPage.ConfirmCancelButton.ClickAsync(new() { Timeout = 2_000 });
        }
        catch
        {
            // Button was removed or disabled — expected
        }

        // Assert: WorkItem reaches Cancelled exactly once (no duplicate transitions)
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Cancelled, TimeSpan.FromSeconds(15));

        // Assert: K8s delete was requested exactly once — proves only one cancel reached the server.
        // A second concurrent cancel would add the same job name to DeletedJobs a second time,
        // making Count == 2 and failing the assertion below.
        // TODO [WARNING]: This count assertion is evaluated at a single point in time immediately
        // after WaitUntilAsync confirms at least one deletion. If a second cancel is still in flight
        // when Count() is evaluated (race window within the ~100 ms WaitUntilAsync poll interval),
        // the test may pass prematurely and the second deletion would be recorded after the assertion.
        // To close the window, add a brief stabilization wait (e.g. Task.Delay(200)) before reading
        // the count, or poll until the count is stable across two consecutive reads.
        await WaitUntilAsync(() => Fixture.K8sClient.DeletedJobs.Contains(expectedJobName),
            TimeSpan.FromSeconds(10));
        var deleteCount = Fixture.K8sClient.DeletedJobs.Count(j => j == expectedJobName);
        Assert.Equal(1, deleteCount);

        // Assert: no error text on page
        var pageText = await runPage.GetPageTextAsync();
        Assert.DoesNotContain("Cancel failed", pageText);

        // Assert: WorkItem remains Cancelled (not Failed or any other state)
        await using var dbCheck = Fixture.DbContextFactory.CreateDbContext();
        var finalItem = await dbCheck.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workItemId);
        Assert.NotNull(finalItem);
        Assert.Equal(WorkItemStatus.Cancelled, finalItem.Status);
    }

    // ════════════════════════════════════════════════════════════════════
    // Scenario 3: Re-dispatch a failed implementation run
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scenario 3: clicking Re-dispatch on a terminal Implementation run creates a new WorkItem,
    /// the page shows "Re-dispatched successfully", and the agent receives a new assignment.
    /// </summary>
    [Fact]
    public async Task Redispatch_TerminalImplementationRun_SuccessMessage_AgentReceivesNewAssignment()
    {
        await using var agent = new FakeAgentClient("runpage-redispatch-agent-1", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Arrange: complete the first run as Failed
        var runId = await ArrangeFailedRunAsync(agent, "3091-redispatch-1", "Re-dispatch success test");

        // Reset the TCS so the agent can receive the second assignment.
        // TODO [WARNING]: ArrangeFailedRunAsync calls AcceptAndCompleteJobWithPayloadAsync, which
        // consumes the JobAssigned TCS. If ResetJobAssigned() creates a new TCS unconditionally this
        // is safe; if it no-ops when the TCS is already in a completed state, the
        // agent.JobAssigned.Task assertion at the bottom of this test may use a stale completed task
        // and return the first (arrange-time) assignment rather than the new one. Verify
        // FakeAgentClient.ResetJobAssigned() always creates a fresh, uncompleted TCS.
        agent.ResetJobAssigned();

        // Act: navigate to the run page and confirm re-dispatch
        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);

        // Re-dispatch card should be visible
        Assert.True(await runPage.IsRedispatchCardVisibleAsync(), "Re-dispatch card should be visible for a terminal Implementation run");
        Assert.True(await runPage.IsRedispatchButtonVisibleAsync(), "Re-dispatch button should be visible");

        await runPage.RedispatchAsync(confirm: true);

        // Assert: success message is shown on the page
        await Page.WaitForSelectorAsync("[data-testid='redispatch-card']:has-text('Re-dispatched successfully')",
            new() { Timeout = 15_000 });
        var pageText = await runPage.GetPageTextAsync();
        Assert.Contains("Re-dispatched successfully", pageText);

        // Assert: a new WorkItem was created (two items for this issue now exist)
        // TODO [WARNING]: The wait condition uses count >= 2 rather than count == 2. A double-dispatch
        // bug that creates three WorkItems would pass this assertion. Tighten to == 2 once the
        // re-dispatch mechanism is confirmed to not spuriously create extra rows.
        await WaitUntilAsync(async () =>
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var count = await db.WorkItems.AsNoTracking()
                .CountAsync(w => w.IssueIdentifier == "3091-redispatch-1");
            return count >= 2;
        }, TimeSpan.FromSeconds(15));

        // Assert: the agent receives the new assignment
        var newAssignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("3091-redispatch-1", newAssignment.IssueIdentifier);
        Assert.NotEqual(runId, newAssignment.JobId);
    }

    // ════════════════════════════════════════════════════════════════════
    // Scenario 4: Re-dispatch is hidden where it should be
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scenario 4a: Re-dispatch is NOT shown for a live (active) run.
    /// </summary>
    [Fact]
    public async Task Redispatch_Hidden_ForLiveRun()
    {
        await using var agent = new FakeAgentClient("runpage-redispatch-agent-4a", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var runId = await ArrangeActiveRunAsync(agent, "3091-hidden-live", "Hidden re-dispatch test — live");

        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId);

        // Re-dispatch card must NOT be present for an active run
        // TODO [WARNING]: CountAsync is evaluated immediately after NavigateAsync without waiting for
        // the page to finish rendering. For a Blazor Server app, the component's OnInitializedAsync
        // lifecycle may not have completed by the time CountAsync is evaluated. A 0 count could
        // reflect a not-yet-rendered page (false negative) rather than the component correctly hiding
        // the card. Add WaitForLoadStateAsync or a brief stabilization wait before asserting absence.
        var cardCount = await runPage.RedispatchCard.CountAsync();
        Assert.Equal(0, cardCount);
    }

    /// <summary>
    /// Scenario 4b: Re-dispatch is NOT shown for a Review run that has reached a terminal state.
    /// </summary>
    [Fact]
    public async Task Redispatch_Hidden_ForTerminalReviewRun()
    {
        // Insert a terminal Review run directly into history — the run type is what hides re-dispatch.
        // The run page reads from the history service via GetRunAsync, so we use AddRunSummaryAsync
        // to seed it without going through the full dispatch+complete cycle.
        // TODO [WARNING]: This test seeds a PipelineRunSummary directly into the history service fake
        // (bypassing the dispatch cycle) and then asserts the re-dispatch card is absent. If RunDetailPage
        // reads run data through a path that does not include the fake history service (e.g. reads from the
        // DB for recently-created runs), the page may 404 or show an empty run — causing the cardCount==0
        // assertion to pass vacuously (card absent because the run was not found, not because CanRedispatch
        // returned false). The correctness of this test depends on the fake history service being the
        // canonical source for the Run page. Verify this assumption, or use the DB-seeding path instead.
        var runId = Guid.NewGuid();
        var reviewRun = new PipelineRunSummary
        {
            RunId = runId.ToString(),
            IssueIdentifier = "3091-hidden-review",
            IssueTitle = "Hidden re-dispatch test — review run",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            RunType = PipelineRunType.Review,
            // Provider IDs present — only RunType should prevent the button
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e"
        };
        await Fixture.HistoryService.AddRunSummaryAsync(reviewRun);

        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId.ToString());

        // Re-dispatch card must NOT be present for a Review run
        // TODO [WARNING]: Same stabilization concern as Scenario 4a: CountAsync is evaluated immediately
        // after NavigateAsync. This test also lacks a Decomposition run type coverage — the issue
        // specifies both Review and Decomposition runs should hide the card. Consider adding a
        // separate test for PipelineRunType.Decomposition.
        var cardCount = await runPage.RedispatchCard.CountAsync();
        Assert.Equal(0, cardCount);
    }

    /// <summary>
    /// Scenario 4c: Re-dispatch is NOT shown for a terminal Implementation run that lacks provider IDs.
    /// </summary>
    [Fact]
    public async Task Redispatch_Hidden_ForRunWithMissingProviderIds()
    {
        // Insert a terminal Implementation run without provider IDs
        // TODO [WARNING]: Same concern as 4b — if RunDetailPage reads from the DB rather than the
        // history service fake, the seeded PipelineRunSummary is never read and the cardCount==0
        // assertion passes vacuously. Verify the fake history service is the canonical read path.
        var runId = Guid.NewGuid();
        var runNoProviders = new PipelineRunSummary
        {
            RunId = runId.ToString(),
            IssueIdentifier = "3091-hidden-noproviders",
            IssueTitle = "Hidden re-dispatch — missing providers",
            FinalStep = PipelineStep.Failed,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            RunType = PipelineRunType.Implementation,
            // Deliberately null — CanRedispatch should return false
            IssueProviderConfigId = null,
            RepoProviderConfigId = null
        };
        await Fixture.HistoryService.AddRunSummaryAsync(runNoProviders);

        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(runId.ToString());

        // Re-dispatch card must NOT be present
        // TODO [WARNING]: Same stabilization concern as Scenario 4a: add a page stabilization wait
        // before asserting absence.
        var cardCount = await runPage.RedispatchCard.CountAsync();
        Assert.Equal(0, cardCount);
    }

    // ════════════════════════════════════════════════════════════════════
    // Scenario 5: Re-dispatch error path
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scenario 5: attempting to re-dispatch when the concurrency limit for the selector is already
    /// reached results in a "Re-dispatch failed" error message and no additional WorkItem is created.
    ///
    /// The re-dispatch builds a request with <c>AgentSelector = ""</c>, which resolves to the
    /// default template with <c>maxConcurrent: 5</c>. We insert 5 Running WorkItems with the same
    /// selector to saturate the gate, then attempt re-dispatch — the API returns 409 Conflict.
    /// </summary>
    [Fact]
    public async Task Redispatch_ConcurrencyLimitReached_ShowsErrorMessage_NoAdditionalWorkItemCreated()
    {
        await using var agent = new FakeAgentClient("runpage-redispatch-agent-5", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Arrange: complete first run as Failed
        var failedRunId = await ArrangeFailedRunAsync(agent, "3091-error-2", "Re-dispatch concurrency error test");

        // Count WorkItems before filling the concurrency limit
        await using var dbBefore = Fixture.DbContextFactory.CreateDbContext();
        var workItemsBefore = await dbBefore.WorkItems.AsNoTracking()
            .Where(w => w.IssueIdentifier == "3091-error-2")
            .CountAsync();

        // Saturate the concurrency limit: insert 5 Running WorkItems with AgentSelector="" (maxConcurrent: 5)
        // These are for different issues so they don't conflict with the failed run's row,
        // but they all use AgentSelector="" which is what RunPage.RedispatchAsync sends.
        // TODO [WARNING]: This assumes the re-dispatch request will use AgentSelector="" and that the
        // concurrency gate checks on that selector with maxConcurrent=5. If the re-dispatch request
        // resolves the selector to something non-empty at runtime, the 5 filler rows would not count
        // toward the same limit and no 409 would fire — the test would then hang waiting for the
        // 'Re-dispatch failed' selector to appear. There is no guard that verifies the concurrency
        // limit was actually saturated before attempting re-dispatch. Consider asserting the filler
        // rows are visible to the gate (e.g. via a WorkItems count endpoint) before proceeding.
        for (var i = 0; i < 5; i++)
        {
            await using var dbInsert = Fixture.DbContextFactory.CreateDbContext();
            dbInsert.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"filler-issue-{i}",
                IssueProviderConfigId = "issue-e2e",
                Status = WorkItemStatus.Running,
                Payload = "{}",
                AgentSelector = "",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600,
                ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId)
            });
            await dbInsert.SaveChangesAsync();
        }

        // Act: navigate to the failed run and attempt re-dispatch
        var runPage = new RunDetailPage(Page, BaseUrl);
        await runPage.NavigateAsync(failedRunId);

        Assert.True(await runPage.IsRedispatchCardVisibleAsync(), "Re-dispatch card should be visible for a terminal run");
        await runPage.RedispatchAsync(confirm: true);

        // Assert: "Re-dispatch failed" message appears (the 409 from ApplyGates triggers the error callout)
        await Page.WaitForSelectorAsync("[data-testid='redispatch-card']:has-text('Re-dispatch failed')",
            new() { Timeout = 15_000 });
        var pageText = await runPage.GetPageTextAsync();
        Assert.Contains("Re-dispatch failed", pageText);

        // Assert: no additional WorkItem for the original issue was created
        await using var dbFinal = Fixture.DbContextFactory.CreateDbContext();
        var workItemsAfter = await dbFinal.WorkItems.AsNoTracking()
            .Where(w => w.IssueIdentifier == "3091-error-2")
            .CountAsync();
        Assert.Equal(workItemsBefore, workItemsAfter);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the K8sJobName on a WorkItem so the <see cref="KubernetesJobCleanup"/> path has a
    /// job to delete when the run is cancelled. Without this, the cleanup method returns early.
    /// </summary>
    private async Task SetK8sJobNameAsync(Guid workItemId, string jobName)
    {
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId);
        if (entity is not null)
        {
            entity.K8sJobName = jobName;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Waits for a WorkItem to reach the expected status, or throws after the timeout.
    /// Delegates to the inherited wait helper from <see cref="E2ETestBase"/>.
    /// </summary>
    private async Task WaitForWorkItemStatusAsync(Guid workItemId, WorkItemStatus expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
            if (item?.Status == expected) return;
            await Task.Delay(100);
        }

        await using var finalDb = Fixture.DbContextFactory.CreateDbContext();
        var final = await finalDb.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        throw new TimeoutException(
            $"WorkItem {workItemId} did not reach {expected} within {timeout.TotalSeconds}s. " +
            $"Current: {final?.Status.ToString() ?? "NOT FOUND"}");
    }
}
