using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Tests that validate dispatch edge cases where the operation cannot proceed.
/// Ensures the UI provides clear feedback instead of silently failing.
/// </summary>
[Trait("Category", "E2E")]
public sealed class DispatchEdgeCaseTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public DispatchEdgeCaseTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Dispatch_NoAgentsAvailable_ShowsErrorMessage()
    {
        // Arrange: seed template and issue, but do NOT connect any agent
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
            Identifier = "50",
            Title = "No agent available issue",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        // Act: navigate and attempt dispatch without any agents connected
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("50");
        await codingPage.ClickStartPipelineAsync();

        // Assert: error message appears (not silent failure)
        await Page.WaitForSelectorAsync(".settings-status.status-error", new() { Timeout = 10_000 });
        var errorVisible = await Page.Locator(".settings-status.status-error").CountAsync();
        Assert.True(errorVisible > 0, "Expected an error message when no agents are available for dispatch");

        var errorText = await Page.TextContentAsync(".settings-status.status-error");
        Assert.NotNull(errorText);
        // The error should mention inability to dispatch
        Assert.True(
            errorText.Contains("Could not dispatch", StringComparison.OrdinalIgnoreCase) ||
            errorText.Contains("no agents", StringComparison.OrdinalIgnoreCase) ||
            errorText.Contains("already being processed", StringComparison.OrdinalIgnoreCase),
            $"Error message should explain why dispatch failed. Got: '{errorText}'");
    }

    [Fact]
    public async Task Dispatch_IssueAlreadyProcessing_ShowsDispatchedBadge()
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
            Identifier = "51",
            Title = "Already processing issue",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("51");
        await codingPage.ClickStartPipelineAsync();

        // Wait for dispatch success
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });

        // Wait for agent to receive the job (don't complete it — keep it active)
        await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act: close drawer and try to dispatch the same issue again
        // Navigate fresh to reset drawer state
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Assert: the issue row should show "DISPATCHED" badge and be non-interactive
        var issueRow = Page.Locator("[data-testid='issue-row-51']");
        var hasDispatchedBadge = await issueRow.Locator("text=DISPATCHED").CountAsync();
        Assert.True(hasDispatchedBadge > 0, "Issue already being processed should show DISPATCHED badge");

        // The row should have reduced opacity (pointer-events: none)
        var opacity = await issueRow.EvaluateAsync<string>("el => getComputedStyle(el).opacity");
        Assert.NotEqual("1", opacity); // Should be 0.6 per the component
    }

    [Fact]
    public async Task Dispatch_IssueProviderFails_ShowsErrorMessage()
    {
        // Arrange: seed template but configure the issue provider to fail
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

        // Configure the issue provider to fail when listing issues
        Fixture.IssueProvider.ShouldFail = true;

        // Act: navigate and try to browse issues
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");

        // Click browse issues — this should trigger the provider failure
        await Page.WaitForFunctionAsync(
            @"() => {
                const btn = document.querySelector('[data-testid=""browse-issues-btn""]');
                return btn && !btn.disabled;
            }",
            null,
            new() { Timeout = 10_000 });
        await Page.ClickAsync("[data-testid='browse-issues-btn']");

        // Wait for the error to appear
        await Page.WaitForSelectorAsync(".settings-status.status-error", new() { Timeout = 10_000 });

        // Assert: error message is shown
        var errorVisible = await Page.Locator(".settings-status.status-error").CountAsync();
        Assert.True(errorVisible > 0, "Expected an error message when issue provider fails");
    }
}
