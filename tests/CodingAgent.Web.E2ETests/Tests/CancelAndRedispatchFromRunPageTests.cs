using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using k8s.Models;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the "Cancel Pipeline" and "Re-dispatch" actions on the /runs/{id} page.
///
/// <para>
/// Scenarios covered:
/// <list type="number">
///   <item>Cancel with confirm — live run is cancelled; WorkItem ends Cancelled, issue label
///     becomes agent:cancelled, K8s job is deleted via orphan cleanup, page shows Cancelled
///     badge with no Cancel button.</item>
///   <item>Cancel double-click — clicking "Yes, cancel" twice fast produces one status post
///     and no error callout.</item>
///   <item>Re-dispatch a failed run — confirms re-dispatch, a new WorkItem is created, the
///     "Re-dispatched successfully" message appears, and the fake agent receives the new job.</item>
///   <item>Re-dispatch visibility — hidden for live runs, Review runs, Decomposition runs, and
///     runs whose provider IDs are missing.</item>
///   <item>Re-dispatch error path — re-dispatch while an active WorkItem already exists shows
///     "Re-dispatch failed: …", no second WorkItem is created.</item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "RunPageActions")]
[Collection(E2ECollection.Name)]
public sealed class CancelAndRedispatchFromRunPageTests : E2ETestBase
{
    public CancelAndRedispatchFromRunPageTests(E2EFixture fixture) : base(fixture) { }

    // ── Shared seed helpers ───────────────────────────────────────────────

