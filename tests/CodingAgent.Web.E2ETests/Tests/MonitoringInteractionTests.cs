using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Run/agent interaction coverage, migrated from the retired /agent-monitoring page to the cockpit
/// pages that replaced it: active + queued work on /work, the live run detail on /runs/{id}, and
/// agent status on /fleet. The dispatch + fake-agent arrange is unchanged; only the UI assertions
/// were retargeted. (The old run-detail modal is now a full page, so the "closes on Escape" modal
/// test was retired — RunPage is navigated to and away from, not opened/closed as an overlay.)
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class MonitoringInteractionTests : E2ETestBase
{
    public MonitoringInteractionTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Seeds a template/profile/issue, dispatches it, then has the connected <paramref name="agent"/>
    /// accept the job and report <paramref name="step"/>. Returns the active run's id once the server
    /// reflects the step. Mirrors the arrange the old monitoring tests repeated inline.
    /// </summary>
    private async Task<string> SeedDispatchAndActivateAsync(
        FakeAgentClient agent, string templateName, string issueId, PipelineStep step = PipelineStep.GeneratingCode)
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = $"Issue {issueId} test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync(templateName);
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueId);
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, step);

        var runService = Fixture.RunService;
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == issueId && r.CurrentStep == step));
        return runService.GetActiveRuns().First(r => r.IssueIdentifier == issueId).RunId;
    }

    /// <summary>
    /// Seeds a template/profile/issue and dispatches via the UI without connecting an agent, so
    /// the WorkItem stays Pending. Returns after the dispatch success indicator appears.
    /// </summary>
    private async Task SeedAndDispatchWithoutAgentAsync(string templateName, string issueId)
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = $"Issue {issueId} test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync(templateName);
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueId);
        await codingPage.ClickStartPipelineAsync();

        // Wait for the canonical post-dispatch success indicator — the same pattern used by all
        // other tests. This ensures the WorkItem has been created as Pending before navigating.
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });
    }

    [Fact]
    public async Task Work_ActiveRun_ShowsInFlight()
    {
        await using var fakeAgent = new FakeAgentClient("monitor-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await SeedDispatchAndActivateAsync(fakeAgent, "Monitor Template", "70");

        // Assert: the in-flight run is visible on /work.
        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();
        await work.WaitForInFlightAsync("70", timeoutMs: 15_000);
        Assert.True(await work.IsIssueInFlightAsync("70"), "Active run #70 should appear in the Work 'In flight' table");
    }

    [Fact]
    public async Task ActiveRun_RowClick_OpensRunDetailPage()
    {
        await using var fakeAgent = new FakeAgentClient("modal-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var runId = await SeedDispatchAndActivateAsync(fakeAgent, "Modal Template", "71");

        // Act: the Overview "Active runs" card lists runs and navigates to /runs/{id} on click.
        await Page.GotoAsync($"{BaseUrl}/overview");
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        var runRow = Page.Locator(".cockpit-run-row").Filter(new() { HasTextString = "#71" });
        await runRow.First.WaitForAsync(new() { Timeout = 15_000 });

        // Start the URL-wait task BEFORE the click so no history.pushState event is missed.
        // Blazor's Nav.NavigateTo fires a history.pushState (not a network "load" event) —
        // starting WaitForURLAsync after ClickAsync creates a race where the SPA navigation
        // can fire and complete before the listener is attached, causing a 15s timeout.
        var navTask = Page.WaitForURLAsync($"**/runs/{runId}",
            new() { WaitUntil = WaitUntilState.Commit, Timeout = 15_000 });
        await runRow.First.ClickAsync();
        await navTask;

        // Wait for the run content to render — RunPage.razor does an async API call in
        // OnParametersSetAsync before populating the page body. WaitForURLAsync with Commit only
        // waits for the SPA navigation push, not for Blazor to finish rendering.
        await Page.Locator("h1").Filter(new() { HasTextString = "#71" }).WaitForAsync(new() { Timeout = 15_000 });
        var pageText = await Page.TextContentAsync("body");
        Assert.Contains("#71", pageText);
    }

    [Fact]
    public async Task RunDetailPage_ShowsPipelineProgress_ForActiveRun()
    {
        await using var fakeAgent = new FakeAgentClient("detail-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var runId = await SeedDispatchAndActivateAsync(fakeAgent, "Detail Template", "74");

        // The run detail page renders the live "Pipeline progress" card (PipelineSidebar) for active runs.
        var detail = new RunDetailPage(Page, BaseUrl);
        await detail.NavigateAsync(runId);
        await detail.PipelineProgressCard.First.WaitForAsync(new() { Timeout = 15_000 });
        Assert.True(await detail.PipelineProgressCard.CountAsync() > 0, "Run detail page should show the Pipeline progress card for an active run");

        var pageText = await detail.GetPageTextAsync();
        Assert.Contains("#74", pageText);
    }

    [Fact]
    public async Task Fleet_AgentStatus_ShowsBusyDuringJob()
    {
        await using var fakeAgent = new FakeAgentClient("status-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await SeedDispatchAndActivateAsync(fakeAgent, "Status Template", "73");

        // Assert: the agent shows "Busy" on /fleet (poll — the fleet view auto-refreshes on a timer).
        var fleet = new FleetPage(Page, BaseUrl);
        await fleet.NavigateAsync();
        await fleet.WaitForAgentStatusAsync("status-agent-1", "Busy", timeoutMs: 15_000);
        Assert.True(await fleet.IsAgentVisibleAsync("status-agent-1"), "Busy agent should be visible on Fleet");
    }

    /// <summary>
    /// Scenario 4 (revived): dispatching without a connected agent leaves the WorkItem in the
    /// Queue card only. Once an agent connects and claims the item, it moves to In flight.
    ///
    /// The original Skip reason ("Legacy mode fails dispatch immediately…") is obsolete — the
    /// current E2E harness uses DB mode with <see cref="FakeJobController"/>, which only dispatches
    /// when an idle agent is present.
    /// </summary>
    [Fact]
    public async Task Work_UnassignedRun_ShowsInQueueOnly_NotInFlight()
    {
        await SeedAndDispatchWithoutAgentAsync("Queue Only Template", "80");

        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();

        // WaitForQueuedAsync is required here — NavigateAsync uses a fixed 2s sleep which is not
        // a deterministic signal that the newly-created Pending WorkItem is visible on the page.
        await work.WaitForQueuedAsync("80", timeoutMs: 15_000);

        // Queued but not in flight (no agent assigned yet).
        Assert.True(await work.IsIssueQueuedAsync("80"), "Issue #80 should appear in the Queue");
        Assert.False(await work.IsIssueInFlightAsync("80"), "Issue #80 should NOT be in flight while unassigned");

        // Connect an agent; the job should move to in flight.
        await using var fakeAgent = new FakeAgentClient("late-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);

        var runService = Fixture.RunService;
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == "80" && r.AgentId == "late-agent-1"));

        await work.NavigateAsync();
        await work.WaitForInFlightAsync("80", timeoutMs: 15_000);
        Assert.True(await work.IsIssueInFlightAsync("80"), "Issue #80 should move to In flight after an agent picks it up");
    }

    /// <summary>
    /// Scenario 1: Removing a queued item via the UI cancels the WorkItem in the database and
    /// prevents FakeJobController from claiming it.
    /// </summary>
    [Fact]
    public async Task Work_RemoveQueuedItem_DisappearsAndIsCancelled()
    {
        // Arrange: dispatch two issues without an agent so both stay Pending.
        await SeedAndDispatchWithoutAgentAsync("Remove Template", "91");

        // Second dispatch reuses the same template/profile (already saved); just add the issue
        // and dispatch it. The template-save is idempotent so calling SeedAndDispatchWithoutAgentAsync
        // again would overwrite template-1 harmlessly, but we call the private method directly
        // rather than duplicating the boilerplate.
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "92",
            Title = "Issue 92 test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Remove Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("92");
        await codingPage.ClickStartPipelineAsync();
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });

        // Resolve the GUID for issue 91 before connecting any agent (so the controller cannot
        // claim items between removal and assertion).
        // TODO: Both item91 and item92 are resolved from a single snapshot taken here. The snapshot
        // is safe because GetPendingAsync runs after the .settings-status.status-success wait for
        // issue 92, which guarantees both WorkItems are persisted as Pending before this call.
        // However, if FakeJobController.PollAsync races and claims either item in the narrow window
        // between the success-wait and GetPendingAsync, pendingItems.First(...) will throw an
        // InvalidOperationException. A more robust approach is to resolve each GUID independently
        // with a retry/poll after each dispatch, eliminating the shared-snapshot assumption.
        var pendingItems = await Fixture.WorkItems.GetPendingAsync(ct: CancellationToken.None);
        var item91 = pendingItems.First(p => p.IssueIdentifier == "91");

        // Act: navigate to /work and remove issue 91.
        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();
        await work.WaitForQueuedAsync("91", timeoutMs: 15_000);
        await work.WaitForQueuedAsync("92", timeoutMs: 15_000);

        await work.RemoveQueuedAsync("91");

        // The row should disappear from the Queue card.
        await QueueRow91DisappearsAsync(work);
        Assert.False(await work.IsIssueQueuedAsync("91"), "Issue #91 row should disappear after Remove");

        // The WorkItem should be Cancelled in the database. WaitForWorkItemStatusAsync is on
        // HeadlessE2ETestBase which this class does not inherit — use inline EF poll instead.
        // TODO: using var db (synchronous Dispose) is used inside a Func<bool> polling lambda.
        // DbContext.Dispose() is safe against the in-memory provider, but callers at all other
        // sites in the suite use "await using" (IAsyncDisposable). Now that E2ETestBase has a
        // WaitUntilAsync(Func<Task<bool>>) overload, migrate this to an async lambda:
        //   await WaitUntilAsync(async () => {
        //       await using var db = await Fixture.DbContextFactory.CreateDbContextAsync();
        //       var entity = await db.WorkItems.AsNoTracking()
        //           .FirstOrDefaultAsync(w => w.Id == item91.Id);
        //       return entity?.Status == WorkItemStatus.Cancelled;
        //   }, timeout: TimeSpan.FromSeconds(15));
        await WaitUntilAsync(() =>
        {
            using var db = Fixture.DbContextFactory.CreateDbContext();
            var entity = db.WorkItems.AsNoTracking().FirstOrDefault(w => w.Id == item91.Id);
            return entity?.Status == WorkItemStatus.Cancelled;
        }, timeout: TimeSpan.FromSeconds(15));

        // Connect an agent — FakeJobController should claim only issue 92, not the cancelled 91.
        await using var fakeAgent = new FakeAgentClient("remove-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The claimed item must be 92, not the removed 91.
        // TODO: This assertion is logically sound (WaitUntilAsync above already confirmed item 91
        // is Cancelled in the DB, so the poll loop will skip it). However, the ordering is
        // misleading: if FakeJobController raced and claimed item 91 before the DB write completed,
        // ClaimedWorkItemIds would already contain item91 at this point and DoesNotContain would
        // fail — but the failure message would not indicate the race. Consider asserting
        // DoesNotContain *before* connecting the agent as an earlier, cleaner signal.
        Assert.DoesNotContain(item91.Id, Fixture.JobController.ClaimedWorkItemIds);
        var item92 = pendingItems.First(p => p.IssueIdentifier == "92");
        Assert.Contains(item92.Id, Fixture.JobController.ClaimedWorkItemIds);
    }

    // Helper: polls until the Queue row for issue 91 disappears. Using a Playwright wait is more
    // robust than a fixed delay because Work.razor refreshes asynchronously after CancelWorkItemAsync.
    // TODO: This helper is hard-coded to issue identifier "91" and is not reusable. When a
    // WaitForNotQueuedAsync(issueIdentifier) helper is added to WorkPage (mirroring WaitForQueuedAsync),
    // this private method can be removed and all callers updated to use the page-object equivalent.
    // Also note: the silent-return on timeout means failures here surface only via the subsequent
    // Assert.False with no indication of how long was waited; a proper WaitForAsync(Hidden) on
    // the Playwright locator would produce a more informative timeout message.
    private static async Task QueueRow91DisappearsAsync(WorkPage work)
    {
        // WaitForAsync with State.Hidden / detached is not available on a filtered locator in all
        // versions; poll IsIssueQueuedAsync instead (same poll interval as WaitUntilAsync).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (!await work.IsIssueQueuedAsync("91")) return;
            await Task.Delay(200);
        }
        // TODO: Silently returning here on timeout means the caller's Assert.False surfaces the
        // failure without timing context ("waited 15 s, row was still visible"). Consider throwing
        // a TimeoutException instead, or adding a WaitForNotQueuedAsync(string issueIdentifier)
        // helper to WorkPage that uses a Playwright locator wait with WaitForSelectorState.Hidden,
        // which produces a more informative failure message. The helper is also hard-coded to "91"
        // and cannot be reused for other identifiers — see the matching SUGGESTION in the review.
        // Let the final assertion surface the failure with a meaningful message.
    }

    /// <summary>
    /// Scenario 2: Setting a higher priority weight for item C causes FakeJobController to claim
    /// C before A (oldest), demonstrating that PriorityWeight DESC is the deciding sort key.
    /// </summary>
    [Fact]
    public async Task Work_Priority_ChangesClaimOrder()
    {
        // Arrange: dispatch A, B, C in order (oldest first) without any agent.
        await SeedAndDispatchWithoutAgentAsync("Priority Template", "93");

        foreach (var id in new[] { "94", "95" })
        {
            Fixture.IssueProvider.Issues.Add(new IssueDetail
            {
                Identifier = id,
                Title = $"Issue {id} test",
                Description = "Test",
                Labels = new[] { "enhancement" }
            });
            var cp = new AgentCodingPage(Page, BaseUrl);
            await cp.NavigateAsync();
            await cp.SelectTemplateAsync("Priority Template");
            await cp.ClickBrowseIssuesAsync();
            await cp.SelectIssueAsync(id);
            await cp.ClickStartPipelineAsync();
            await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });
        }

        // Resolve GUIDs from the API now (before any agent connects). The API returns items in
        // PriorityWeight DESC, CreatedAt ASC order; all three are weight 0 so they appear in
        // creation order: A=93, B=94, C=95.
        var pendingItems = await Fixture.WorkItems.GetPendingAsync(ct: CancellationToken.None);
        var guidA = pendingItems.First(p => p.IssueIdentifier == "93").Id;
        var guidC = pendingItems.First(p => p.IssueIdentifier == "95").Id;

        // Act: navigate to /work and raise C's (issue 95) priority to 500.
        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();
        await work.WaitForQueuedAsync("93", timeoutMs: 15_000);
        await work.WaitForQueuedAsync("94", timeoutMs: 15_000);
        await work.WaitForQueuedAsync("95", timeoutMs: 15_000);

        await work.SetPriorityAsync("95", 500);

        // Wait deterministically for the Blazor @onchange → SetPriorityAsync → API → DB write
        // to complete before navigating away. Polling GetPendingAsync is the only reliable signal
        // that the priority persisted; a fixed Task.Delay would be fragile on loaded CI runners.
        await WaitUntilAsync(
            async () =>
            {
                var items = await Fixture.WorkItems.GetPendingAsync(ct: CancellationToken.None);
                return items.Any(p => p.IssueIdentifier == "95" && p.PriorityWeight == 500);
            },
            timeout: TimeSpan.FromSeconds(15));

        // Navigate away and back — verify C's weight persisted.
        await Page.GotoAsync($"{BaseUrl}/overview");
        await work.NavigateAsync();
        await work.WaitForQueuedAsync("95", timeoutMs: 15_000);

        var priorityInput = work.QueueRow("95").Locator("input.priority-input");
        var inputValue = await priorityInput.InputValueAsync();
        Assert.Equal("500", inputValue);

        // Connect agent 1 — FakeJobController will claim C (weight 500 > 0, dispatched first).
        await using var agent1 = new FakeAgentClient("prio-agent-1", "e2e");
        await agent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await agent1.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The first claimed item must be C (95).
        Assert.True(
            Fixture.JobController.ClaimedWorkItemIds.Count >= 1,
            "FakeJobController should have claimed at least one item after agent 1 connected");
        Assert.Equal(guidC, Fixture.JobController.ClaimedWorkItemIds[0]);

        // TODO: Assert Count == 1 here before connecting agent 2. If the background poll fires
        // twice between agent 1 connecting and this point, both weight-0 items (A=93, B=94) could
        // be claimed before agent 2 connects, making ClaimedWorkItemIds[1] indeterminate. Adding
        // Assert.Equal(1, Fixture.JobController.ClaimedWorkItemIds.Count) provides an unambiguous
        // signal that exactly one item has been claimed so far and the second slot is empty.

        // Connect agent 2 — next claim should be A (93), oldest among weight-0 items.
        await using var agent2 = new FakeAgentClient("prio-agent-2", "e2e");
        await agent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await agent2.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(
            Fixture.JobController.ClaimedWorkItemIds.Count >= 2,
            "FakeJobController should have claimed at least two items after agent 2 connected");
        Assert.Equal(guidA, Fixture.JobController.ClaimedWorkItemIds[1]);
    }

    /// <summary>
    /// Scenario 3: Entering a value outside 0–1000 is silently rejected by Work.razor's
    /// @onchange guard. No priority error is shown, and the stored weight remains unchanged.
    /// </summary>
    [Fact]
    public async Task Work_Priority_InvalidInput_Rejected()
    {
        // Arrange: dispatch one issue without an agent.
        await SeedAndDispatchWithoutAgentAsync("Validation Template", "96");

        // Resolve GUID and record the *initial* priority weight. Items dispatched manually via the
        // UI get PriorityWeight = 100 (InitiatedByConstants.IsManual returns true), not 0. Checking
        // against a hard-coded 0 would immediately fail because the weight is already 100 at rest.
        // Capturing the baseline here makes the assertion below independent of dispatch mode.
        var pendingItems = await Fixture.WorkItems.GetPendingAsync(ct: CancellationToken.None);
        var item = pendingItems.First(p => p.IssueIdentifier == "96");
        var initialWeight = item.PriorityWeight;

        // Act: navigate to /work and enter an out-of-range value.
        var work = new WorkPage(Page, BaseUrl);
        await work.NavigateAsync();
        await work.WaitForQueuedAsync("96", timeoutMs: 15_000);

        // FillAsync bypasses the HTML max="1000" attribute and writes "5000" directly into the
        // DOM. Blazor's @onchange guard fires on blur and rejects the value silently (no API call).
        var input = work.QueueRow("96").Locator("input.priority-input");
        await input.FillAsync("5000");
        await input.BlurAsync();

        // Assert 1: wait for the DB to confirm the weight stays at initialWeight for a full
        // stabilisation window. Polling over 3 seconds with 100 ms intervals: if the @onchange
        // guard is working correctly no API call is made and the weight never changes. If the guard
        // is broken and an API call eventually lands, the weight will differ from initialWeight and
        // this assertion will fail with a clear message rather than a vacuous pass. A fixed
        // Task.Delay cannot distinguish "guard suppressed the call" from "API call in-flight but
        // not yet written".
        var weightStayedAtInitial = true;
        var stabilisationDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < stabilisationDeadline)
        {
            var items = await Fixture.WorkItems.GetPendingAsync(ct: CancellationToken.None);
            var current = items.FirstOrDefault(p => p.IssueIdentifier == "96");
            if (current is not null && current.PriorityWeight != initialWeight)
            {
                weightStayedAtInitial = false;
                break;
            }
            await Task.Delay(100);
        }
        Assert.True(weightStayedAtInitial, $"PriorityWeight changed from {initialWeight} — the @onchange guard did not reject the out-of-range value");

        // Assert 2: no priority error text is visible on the page. Because we have already waited
        // 3 seconds above, any async error rendering would have completed. Use Playwright's
        // WaitForFunctionAsync with a short timeout to confirm absence; this blocks until the
        // predicate is true (no error text) rather than sampling at an arbitrary point in time.
        // TODO: Once Work.razor exposes a stable selector for the error element (e.g.
        // [data-testid='priority-error']), replace this body-text scan with a locator
        // IsHiddenAsync check, which is both faster and more targeted.
        await Page.WaitForFunctionAsync(
            "() => !document.body.innerText.includes('Priority update failed')",
            null,
            new() { Timeout = 3_000 });

        // Assert 3: confirm the DB weight directly with a fresh context (belt-and-suspenders check
        // complementing the stabilisation poll above). Compare against initialWeight, not a
        // hard-coded 0, because manually-dispatched items start at 100 not 0.
        // TODO: Use the async factory overload for consistency with all other call sites:
        //   await using var db = await Fixture.DbContextFactory.CreateDbContextAsync();
        // The synchronous CreateDbContext() assigns to an await-using variable (IAsyncDisposable),
        // which works but is inconsistent — the returned context is constructed synchronously and
        // "await" in the disposal is a no-op over DisposeAsync(). Prefer the async factory path.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == item.Id);
        Assert.NotNull(entity);
        Assert.Equal(initialWeight, entity.PriorityWeight);
    }
}
