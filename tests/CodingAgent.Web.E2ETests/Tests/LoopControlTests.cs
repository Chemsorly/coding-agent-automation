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

    /// <summary>
    /// Start and Stop only record the request (spec 051 D11). The Scheduler's watchdog applies it within
    /// a tick (250 ms in the harness) and pushes its status, and the page reads it every 5 s: 20 s covers
    /// two page refreshes and a slow CI machine.
    /// </summary>
    private const int LoopTimeoutMs = 20_000;

    /// <summary>Waits until the page shows the loop running: its progress line names the template it polls.</summary>
    private Task WaitForLoopRunningAsync() =>
        Page.Locator("[data-testid='loop-progress']:has-text('Polling template')").WaitForAsync(new() { Timeout = LoopTimeoutMs });

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

        // Start records the request at once, so Stop Loop appears right away (it also withdraws a
        // Start the Scheduler has not applied yet).
        var stopBtn = Page.Locator("button:has-text('Stop Loop')");
        await stopBtn.First.WaitForAsync(new() { Timeout = 5_000 });

        // Assert: Stop Loop button appears, Start Loop disappears
        var stopCount = await stopBtn.CountAsync();
        Assert.True(stopCount > 0, "Stop Loop button should appear after starting the loop");

        // Assert: the progress line shows the running loop. Start only records the request, so it
        // appears once the Scheduler's watchdog has started the loop and pushed its status, and
        // the page has read it (it refreshes every 5 s).
        var statusSpan = Page.Locator("span.monitoring-muted:has-text('Processed:')");
        await statusSpan.WaitForAsync(new() { Timeout = LoopTimeoutMs });
        var statusSpanCount = await statusSpan.CountAsync();
        Assert.True(statusSpanCount > 0, "Loop status indicator should be visible when loop is active");

        // Act: click Stop Loop
        await stopBtn.First.ClickAsync();

        // Wait for Start Loop button to reappear (confirms loop stopped)
        var startBtnAfter = Page.Locator("button:has-text('Start Loop')");
        await startBtnAfter.First.WaitForAsync(new() { Timeout = LoopTimeoutMs });

        // Assert: Start Loop button returns
        var startCount = await startBtnAfter.CountAsync();
        Assert.True(startCount > 0, "Start Loop button should return after stopping the loop");
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
            // Start only records the request; wait until the Scheduler has started the loop.
            await WaitForLoopRunningAsync();

            // Assert: all template action buttons remain visible while the loop is active
            var removeBtn = Page.Locator("button.btn-delete:has-text('Remove')");
            await removeBtn.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            Assert.True(await removeBtn.CountAsync() > 0, "Remove button should be visible while loop is active");

            var editBtn = Page.Locator("a.btn-edit:has-text('Edit')");
            await editBtn.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            Assert.True(await editBtn.CountAsync() > 0, "Edit link should be visible while loop is active");

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
            await WaitForLoopRunningAsync();

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

        // Wait for the loop to run: the toggle note shows only while it does.
        await WaitForLoopRunningAsync();

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
    /// Smoke test: the full desired-state path (spec 051 D11). Start in the UI writes the request through
    /// the API host; the Scheduler's watchdog starts the loop and pushes its status to the API; the page
    /// reads it back and shows the "Polling template …" progress line with the Scheduler's counts.
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

        // Act: start the loop with the button. Since spec 051 D11 that records the request through
        // the API host (/api/ui/loop/start); the Scheduler's watchdog starts the loop and pushes its
        // status, which the page reads from /api/ui/loop/status. The try/finally runs ResetAllAsync
        // even if a wait times out, so the running loop does not leak into the next test.
        await Page.ClickAsync("button:has-text('Start Loop')");

        try
        {
            // The progress line only shows a loop the Scheduler reported running; its counts come
            // from that report.
            await WaitForLoopRunningAsync();
            var progress = await Page.Locator("[data-testid='loop-progress']").InnerTextAsync();
            Assert.Matches(@"Polling template \d+ of [1-9]\d* · Processed: \d+ · Failed: \d+", progress);
        }
        finally
        {
            await Fixture.ResetAllAsync();
        }
    }
}
