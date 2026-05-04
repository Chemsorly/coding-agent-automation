using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Tests that validate pipeline loop start/stop controls and UI state transitions.
/// Ensures the loop status bar, buttons, and template table reflect the correct state.
/// </summary>
[Trait("Category", "E2E")]
public sealed class LoopControlTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public LoopControlTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Loop_StartStop_UIReflectsState()
    {
        // Arrange: seed a template and connect an agent (loop needs at least one template)
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-1",
                    Name = "Loop Test Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
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
        await Page.WaitForTimeoutAsync(1000);

        // Assert: Stop Loop button appears, Start Loop disappears
        var stopBtn = Page.Locator("button:has-text('Stop Loop')");
        var stopCount = await stopBtn.CountAsync();
        Assert.True(stopCount > 0, "Stop Loop button should appear after starting the loop");

        // Assert: loop status bar is visible
        var statusBar = Page.Locator(".loop-status-bar");
        var statusBarCount = await statusBar.CountAsync();
        Assert.True(statusBarCount > 0, "Loop status bar should be visible when loop is active");

        // Act: click Stop Loop
        await stopBtn.First.ClickAsync();
        await Page.WaitForTimeoutAsync(1000);

        // Assert: Start Loop button returns
        var startBtnAfter = Page.Locator("button:has-text('Start Loop')");
        var startCount = await startBtnAfter.CountAsync();
        Assert.True(startCount > 0, "Start Loop button should return after stopping the loop");
    }

    [Fact]
    public async Task Loop_RemoveButton_HiddenDuringActiveLoop()
    {
        // Arrange: seed a template
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-1",
                    Name = "Loop Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
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
        await Page.WaitForTimeoutAsync(1000);

        // Assert: Remove button is hidden during active loop
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
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-1",
                    Name = "Toggle Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
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
        await Page.WaitForTimeoutAsync(1000);

        // Act: toggle the template's enabled state
        var toggleSwitch = Page.Locator(".toggle-switch input[type='checkbox']").First;
        await toggleSwitch.ClickAsync();
        await Page.WaitForTimeoutAsync(500);

        // Assert: "next cycle" indicator appears
        var nextCycleText = Page.Locator("text=next cycle");
        var nextCycleCount = await nextCycleText.CountAsync();
        Assert.True(nextCycleCount > 0, "Toggling a template during active loop should show 'next cycle' indicator");

        // Cleanup: stop the loop
        var stopBtn = Page.Locator("button:has-text('Stop Loop')");
        if (await stopBtn.CountAsync() > 0)
            await stopBtn.First.ClickAsync();
    }
}
