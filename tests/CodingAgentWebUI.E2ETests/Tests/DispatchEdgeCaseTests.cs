using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Tests that validate dispatch edge cases where the operation cannot proceed.
/// Ensures the UI provides clear feedback instead of silently failing.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class DispatchEdgeCaseTests : E2ETestBase
{
    public DispatchEdgeCaseTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Dispatching with nothing connected queues the work rather than refusing it.
    ///
    /// <para>
    /// This test used to assert the opposite — a red "no agents are currently connected" banner —
    /// which was correct while the in-memory distributor pushed a job straight to a connected
    /// agent. Since Spec 041 there is nothing to be connected in advance: the dispatch writes a
    /// work item and the Job Controller starts a pod for it, so
    /// <c>IWorkDistributor.RequiresConnectedAgents</c> is <c>false</c> and the guard that produced
    /// that banner never fires. Asserting the banner made this a test of a dead branch; asserting
    /// the queue makes it a test of what an operator with an empty cluster actually gets.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Dispatch_NoAgentsAvailable_QueuesForTheJobController()
    {
        // Arrange: seed template and issue, but do NOT connect any agent
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
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

        // Assert: the operator is told the work is queued, not that it was refused.
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });

        var statusText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.NotNull(statusText);
        Assert.Contains("Queued", statusText, StringComparison.OrdinalIgnoreCase);

        // And the work item really is waiting for a pod: nothing has claimed it, because nothing
        // is connected. This is the half of the assertion the old error-banner check never made.
        var pending = await Fixture.WorkItems.GetPendingAsync(50, ct: CancellationToken.None);
        Assert.Contains(pending, w => w.IssueIdentifier == "50");
    }

    [Fact]
    public async Task Dispatch_IssueAlreadyProcessing_ShowsDispatchedBadge()
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
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

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
        await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Act: close drawer and try to dispatch the same issue again
        // Navigate fresh to reset drawer state
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Assert: the issue row should show "⏳ Queued" badge and be non-interactive
        var issueRow = Page.Locator("[data-testid='issue-row-51']");
        // TODO: Tighten selector — "text=Queued" matches any element containing "Queued"; use a more specific locator for the badge text.
        var hasDispatchedBadge = await issueRow.Locator("text=Queued").CountAsync();
        Assert.True(hasDispatchedBadge > 0, "Issue already being processed should show Queued badge");

        // The row should have reduced opacity (pointer-events: none)
        var opacity = await issueRow.EvaluateAsync<string>("el => getComputedStyle(el).opacity");
        Assert.NotEqual("1", opacity); // Should be 0.6 per the component
    }

    [Fact]
    public async Task Dispatch_IssueProviderFails_ShowsErrorMessage()
    {
        // Arrange: seed template but configure the issue provider to fail
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
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
