using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// E2E tests for the PR Review Pipeline: dispatch → agent receives review job → completion.
/// Mirrors the pattern used by HappyPathTests and DispatchEdgeCaseTests for the implementation pipeline.
/// </summary>
[Trait("Category", "E2E")]
public sealed class PrReviewPipelineTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public PrReviewPipelineTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task PrReview_HappyPath_CompletesAndRecordsInHistory()
    {
        // Arrange: seed PR and template
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 99,
            Identifier = "99",
            Title = "Fix null reference in handler",
            Description = "Resolves #42",
            Labels = new[] { "agent:next" },
            BranchName = "fix/null-ref",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/99",
            IsDraft = false
        });

        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-1",
                    Name = "E2E Test Template",
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

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: navigate, open PR drawer, select PR, dispatch
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("E2E Test Template");
        await codingPage.ClickBrowsePrsAsync();
        await codingPage.SelectPrAsync("99");
        await codingPage.ClickDispatchPrReviewAsync();

        // Assert: success message
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var successText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.Contains("PR #99 dispatched for review", successText);

        // Wait for agent to receive job
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(assignment);
        Assert.Equal("99", assignment.IssueIdentifier);
        Assert.Equal(PipelineRunType.Review, assignment.RunType);

        // Agent completes the job
        // TODO: AcceptAndCompleteJobAsync reports implementation-style step transitions (CloningRepository →
        // GeneratingCode → Completed). Consider adding a review-specific completion helper that reports
        // review-appropriate steps for more representative test behavior.
        await fakeAgent.AcceptAndCompleteJobAsync(assignment.JobId);

        // Verify history
        var completedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "99");
        Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);
        Assert.Equal(PipelineRunType.Review, completedRun.RunType);

        // Verify label transitions were tracked
        var labelAdds = Fixture.RepositoryProvider.PrLabelChanges
            .Where(c => c.Action == "Add" && c.PrNumber == 99).ToList();
        Assert.Contains(labelAdds, c => c.Label == "agent:in-progress");
    }

    [Fact]
    public async Task PrReview_DuplicateDispatch_ShowsDispatchedBadge()
    {
        // Arrange
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 77,
            Identifier = "77",
            Title = "Add logging middleware",
            Description = "Adds structured logging",
            Labels = new[] { "agent:next" },
            BranchName = "feature/logging",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/77",
            IsDraft = false
        });

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

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: dispatch PR (first time)
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowsePrsAsync();
        await codingPage.SelectPrAsync("77");
        await codingPage.ClickDispatchPrReviewAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });

        // Wait for agent to receive the job (don't complete it — keep it active)
        await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Navigate fresh to re-render the drawer with current state
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowsePrsAsync();

        // Assert: the PR row shows DISPATCHED badge and reduced opacity
        var prRow = Page.Locator("[data-testid='pr-row-77']");
        var hasDispatchedBadge = await prRow.Locator("text=DISPATCHED").CountAsync();
        Assert.True(hasDispatchedBadge > 0, "PR already being processed should show DISPATCHED badge");

        var opacity = await prRow.EvaluateAsync<string>("el => getComputedStyle(el).opacity");
        Assert.NotEqual("1", opacity);
    }

    // TODO: This test only covers the "no agents connected" error. Consider adding a separate test
    // for the duplicate guard scenario (dispatch with agent connected, then second dispatch shows
    // "already being processed") to fully cover the three acceptance criteria scenarios independently.
    [Fact]
    public async Task PrReview_NoAgentAvailable_ShowsErrorMessage()
    {
        // Arrange: seed PR and template, do NOT connect any agent
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = 55,
            Identifier = "55",
            Title = "Update dependencies",
            Description = "Bumps all packages",
            Labels = new[] { "agent:next" },
            BranchName = "chore/deps",
            TargetBranch = "main",
            Url = "https://github.com/e2e-org/e2e-repo/pull/55",
            IsDraft = false
        });

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

        // Act: attempt dispatch without any agents connected
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowsePrsAsync();
        await codingPage.SelectPrAsync("55");
        await codingPage.ClickDispatchPrReviewAsync();

        // Assert: error message appears indicating no agents available
        await Page.WaitForSelectorAsync(".settings-status.status-error", new() { Timeout = 10_000 });
        var errorText = await Page.TextContentAsync(".settings-status.status-error");
        Assert.NotNull(errorText);
        Assert.Contains("no agents", errorText, StringComparison.OrdinalIgnoreCase);
    }
}
