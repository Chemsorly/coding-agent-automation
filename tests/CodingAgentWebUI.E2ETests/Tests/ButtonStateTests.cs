using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Tests that validate UI button states and interactivity.
/// Addresses the "I press a button and nothing happens" issue by verifying:
/// - Buttons are correctly disabled/enabled based on state
/// - Buttons respond to clicks after Blazor circuit is established
/// - Double-click protection works
/// - Drawer pagination boundaries are respected
/// </summary>
[Trait("Category", "E2E")]
public sealed class ButtonStateTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public ButtonStateTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task BrowseIssues_DisabledWhenNoTemplateSelected()
    {
        // Arrange: seed a template so the dropdown has options
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Act: navigate without selecting a template
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Assert: Browse Issues button exists but is disabled
        var btn = Page.Locator("[data-testid='browse-issues-btn']");
        var isDisabled = await btn.IsDisabledAsync();
        Assert.True(isDisabled, "Browse Issues button should be disabled when no template is selected");

        // Verify clicking it does NOT open the drawer
        await btn.ClickAsync(new() { Force = true }); // Force click even though disabled
        // Drawer should not open — assert immediately (force-click on disabled button is a no-op in Blazor)
        var drawerOpen = await Page.Locator(".dispatch-drawer.open").CountAsync();
        Assert.Equal(0, drawerOpen);
    }

    [Fact]
    public async Task BrowseIssues_EnabledAfterTemplateSelected()
    {
        // Arrange: seed a template and an issue
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "1",
            Title = "Test issue",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        // Act: navigate and select template
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");

        // Assert: Browse Issues button is now enabled
        var btn = Page.Locator("[data-testid='browse-issues-btn']");
        var isDisabled = await btn.IsDisabledAsync();
        Assert.False(isDisabled, "Browse Issues button should be enabled after template selection");

        // Clicking it opens the drawer
        await codingPage.ClickBrowseIssuesAsync();
        var drawerOpen = await Page.Locator(".dispatch-drawer.open").CountAsync();
        Assert.Equal(1, drawerOpen);
    }

    [Fact]
    public async Task DispatchButton_NotVisibleUntilIssueSelected()
    {
        // Arrange: seed template and issue
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "10",
            Title = "Test issue for dispatch",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        // Act: navigate, select template, open drawer
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Assert: dispatch button is NOT visible before selecting an issue
        var dispatchBtn = await Page.QuerySelectorAsync("[data-testid='dispatch-issue-btn']");
        Assert.Null(dispatchBtn);

        // Act: select the issue
        await codingPage.SelectIssueAsync("10");

        // Assert: dispatch button IS now visible
        await Page.WaitForSelectorAsync("[data-testid='dispatch-issue-btn']", new() { Timeout = 5_000 });
        var dispatchBtnAfter = await Page.QuerySelectorAsync("[data-testid='dispatch-issue-btn']");
        Assert.NotNull(dispatchBtnAfter);
    }

    [Fact]
    public async Task DispatchButton_DoubleClick_DispatchesOnlyOnce()
    {
        // Arrange: seed template, issue, and connect an agent
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "99",
            Title = "Double-click test issue",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: navigate, select template, open drawer, select issue
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("99");

        // Click the dispatch button and immediately start watching for the success message
        // (the success message auto-clears after 3s, so we must start observing before clicking).
        var successTask = Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 30_000 });

        var dispatchBtn = Page.Locator("[data-testid='dispatch-issue-btn']");
        await dispatchBtn.ClickAsync(new() { Timeout = 10_000 });

        // Attempt a second click. If the button was disabled/removed by Blazor after the first
        // dispatch, Playwright will throw — expected behavior.
        try
        {
            await Page.Locator("[data-testid='dispatch-issue-btn']").ClickAsync(new() { Timeout = 2000 });
        }
        catch
        {
            // Button was detached, removed, or disabled after first click — expected
        }

        // Wait for the success message (observer was started before the click)
        await successTask;

        // Wait for agent to receive the job
        try
        {
            await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            var reg = Fixture.Factory.AgentRegistry;
            var agent = reg.GetByAgentId("fake-agent-1");
            var allAgents = reg.GetAllAgents();
            Assert.Fail(
                $"ButtonState DoubleClick: agent never received job. " +
                $"agentEntry={(agent is null ? "NULL" : $"{agent.Status},job={agent.ActiveJobId ?? "null"},conn={agent.ConnectionId}")}, " +
                $"allAgents=[{string.Join(";", allAgents.Select(a => $"{a.AgentId}={a.Status}"))}], " +
                $"fakeAgentConnected={fakeAgent.IsConnected}, " +
                $"receivedJobIds={fakeAgent.ReceivedJobIds.Count}");
        }

        // Verify no second dispatch arrived
        Assert.True(fakeAgent.ReceivedJobIds.Count == 1,
            $"Expected exactly 1 job, got {fakeAgent.ReceivedJobIds.Count}. " +
            $"Jobs=[{string.Join(",", fakeAgent.ReceivedJobIds)}]");
    }

    [Fact]
    public async Task StartLoop_DisabledWhenNoTemplates()
    {
        // Arrange: ensure no templates exist

        // Act: navigate to the page
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Assert: Start Loop button is disabled
        var startLoopBtn = await Page.WaitForSelectorAsync(
            "button:has-text('Start Loop')",
            new() { Timeout = 5_000 });
        Assert.NotNull(startLoopBtn);
        var isDisabled = await startLoopBtn.IsDisabledAsync();
        Assert.True(isDisabled, "Start Loop button should be disabled when no templates exist");
    }

    [Fact]
    public async Task StartLoop_EnabledWhenPrerequisitesMet()
    {
        // Arrange: seed an enabled template (fixture SeedDefaults provides issue/repo providers)
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Act: navigate to the page
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Assert: Start Loop button is enabled
        var startLoopBtn = await Page.WaitForSelectorAsync(
            "button:has-text('Start Loop')",
            new() { Timeout = 5_000 });
        Assert.NotNull(startLoopBtn);
        var isDisabled = await startLoopBtn.IsDisabledAsync();
        Assert.False(isDisabled, "Start Loop button should be enabled when prerequisites are met");
    }

    [Fact]
    public async Task StartLoop_ShowsTooltipWhenDisabled()
    {
        // TODO: Add E2E tests for "No issue provider configured" and "No repository provider configured" tooltip messages
        // to validate all disabled-reason paths at the E2E level.

        // Arrange: ensure no templates exist (button should be disabled)

        // Act: navigate to the page
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Assert: Start Loop button has a title tooltip explaining why it's disabled
        var startLoopBtn = await Page.WaitForSelectorAsync(
            "button:has-text('Start Loop')",
            new() { Timeout = 5_000 });
        Assert.NotNull(startLoopBtn);
        var title = await startLoopBtn.GetAttributeAsync("title");
        Assert.NotNull(title);
        Assert.Contains("No enabled pipeline templates configured", title);
    }

    [Fact]
    public async Task DrawerPagination_PrevDisabledOnFirstPage()
    {
        // Arrange: seed template and a few issues (less than page size)
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        for (var i = 1; i <= 3; i++)
        {
            Fixture.IssueProvider.Issues.Add(new IssueDetail
            {
                Identifier = i.ToString(),
                Title = $"Issue {i}",
                Description = "Test",
                Labels = new[] { "enhancement" }
            });
        }

        // Act: navigate, select template, open drawer
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Assert: Prev button is disabled on first page (if pagination is shown)
        var prevBtn = await Page.QuerySelectorAsync("button:has-text('← Prev')");
        if (prevBtn != null)
        {
            var isDisabled = await prevBtn.IsDisabledAsync();
            Assert.True(isDisabled, "Prev button should be disabled on the first page");
        }
        // If pagination isn't shown at all (fewer issues than page size), that's also correct
    }

    [Fact]
    public async Task DrawerPagination_NextDisabledWhenNoMoreIssues()
    {
        // Arrange: seed template and exactly 3 issues (well under page size of 25)
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        for (var i = 1; i <= 3; i++)
        {
            Fixture.IssueProvider.Issues.Add(new IssueDetail
            {
                Identifier = i.ToString(),
                Title = $"Issue {i}",
                Description = "Test",
                Labels = new[] { "enhancement" }
            });
        }

        // Act: navigate, select template, open drawer
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Assert: Next button is disabled when there are no more issues
        var nextBtn = await Page.QuerySelectorAsync("button:has-text('Next →')");
        if (nextBtn != null)
        {
            var isDisabled = await nextBtn.IsDisabledAsync();
            Assert.True(isDisabled, "Next button should be disabled when there are no more issues");
        }
        // If pagination isn't shown at all, that's also correct behavior
    }

    [Fact]
    public async Task DisabledTemplate_NotShownInManualDispatchDropdown()
    {
        // Arrange: seed one enabled and one disabled template
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-enabled",
            Name = "Enabled Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-disabled",
            Name = "Disabled Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = false
        }, CancellationToken.None);

        // Act: navigate
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Assert: enabled template is in dropdown, disabled is not
        var hasEnabled = await Page.EvaluateAsync<bool>(
            @"() => {
                const select = document.querySelector('[data-testid=""template-select""]');
                if (!select) return false;
                return Array.from(select.options).some(o => o.text === 'Enabled Template');
            }");
        Assert.True(hasEnabled, "Enabled template should appear in the dropdown");

        var hasDisabled = await Page.EvaluateAsync<bool>(
            @"() => {
                const select = document.querySelector('[data-testid=""template-select""]');
                if (!select) return false;
                return Array.from(select.options).some(o => o.text === 'Disabled Template');
            }");
        Assert.False(hasDisabled, "Disabled template should NOT appear in the Manual Dispatch dropdown");
    }
}
