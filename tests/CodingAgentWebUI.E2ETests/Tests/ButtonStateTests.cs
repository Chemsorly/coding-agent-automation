using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
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
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-1",
                    Name = "Test Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
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
        await Page.WaitForTimeoutAsync(500);
        var drawerOpen = await Page.Locator(".dispatch-drawer.open").CountAsync();
        Assert.Equal(0, drawerOpen);
    }

    [Fact]
    public async Task BrowseIssues_EnabledAfterTemplateSelected()
    {
        // Arrange: seed a template and an issue
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-1",
                    Name = "Test Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
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
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-1",
                    Name = "Test Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
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
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-1",
                    Name = "Test Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
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

        // Double-click the dispatch button rapidly
        var dispatchBtn = Page.Locator("[data-testid='dispatch-issue-btn']");
        // Use Playwright's built-in auto-wait (retries until element is visible and stable)
        await dispatchBtn.ClickAsync(new() { Timeout = 10_000 });
        // Attempt a second click. If the button was detached/disabled by Blazor after the first
        // dispatch (correct behavior), Playwright will throw — which is fine,
        // it means the UI prevented the double-dispatch at the DOM level.
        try
        {
            await Page.Locator("[data-testid='dispatch-issue-btn']").ClickAsync(new() { Timeout = 2000 });
        }
        catch
        {
            // Button was detached, removed, or disabled after first click — expected
        }

        // Wait for the dispatch to complete and the success message to show
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });

        // Wait for agent to receive the job (with generous timeout for slow ARM runners)
        await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Give extra time for any potential second dispatch to arrive
        await Task.Delay(2000);

        // Assert: only one job was received by the agent
        Assert.Single(fakeAgent.ReceivedJobIds);
    }

    [Fact]
    public async Task StartLoop_DisabledWhenNoTemplates()
    {
        // Arrange: ensure no templates exist
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = Array.Empty<PipelineJobTemplate>()
        }, CancellationToken.None);

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
    public async Task DrawerPagination_PrevDisabledOnFirstPage()
    {
        // Arrange: seed template and a few issues (less than page size)
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-1",
                    Name = "Test Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
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
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-1",
                    Name = "Test Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
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
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-enabled",
                    Name = "Enabled Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                },
                new PipelineJobTemplate
                {
                    Id = "template-disabled",
                    Name = "Disabled Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = false
                }
            }
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
