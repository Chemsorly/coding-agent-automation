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

        // TODO [WARNING]: The statusSpan CountAsync below is called immediately after WaitForAsync
        // for the Stop Loop button. The status span may render 1-2 poll cycles later (rendering
        // is not atomic), so CountAsync can return 0 even though the span eventually appears.
        // Replace with a WaitForAsync on statusSpan before asserting CountAsync > 0.
        // Assert: loop status indicator is visible (inline status span shows cycle/processed info)
        var statusSpan = Page.Locator("span.monitoring-muted:has-text('Processed:')");
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
    }

    [Fact]
    public async Task Loop_RemoveButton_HiddenDuringActiveLoop()
    {
        // Arrange: seed a template
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

        // TODO [WARNING]: 5s timeout was set when the loop was in-process. Now goes through real
        // HTTP (Web → Scheduler → LoopStatusPollingService poll → Blazor re-render). One missed
        // 1s poll cycle already consumes 2s; consider bumping to 15s to match real HTTP latency.
        // Wait for the loop to activate (Stop Loop button appears)
        await Page.WaitForSelectorAsync("button:has-text('Stop Loop')", new() { Timeout = 5_000 });
        var removeBtnAfter = Page.Locator("button.btn-delete:has-text('Remove')");
        var removeCountAfter = await removeBtnAfter.CountAsync();
        Assert.Equal(0, removeCountAfter);

        // Cleanup: stop the loop
        var stopBtn = Page.Locator("button:has-text('Stop Loop')");
        if (await stopBtn.CountAsync() > 0)
            await stopBtn.First.ClickAsync();
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
        // TODO [WARNING]: This test does not wrap the loop start/stop in a try/finally block.
        // If WaitForAsync for "Stop Loop" or "Polling template" times out, the test throws before
        // reaching the "Stop Loop" click at the end, leaving the loop running in the Scheduler
        // host. The next test that calls ResetAll() (not ResetAllAsync()) will not wait for the
        // loop to stop — causing test pollution exactly as documented in the ResetAllAsync XML
        // comment. Wrap the act/assert section in try/finally { await Fixture.ResetAllAsync(); }.
        await Page.ClickAsync("button:has-text('Start Loop')");

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

        // Cleanup: stop the loop and wait for Start Loop to return
        await Page.ClickAsync("button:has-text('Stop Loop')");
        await Page.Locator("button:has-text('Start Loop')").First.WaitForAsync(new() { Timeout = 15_000 });
    }
}