    private async Task SeedTemplateAndProfileAsync(string templateId = "template-1", string templateName = "Run Page Test Template")
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = templateId,
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-runpage-e2e",
            DisplayName = "Run Page E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);
    }

    private void AddIssue(string identifier, string title = "Run page test issue")
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = identifier,
            Title = title,
            Description = "Test",
            Labels = new[] { "enhancement" }
        });
    }

    /// <summary>
    /// Seeds template/issue/profile, dispatches via the UI, has the agent accept the job and
    /// report a step. Returns the run id once the run is active at the expected step.
    /// </summary>
    private async Task<(string RunId, FakeAgentClient Agent, string JobId)> SeedDispatchAndActivateAsync(
        FakeAgentClient agent,
        string issueId,
        PipelineStep step = PipelineStep.GeneratingCode)
    {
        await SeedTemplateAndProfileAsync();
        AddIssue(issueId);

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Run Page Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueId);
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, step);

        var runService = Fixture.RunService;
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == issueId && r.CurrentStep == step));
        var runId = runService.GetActiveRuns().First(r => r.IssueIdentifier == issueId).RunId;

        return (runId, agent, assignment.JobId);
    }

    /// <summary>
    /// Polls the database until the WorkItem for the given id reaches the expected status.
    /// E2ETestBase does not inherit from HeadlessE2ETestBase, so this is an inline version.
    /// </summary>
    // TODO [WARNING]: This duplicates the polling pattern already present in HeadlessE2ETestBase.WaitForWorkItemStatusAsync.
    // If E2ETestBase ever inherits from HeadlessE2ETestBase (or if a shared helper is extracted), remove this
    // inline version and delegate to the shared helper to avoid divergence and O(n) DbContext allocations under slow CI.
    private async Task WaitForWorkItemStatusAsync(Guid workItemId, WorkItemStatus expectedStatus, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(25));
        while (DateTime.UtcNow < deadline)
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
            if (item?.Status == expectedStatus) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        await using var finalDb = Fixture.DbContextFactory.CreateDbContext();
        var finalItem = await finalDb.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        throw new TimeoutException(
            $"WorkItem {workItemId} did not reach {expectedStatus} within " +
            $"{(timeout ?? TimeSpan.FromSeconds(25)).TotalSeconds}s. " +
            $"Current status: {finalItem?.Status.ToString() ?? "NOT FOUND"}");
    }

    // ═════════════════════════════════════════════════════════════════════
    // Scenario 1 — Cancel with confirm
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scenario 1: A live run is opened on /runs/{id}. Clicking "Cancel Pipeline" opens a
    /// confirm prompt; choosing "No" leaves the run unchanged. Choosing "Yes, cancel" cancels
    /// the WorkItem, applies agent:cancelled to the issue, deletes the K8s Job (via orphan
    /// cleanup), and shows the Cancelled badge with no Cancel button.
    /// </summary>
    [Fact]
    public async Task Cancel_WithConfirm_CancelsWorkItemAndAppliesLabelAndDeletesK8sJob()
    {
        // Arrange: seed and activate a live run
        await using var agent = new FakeAgentClient("cancel-runpage-1", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var (runId, _, _) = await SeedDispatchAndActivateAsync(agent, "rp-cancel-1");

        // Resolve the WorkItem id — RunId == WorkItem.Id by contract
        var workItemId = Guid.Parse(runId);

        // Register a fake K8s Job for this WorkItem so the orphan-cleanup path has something to
        // delete. The normal E2E harness does not go through the K8s dispatch endpoint (which is
        // what creates a real Job in the cluster); we seed the job manually here, mirroring the
        // labelling that JobNameFactory.ForBrain and DispatchLifecycleService produce.
        var jobName = JobNameFactory.ForBrain(workItemId);
        var fakeJob = new V1Job
        {
            Metadata = new k8s.Models.V1ObjectMeta
            {
                Name = jobName,
                NamespaceProperty = "default",
                CreationTimestamp = DateTime.UtcNow,
                Labels = new Dictionary<string, string>
                {
                    ["caa/work-item-id"] = workItemId.ToString(),
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator"
                }
            },
            Status = new V1JobStatus()
        };
        Fixture.K8sClient.CreatedJobs[jobName] = fakeJob;

        // Act: open the Run page and verify the Cancel Pipeline button is visible
        var runDetail = new RunDetailPage(Page, BaseUrl);
        await runDetail.NavigateAsync(runId);
        Assert.True(await runDetail.IsCancelButtonVisibleAsync(),
            "Cancel Pipeline button should be visible for a live run");

        // Dismissing the confirm ("No") should leave everything unchanged
        await runDetail.CancelAsync(confirm: false);
        // Wait for the cancel button to become visible again — this confirms that Blazor has
        // processed the dismiss (@onclick="() => _showCancelConfirm = false") and re-rendered
        // the button branch. The previous fixed 500 ms sleep was insufficient under CI load.
        await runDetail.CancelButton.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible, Timeout = 5_000 });

        Assert.True(await runDetail.IsCancelButtonVisibleAsync(),
            "Cancel Pipeline button should still be visible after dismissing the confirm");
        Assert.False(await runDetail.IsShownAsCancelledAsync(),
            "Run should not be cancelled after dismissing confirm");

        // Act: confirm the cancellation
        await runDetail.CancelAsync(confirm: true);

        // Assert 1: WorkItem transitions to Cancelled in the database
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Cancelled, TimeSpan.FromSeconds(20));

        // Assert 2: the issue's final label is agent:cancelled
        await WaitUntilAsync(
            () => Fixture.IssueProvider.LabelChanges.Any(lc =>
                lc.Identifier == "rp-cancel-1" && lc.Label == AgentLabels.Cancelled && lc.Added),
            timeout: TimeSpan.FromSeconds(15));

        // Assert 3: K8s job delete was requested — orphan cleanup deletes the job once the
        // WorkItem is terminal (Cancelled). Age the job past the 600s retention window so
        // CleanupOrphansAsync fires; then run the sweep.
        fakeJob.Status!.CompletionTime = DateTime.UtcNow.AddSeconds(-700);
        await Fixture.RealReconciliationLoop.CleanupOrphansAsync(CancellationToken.None);

        await WaitUntilAsync(
            () => Fixture.K8sClient.DeletedJobs.Contains(jobName),
            timeout: TimeSpan.FromSeconds(10));

        // Assert 4: page shows Cancelled badge and no Cancel button
        // The page updates via SignalR OnCompleted; wait for the badge to appear
        await runDetail.WaitForCancelledStateAsync(timeoutMs: 20_000);
        Assert.True(await runDetail.IsShownAsCancelledAsync(),
            "Run page should show Cancelled badge after cancellation");

        await runDetail.WaitForCancelButtonAbsentAsync(timeoutMs: 10_000);
        Assert.False(await runDetail.IsCancelButtonVisibleAsync(),
            "Cancel Pipeline button should be absent after the run is Cancelled");
    }

    // ═════════════════════════════════════════════════════════════════════
    // Scenario 2 — Cancel double-click
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scenario 2: Clicking "Yes, cancel" twice in quick succession produces exactly one
    /// status transition (the guard prevents a second PostStatusAsync) and no error callout.
    /// </summary>
    [Fact]
    public async Task Cancel_DoubleClick_ProducesOneStatusPostAndNoError()
    {
        // Arrange: seed and activate a live run
        await using var agent = new FakeAgentClient("cancel-runpage-2", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var (runId, _, _) = await SeedDispatchAndActivateAsync(agent, "rp-cancel-2");
        var workItemId = Guid.Parse(runId);

        // Act: open the Run page and click Cancel Pipeline
        var runDetail = new RunDetailPage(Page, BaseUrl);
        await runDetail.NavigateAsync(runId);

        // Click Cancel Pipeline to show the confirm section
        await runDetail.CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        await runDetail.CancelButton.ClickAsync();
        await Page.WaitForSelectorAsync("[data-testid='cancel-pipeline-confirm-section']",
            new() { Timeout = 10_000 });

        // Double-click "Yes, cancel" rapidly via JavaScript so both clicks fire within the same
        // browser task, before any Blazor re-render or button disable can remove the element.
        // This reliably exercises the _cancelling guard (double-submit guard) in RunPage.razor,
        // unlike two sequential ClickAsync calls where the element may already be gone before
        // the second call reaches the browser.
        var confirmBtn = Page.Locator("[data-testid='confirm-cancel-pipeline-btn']");
        await confirmBtn.WaitForAsync(new() { Timeout = 10_000 });
        await Page.EvaluateAsync(@"selector => {
            const el = document.querySelector(selector);
            if (el) { el.click(); el.click(); }
        }", "[data-testid='confirm-cancel-pipeline-btn']");

        // Assert: WorkItem transitions to Cancelled (one transition)
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Cancelled, TimeSpan.FromSeconds(20));

        // Assert: no cancel error callout
        var cancelError = await runDetail.GetCancelErrorTextAsync();
        Assert.Null(cancelError);

        // Assert: only one Cancelled label change for this issue.
        // Use Assert.Equal(1, ...) rather than <= 1 so that a silent failure (0 adds) is caught.
        var cancelledAdds = Fixture.IssueProvider.LabelChanges
            .Count(lc => lc.Identifier == "rp-cancel-2" && lc.Label == AgentLabels.Cancelled && lc.Added);
        Assert.Equal(1, cancelledAdds);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Scenario 3 — Re-dispatch a failed implementation run
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scenario 3: A failed terminal Implementation run (with provider IDs) shows the
    /// Re-dispatch card. Clicking Re-dispatch and confirming creates a new WorkItem for the
    /// same issue, shows "Re-dispatched successfully", and the fake agent receives the new job.
    /// </summary>
    [Fact]
    public async Task Redispatch_FailedImplRun_CreatesNewWorkItemAndAgentReceivesAssignment()
    {
        // Arrange: seed template, issue, and agent profile, then create a terminal run in history
        await SeedTemplateAndProfileAsync("template-rdp", "Redispatch Test Template");
        AddIssue("rp-redispatch-3", "Re-dispatch test issue");

        // Create a terminal (Failed) run summary with provider IDs so CanRedispatch returns true
        // TODO [WARNING]: PipelineRunSummary does not set a TemplateId referencing "template-rdp".
        // If CanRedispatch requires the run's template to resolve to an enabled template, the redispatch
        // card may be hidden and WaitForRedispatchCardVisibleAsync will time out with a non-obvious error.
        // Set TemplateId = "template-rdp" here once PipelineRunSummary exposes that property.
        var terminalRunId = Guid.NewGuid();
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = terminalRunId.ToString(),
            IssueIdentifier = "rp-redispatch-3",
            IssueTitle = "Re-dispatch test issue",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            FailureReason = "Build failed"
        });

        // Connect a fake agent to receive the new dispatch
        await using var agent = new FakeAgentClient("redispatch-agent-3", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: open the Run page for the terminal run
        var runDetail = new RunDetailPage(Page, BaseUrl);
        await runDetail.NavigateAsync(terminalRunId.ToString());

        // Verify the redispatch card is visible for this terminal Implementation run
        await runDetail.WaitForRedispatchCardVisibleAsync(timeoutMs: 15_000);
        Assert.True(await runDetail.IsRedispatchCardVisibleAsync(),
            "Redispatch card should be visible for a terminal Implementation run with provider IDs");

        // Click Re-dispatch without confirming first — confirm section should appear
        await Page.ClickAsync("[data-testid='redispatch-btn']");
        await Page.WaitForSelectorAsync("[data-testid='redispatch-confirm-btn']",
            new() { Timeout = 10_000 });
        Assert.True(await runDetail.IsRedispatchConfirmVisibleAsync(),
            "Confirm section should appear after clicking Re-dispatch");

        // Now confirm the re-dispatch
        await Page.ClickAsync("[data-testid='redispatch-confirm-btn']");

        // Assert 1: "Re-dispatched successfully" message appears
        await runDetail.WaitForRedispatchSuccessAsync(timeoutMs: 15_000);
        Assert.True(await runDetail.IsRedispatchSuccessVisibleAsync(),
            "Re-dispatched successfully message should appear after confirming re-dispatch");

        // Assert 2: a new WorkItem was created for the same issue
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("rp-redispatch-3", assignment.IssueIdentifier);

        // Assert 3: the new WorkItem has a different run id than the original
        // TODO [WARNING]: This does not assert that the new WorkItem was dispatched with the correct
        // template or provider IDs. If RedispatchAsync dispatches with the wrong template or provider,
        // the test passes while the re-dispatched run would be misconfigured. Add an assertion on
        // assignment.TemplateId (or equivalent) once the assignment message exposes that field.
        Assert.NotEqual(terminalRunId.ToString(), assignment.JobId);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Scenario 4 — Re-dispatch visibility
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scenario 4: The re-dispatch card is hidden for live runs, Review runs, Decomposition runs,
    /// and terminal Implementation runs that have no provider IDs.
    /// </summary>
    [Fact]
    public async Task Redispatch_IsHidden_ForLiveRunsAndNonImplRunsAndMissingProviderIds()
    {
        // ── 4a: Live run — Cancel button is visible but no Redispatch card ──────────
        await using var agent = new FakeAgentClient("visibility-agent-4", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var (liveRunId, _, _) = await SeedDispatchAndActivateAsync(agent, "rp-vis-live");

        var runDetail = new RunDetailPage(Page, BaseUrl);
        await runDetail.NavigateAsync(liveRunId);

        // Must wait for the run content to load (Cancel button confirms it's live)
        await runDetail.CancelButton.WaitForAsync(new() { Timeout = 15_000 });
        Assert.True(await runDetail.IsRunActiveAsync(), "Run should be live");
        Assert.False(await runDetail.IsRedispatchCardVisibleAsync(),
            "Redispatch card must not be visible for a live run");

        // ── 4b: Terminal Review run ───────────────────────────────────────────────────
        var reviewRunId = Guid.NewGuid();
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = reviewRunId.ToString(),
            IssueIdentifier = "rp-vis-review",
            IssueTitle = "Review run",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Review,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-2),
        });

        await runDetail.NavigateAsync(reviewRunId.ToString());
        // NavigateAsync already waits for h1 + Blazor circuit; wait for the run-type badge to
        // confirm the page has rendered the run content before asserting.
        await Page.Locator(".run-type-review").First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible, Timeout = 10_000 });

        // TODO [WARNING]: IsReviewRunAsync, IsDecompRunAsync, and IsImplRunAsync check internal CSS
        // class names (.run-type-review, .run-type-decomp, .run-type-impl). If these class names
        // change during a refactor, these assertions will fail without any behavioral regression,
        // making the tests brittle. Prefer data-testid or ARIA role attributes that reflect
        // observable behavior rather than implementation details.
        Assert.True(await runDetail.IsReviewRunAsync(),
            "Page should show a Review run badge");
        Assert.False(await runDetail.IsRedispatchCardVisibleAsync(),
            "Redispatch card must not be visible for a Review run");

        // ── 4c: Terminal Decomposition run ─────────────────────────────────────────
        var decompRunId = Guid.NewGuid();
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = decompRunId.ToString(),
            IssueIdentifier = "rp-vis-decomp",
            IssueTitle = "Decomp run",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Decomposition,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-2),
        });

        await runDetail.NavigateAsync(decompRunId.ToString());
        await Page.Locator(".run-type-decomp").First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible, Timeout = 10_000 });

        Assert.True(await runDetail.IsDecompRunAsync(),
            "Page should show a Decomp run badge");
        Assert.False(await runDetail.IsRedispatchCardVisibleAsync(),
            "Redispatch card must not be visible for a Decomposition run");

        // ── 4d: Terminal Implementation run WITHOUT provider IDs ───────────────────
        var implNoProviderRunId = Guid.NewGuid();
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = implNoProviderRunId.ToString(),
            IssueIdentifier = "rp-vis-noprov",
            IssueTitle = "Impl run without provider IDs",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            // IssueProviderConfigId and RepoProviderConfigId left null/empty intentionally
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-2),
        });

        await runDetail.NavigateAsync(implNoProviderRunId.ToString());
        await Page.Locator(".run-type-impl").First.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible, Timeout = 10_000 });

        Assert.True(await runDetail.IsImplRunAsync(),
            "Page should show an Impl run badge");
        Assert.False(await runDetail.IsRedispatchCardVisibleAsync(),
            "Redispatch card must not be visible for a terminal Impl run missing provider IDs");

        // ── 4e: Terminal Implementation run WITH provider IDs (positive control) ────
        var implWithProviderRunId = Guid.NewGuid();
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = implWithProviderRunId.ToString(),
            IssueIdentifier = "rp-vis-withprov",
            IssueTitle = "Impl run with provider IDs",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-2),
        });
        AddIssue("rp-vis-withprov", "Impl run with provider IDs");

        await runDetail.NavigateAsync(implWithProviderRunId.ToString());
        // For the positive control, wait for the redispatch card itself to confirm full render.
        await runDetail.WaitForRedispatchCardVisibleAsync(timeoutMs: 15_000);

        Assert.True(await runDetail.IsImplRunAsync(),
            "Page should show an Impl run badge");
        Assert.True(await runDetail.IsRedispatchCardVisibleAsync(),
            "Redispatch card MUST be visible for a terminal Impl run with provider IDs (positive control)");
    }

    // ═════════════════════════════════════════════════════════════════════
    // Scenario 5 — Re-dispatch error path
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scenario 5: Attempting to re-dispatch while an active WorkItem for the same issue already
    /// exists causes the dispatch to fail with 409. The page shows "Re-dispatch failed: …" and no
    /// second WorkItem is created.
    ///
    /// <para>
    /// The guard is the application-level active-conflict check in
    /// <c>WorkItemDispatchEndpoints.CreateWorkItem</c>: it queries for any WorkItem with the same
    /// <c>IssueIdentifier</c> + <c>IssueProviderConfigId</c> in an active status
    /// (<see cref="PipelineConstants.ActiveWorkItemStatuses"/>: Pending, Dispatched, Running) and
    /// returns 409 before inserting. This check runs in application code and therefore works
    /// identically in both InMemory (E2E test) and Postgres (production) — the Postgres partial
    /// unique index is an additional backstop for concurrent races, but the application-level check
    /// fires first on the non-concurrent path tested here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Redispatch_WhenActiveWorkItemAlreadyExistsForIssue_ShowsErrorAndCreatesNoSecondWorkItem()
    {
        // Arrange: seed template and profile
        await SeedTemplateAndProfileAsync("template-rdp-err", "Redispatch Error Template");
        AddIssue("rp-rdp-err-5", "Re-dispatch error test issue");

        // Create a terminal run in history (Failed) with provider IDs — this is the run we
        // will navigate to and try to re-dispatch from.
        var terminalRunId = Guid.NewGuid();
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = terminalRunId.ToString(),
            IssueIdentifier = "rp-rdp-err-5",
            IssueTitle = "Re-dispatch error test issue",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            FailureReason = "Build failed"
        });

        // Seed an active WorkItem for the same issue + provider, simulating the scenario described
        // in the acceptance criteria: "re-dispatch while an active WorkItem for the same issue
        // already exists." CreateWorkItem's application-level activeConflict check queries for
        // (IssueIdentifier, IssueProviderConfigId) in PipelineConstants.ActiveWorkItemStatuses
        // (Pending, Dispatched, Running) and returns 409 before inserting — enforced in application
        // code, so it fires in InMemory mode exactly as in production.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new CodingAgent.Infrastructure.Persistence.Entities.WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "rp-rdp-err-5",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Running,
            AgentSelector = "",
            Payload = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId)
        });
        // TODO [WARNING]: Pass CancellationToken.None to SaveChangesAsync per project convention.
        await db.SaveChangesAsync();

        // Get the count of ALL WorkItems for this issue before the re-dispatch attempt.
        // countBefore is 1 (the Running WorkItem seeded above). After the failing re-dispatch
        // it must still be 1 — if it becomes 2 a WorkItem was created despite the 409 guard.
        // Query all statuses (not just active) so that a WorkItem created-then-terminated by
        // a failing dispatch is caught rather than silently ignored.
        await using var countDb = Fixture.DbContextFactory.CreateDbContext();
        var countBefore = await countDb.WorkItems.AsNoTracking()
            .CountAsync(w => w.IssueIdentifier == "rp-rdp-err-5");
        Assert.Equal(1, countBefore); // sanity-check: the Running seed is the only row

        // Act: navigate to the terminal run and attempt re-dispatch
        var runDetail = new RunDetailPage(Page, BaseUrl);
        await runDetail.NavigateAsync(terminalRunId.ToString());

        // Wait for the redispatch card
        await runDetail.WaitForRedispatchCardVisibleAsync(timeoutMs: 15_000);

        // Attempt the re-dispatch — this should fail (409 Conflict: active WorkItem for same issue)
        await runDetail.RedispatchAsync(confirm: true);

        // Assert: the error callout appears showing "Re-dispatch failed: ..."
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        string? errorText = null;
        while (DateTime.UtcNow < deadline)
        {
            errorText = await runDetail.GetRedispatchErrorTextAsync();
            if (errorText is not null) break;

            // If success appeared unexpectedly, fail immediately with a clear message
            if (await runDetail.IsRedispatchSuccessVisibleAsync())
            {
                Assert.Fail(
                    "Re-dispatch succeeded unexpectedly when an active WorkItem already exists for issue rp-rdp-err-5. " +
                    "This indicates the activeConflict check in CreateWorkItem did not fire.");
            }

            await Page.WaitForTimeoutAsync(200);
        }

        Assert.NotNull(errorText);
        Assert.Contains("Re-dispatch failed", errorText, StringComparison.OrdinalIgnoreCase);

        // Assert: no additional WorkItem was created for issue rp-rdp-err-5 (still exactly 1).
        await using var countDbAfter = Fixture.DbContextFactory.CreateDbContext();
        var countAfter = await countDbAfter.WorkItems.AsNoTracking()
            .CountAsync(w => w.IssueIdentifier == "rp-rdp-err-5");
        Assert.Equal(countBefore, countAfter);
    }
}
