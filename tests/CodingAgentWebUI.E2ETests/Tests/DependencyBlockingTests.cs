using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Tests that validate issue dependency blocking prevents dispatch and shows clear UI feedback.
/// </summary>
[Trait("Category", "E2E")]
public sealed class DependencyBlockingTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public DependencyBlockingTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Dispatch_BlockedByOpenDependency_ShowsBlockingError()
    {
        // Arrange: seed template and issue with dependency on open issue #100
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
            Identifier = "60",
            Title = "Blocked issue",
            Description = "Blocked by #100",
            Labels = new[] { "enhancement" }
        });

        // ClosedIssueIdentifiers is empty — #100 is open

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Wait for the readiness check to complete and show the blocked badge
        var issueRow = Page.Locator("[data-testid='issue-row-60']");
        await issueRow.Locator(".drawer-badge-blocked").WaitForAsync(new() { Timeout = 10_000 });

        // Assert: blocked badge mentions #100
        var badgeText = await issueRow.Locator(".drawer-badge-blocked").TextContentAsync();
        Assert.NotNull(badgeText);
        Assert.Contains("#100", badgeText);

        // Select the issue and verify dispatch button is disabled
        await codingPage.SelectIssueAsync("60");
        var dispatchBtn = Page.Locator("[data-testid='dispatch-issue-btn']");
        var isDisabled = await dispatchBtn.IsDisabledAsync();
        Assert.True(isDisabled, "Dispatch button should be disabled for blocked issues");
    }

    [Fact]
    public async Task Dispatch_DependencyResolved_DispatchesSuccessfully()
    {
        // Arrange: seed template and issue with dependency on #100, but #100 is closed
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
            Identifier = "61",
            Title = "Resolved dependency issue",
            Description = "Blocked by #100",
            Labels = new[] { "enhancement" }
        });

        // Mark #100 as closed — dependency is satisfied
        Fixture.IssueProvider.ClosedIssueIdentifiers.Add("100");

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("61");
        await codingPage.ClickStartPipelineAsync();

        // Assert: dispatch succeeds
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var successText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.NotNull(successText);
        Assert.Contains("#61", successText);
    }

    [Fact]
    public async Task Dispatch_PartiallyBlocked_ShowsOpenDependency()
    {
        // Arrange: issue depends on #100 (closed) and #200 (open)
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
            Identifier = "62",
            Title = "Partially blocked issue",
            Description = "Depends on #100\nBlocked by #200",
            Labels = new[] { "enhancement" }
        });

        // Only #100 is closed, #200 is still open
        Fixture.IssueProvider.ClosedIssueIdentifiers.Add("100");

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Wait for the readiness check to complete and show the blocked badge
        var issueRow = Page.Locator("[data-testid='issue-row-62']");
        await issueRow.Locator(".drawer-badge-blocked").WaitForAsync(new() { Timeout = 10_000 });

        // Assert: blocked badge mentions #200 but not #100 (which is closed)
        var badgeText = await issueRow.Locator(".drawer-badge-blocked").TextContentAsync();
        Assert.NotNull(badgeText);
        Assert.Contains("#200", badgeText);
        Assert.DoesNotContain("#100", badgeText);

        // Select the issue and verify dispatch button is disabled
        await codingPage.SelectIssueAsync("62");
        var dispatchBtn = Page.Locator("[data-testid='dispatch-issue-btn']");
        var isDisabled = await dispatchBtn.IsDisabledAsync();
        Assert.True(isDisabled, "Dispatch button should be disabled for partially blocked issues");
    }
}
