using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Tests that validate pipeline loop start/stop controls and UI state transitions.
/// Ensures the loop status bar, buttons, and template table reflect the correct state.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class LoopControlTests : E2ETestBase
{
    public LoopControlTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Loop_StartStop_UIReflectsState()
    {
        // Arrange: seed a template and connect an agent (loop needs at least one template)
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Loop Test Template",
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

        // Act: navigate
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Assert: Start Loop button is visible and enabled
        var startBtn = Page.Locator("button:has-text('Start Loop')");
        await startBtn.WaitForAsync(new() { Timeout = 5_000 });
        Assert.False(await startBtn.IsDisabledAsync(), "Start Loop should be enabled with templates");

        // Act: click Start Loop
        await startBtn.ClickAsync();

        // TODO [WARNING]: The 5s timeout here was set when the loop was in-process. Now the
        // signal travels Web → HTTP → Scheduler → PipelineLoopService.StartLoopAsync → response
        // → LoopStatusPollingService polls /loop/status (1s interval) → Blazor re-render.
        // One missed poll cycle already consumes 2s; a slow CI machine can exceed 5s before
        // the button flips, causing a spurious failure. The smoke test correctly uses 15s for
        // the identical wait. Consider bumping these timeouts to 15s to match real HTTP latency.
        // Wait for Stop Loop button to appear (confirms loop started)
        var stopBtn = Page.Locator("button:has-text('Stop Loop')");
        await stopBtn.First.WaitForAsync(new() { Timeout = 5_000 });

        // Assert: Stop Loop button appears, Start Loop disappears
        var stopCount = await stopBtn.CountAsync();
        Assert.True(stopCount > 0, "Stop Loop button should appear after starting the loop");

        // Assert: loop status indicator is visible (inline status span shows cycle/processed info).
        // WaitForAsync is required here: the status span is populated from the next /loop/status
        // poll response (up to 1s after IsLoopActive becomes true), so it can lag the Stop Loop
        // button by one poll cycle. CountAsync() alone would return 0 during that window.
        var statusSpan = Page.Locator("span.monitoring-muted:has-text('Processed:')");
        await statusSpan.WaitForAsync(new() { Timeout = 5_000 });
        var statusSpanCount = await statusSpan.CountAsync();
        Assert.True(statusSpanCount > 0, "Loop status indicator should be visible when loop is active");

        // Act: click Stop Loop
        await stopBtn.First.ClickAsync();

        // Wait for Start Loop button to reappear (confirms loop stopped)
        var startBtnAfter = Page.Locator("button:has-text('Start Loop')");
        await startBtnAfter.First.WaitForAsync(new() { Timeout = 5_000 });

        // Assert: Start Loop button returns
        var startCount = await startBtnAfter.CountAsync();
        Assert.True(startCount > 0, "Start Loop button should return after stopping the loop");
        // TODO [WARNING]: This test does not call Fixture.ResetAllAsync() after stopping the loop
        // via the UI. The UI click fires over HTTP; by the time the assertion completes,
        // PipelineLoopService.IsLoopActive may still be true while the Scheduler finishes its
        // current cycle. If a subsequent test runs before the cycle drains it will find a "Stop
        // Loop" button where "Start Loop" is expected. Add a try/finally wrapping the test body
        // (like the smoke test) that calls await Fixture.ResetAllAsync() to guarantee the loop
        // is fully stopped and IsLoopActive is false before the next test begins.
    }

    [Fact]
    public async Task Loop_TemplateActions_AvailableWhileLoopRuns()
    {
        // Arrange: seed a template and agent profile
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Loop Template",
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

        // Act: navigate
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Assert: Remove button is visible before loop starts
        var removeBtnBefore = Page.Locator("button.btn-delete:has-text('Remove')");
        var removeCountBefore = await removeBtnBefore.CountAsync();
        Assert.True(removeCountBefore > 0, "Remove button should be visible when loop is not active");

        // Act: start the loop
        await Page.ClickAsync("button:has-text('Start Loop')");

        try
        {
            // Wait for the loop to activate (Stop Loop button appears). Use 15s to account for
            // the real HTTP path: Web → Scheduler → LoopStatusPollingService poll → Blazor re-render.
            await Page.Locator("button:has-text('Stop Loop')").First.WaitForAsync(new() { Timeout = 15_000 });

            // Assert: all template action buttons remain visible while the loop is active
            var removeBtn = Page.Locator("button.btn-delete:has-text('Remove')");
            await removeBtn.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            Assert.True(await removeBtn.CountAsync() > 0, "Remove button should be visible while loop is active");

            var editBtn = Page.Locator("button.btn-edit:has-text('Edit')");
            await editBtn.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            Assert.True(await editBtn.CountAsync() > 0, "Edit button should be visible while loop is active");

            var addBtn = Page.Locator("button.btn-add:has-text('Add Template')");
            await addBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            Assert.True(await addBtn.CountAsync() > 0, "Add Template button should be visible while loop is active");
        }
        finally
        {
            await Fixture.ResetAllAsync();
        }
    }

    [Fact]
    public async Task Loop_AddTemplate_WhileLoopRuns_SavesAndKeepsLoopRunning()
    {
        // Arrange: seed template, agent profile, and two extra providers before navigating
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Loop Template",
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

        // Two extra providers are needed so the new template can use different providers than template-1
        // (enabled templates may not share an issue tracker or repository).
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "issue-e2e-2",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "E2E Issue Provider 2"
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-e2e-2",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "E2E Repo Provider 2"
        }, CancellationToken.None);

        // Act: navigate, start loop, wait for it to become active
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await Page.ClickAsync("button:has-text('Start Loop')");

        try
        {
            await Page.Locator("button:has-text('Stop Loop')").First.WaitForAsync(new() { Timeout = 15_000 });

            // Open the add form
            await Page.ClickAsync("button.btn-add:has-text('Add Template')");
            await Page.WaitForSelectorAsync("div.provider-form", new() { Timeout = 5_000 });

            // Type the template name into the first text input in the form
            var nameInput = Page.Locator("div.provider-form input[type='text']").First;
            await nameInput.FillAsync("Added While Running");

            // Wait 3 seconds — the page re-renders on AutoRefresh every 3s while HasLiveValues=true
            // (loop active + form open). The typed name must survive the re-render.
            await Page.WaitForTimeoutAsync(3_000);
            var nameValue = await nameInput.InputValueAsync();
            Assert.Equal("Added While Running", nameValue);

            // Select providers: Nth(1) = Issue Provider, Nth(2) = Repo Provider
            var issueSelect = Page.Locator("div.provider-form select").Nth(1);
            await issueSelect.SelectOptionAsync(new SelectOptionValue { Label = "E2E Issue Provider 2" });
            var repoSelect = Page.Locator("div.provider-form select").Nth(2);
            await repoSelect.SelectOptionAsync(new SelectOptionValue { Label = "E2E Repo Provider 2" });

            // Save
            await Page.ClickAsync("div.form-buttons button.btn-save");

            // Assert: new template row appears
            await Page.WaitForSelectorAsync("td:has-text('Added While Running')", new() { Timeout = 15_000 });

            // Assert: success message mentions next cycle
            // TODO: [WARNING] The CSS comma-selector below is passed to Playwright's querySelector
            // (single-element match). If the first DOM element matching [class*='success'] is a
            // status badge or feature badge rather than the save-confirmation banner, TextContentAsync
            // returns the wrong element's text and the "next cycle" assertion fails spuriously.
            // Replace with the specific selector used by the success-notification element in
            // AgentCoding.razor (e.g. the same selector used by other E2E success-message assertions).
            var successMsg = await Page.TextContentAsync(".settings-status.status-success, .inline-status-success, [class*='success']");
            Assert.Contains("next cycle", successMsg ?? "", StringComparison.OrdinalIgnoreCase);

            // Assert: loop is still running
            var stopBtn = Page.Locator("button:has-text('Stop Loop')");
            Assert.True(await stopBtn.CountAsync() > 0, "Stop Loop should still be visible after adding a template");

            // Assert: template was persisted to the config store
            var templates = await Fixture.ConfigStore.LoadAllTemplatesAsync(CancellationToken.None);
            Assert.Contains(templates, t => t.Name == "Added While Running");
        }
        finally
        {
            await Fixture.ResetAllAsync();
        }
    }

    [Fact]
    public async Task Loop_TemplateToggle_ShowsNextCycleIndicator()
    {
        // Arrange: seed a template
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Toggle Template",
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

        // Act: navigate and start loop
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await Page.ClickAsync("button:has-text('Start Loop')");

        // TODO [WARNING]: 5s timeout was set when the loop was in-process. Now goes through real
        // HTTP (Web → Scheduler → LoopStatusPollingService poll → Blazor re-render). One missed
        // 1s poll cycle already consumes 2s; consider bumping to 15s to match real HTTP latency.
        // Wait for the loop to activate (Stop Loop button appears)
        await Page.WaitForSelectorAsync("button:has-text('Stop Loop')", new() { Timeout = 5_000 });

        // Act: toggle the template's enabled state
        // The hidden input is not directly clickable (opacity:0, width/height:0);
        // click the visible .toggle-slider instead, which is how the toggle works.
        var toggleSwitch = Page.Locator(".toggle-switch .toggle-slider").First;
        await toggleSwitch.ClickAsync();

        // Wait for the "next cycle" indicator to appear
        var nextCycleText = Page.Locator("text=next cycle");
        await nextCycleText.First.WaitForAsync(new() { Timeout = 5_000 });

        // Assert: "next cycle" indicator appears
        var nextCycleCount = await nextCycleText.CountAsync();
        Assert.True(nextCycleCount > 0, "Toggling a template during active loop should show 'next cycle' indicator");

        // Cleanup: stop the loop
        var stopBtn = Page.Locator("button:has-text('Stop Loop')");
        if (await stopBtn.CountAsync() > 0)
            await stopBtn.First.ClickAsync();
        // TODO [WARNING]: Same isolation hazard as Loop_RemoveButton_HiddenDuringActiveLoop: this
        // cleanup fires-and-forgets over real HTTP without awaiting Fixture.ResetAllAsync(). The
        // Scheduler loop may still be active when the next test begins, leaving a stale cycle in
        // flight. Wrap the test body in try/finally and call await Fixture.ResetAllAsync() in the
        // finally block to guarantee IsLoopActive is false before the next test starts.
    }

    /// <summary>
    /// Smoke test: verifies the full Web → Scheduler → Web status-propagation path.
    ///
    /// Starts the loop via the UI (which calls the real Scheduler over HTTP), then asserts that
    /// the "Polling template …" status line appears — confirming that
    /// <see cref="CodingAgent.Web.Services.LoopStatusPollingService"/> successfully polled
    /// <c>GET /loop/status</c> on the Scheduler and the Blazor page reflected the result.
    /// </summary>
    [Fact]
    public async Task Scheduler_StartLoopFromPipelinesPage_StatusLineReflectsSchedulerState()
    {
        // Arrange: seed template + agent profile
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-smoke",
            Name = "Scheduler Smoke Template",
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

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Act: start loop via UI button — goes through real HttpSchedulerApiClient →
        //      Scheduler POST /loop/start → PipelineLoopService.StartLoopAsync().
        // The try/finally guarantees ResetAllAsync() runs even if a WaitForAsync times out,
        // preventing the running loop from leaking into the next test ("Stop Loop where Start
        // Loop was expected" pollution documented in E2EFixture.ResetAllAsync).
        await Page.ClickAsync("button:has-text('Start Loop')");

        try
        {
            // LoopStatusPollingService in the Web host polls GET /loop/status on the Scheduler every
            // 1 second (SchedulerApi__StatusPollIntervalSeconds=1 set in E2EWebApplicationFactory).
            // The Stop Loop button appears once IsLoopActive=true propagates through the polling cycle.
            // TODO [WARNING]: This uses a 15s timeout vs the 5s used by the other LoopControlTests.
            // In a failure scenario both WaitForAsync calls below can expire sequentially (30s total)
            // before the test reports failure, consuming a significant portion of the 15-minute budget.
            // The discrepancy also documents that the old 5s timeouts in the other tests are now
            // under-budgeted for the real HTTP path — see TODO comments on those tests.
            await Page.Locator("button:has-text('Stop Loop')").First.WaitForAsync(new() { Timeout = 15_000 });

            // Assert: the "Polling template N of M · Processed: P · Failed: F" status span appears.
            // This span is only rendered when LoopService.IsLoopActive is true (AgentCoding.razor ~L122),
            // confirming the status data flowed from the real Scheduler via HTTP.
            var statusSpan = Page.Locator("span.monitoring-muted:has-text('Polling template')");
            await statusSpan.WaitForAsync(new() { Timeout = 15_000 });
            Assert.True(await statusSpan.IsVisibleAsync(), "Status span should be visible after loop started via Scheduler");
            // TODO [WARNING]: This assertion verifies the span is visible, but the selector
            // 'has-text("Polling template")' would also match a hard-coded static string baked into
            // the Blazor component with no data from the Scheduler. A stronger assertion would
            // extract the span's text content and verify it contains a dynamic value — e.g. a
            // template name ("Scheduler Smoke Template") or a numeric index — proving the status
            // data actually came from the Scheduler's /loop/status response rather than a static
            // placeholder. Consider: var text = await statusSpan.InnerTextAsync();
            // Assert.Contains("Scheduler Smoke Template", text);
        }
        finally
        {
            // Stop the loop and wait for Start Loop to return before allowing the next test to run.
            // ResetAllAsync() stops the loop via PipelineLoopService.StopLoop() and busy-waits up
            // to 10s for IsLoopActive to become false, preventing the "Stop Loop where Start Loop
            // was expected" pollution in subsequent tests.
            await Fixture.ResetAllAsync();
        }
    }
}
