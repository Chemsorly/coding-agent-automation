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
[Collection(E2ECollection.Name)]
public sealed class PrReviewPipelineTests : E2ETestBase
{
    public PrReviewPipelineTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Seeds the issue side of a pull request.
    ///
    /// <para>
    /// Dispatch preparation fetches issue context for whatever identifier it is dispatching —
    /// including a review, where the identifier is the PR number. On GitHub that resolves, because
    /// every pull request is also an issue under the same number; the harness models the two as
    /// separate fakes, so a PR seeded only in the repository provider makes
    /// <c>GetIssueAsync</c> throw <c>KeyNotFoundException</c>, orchestration return null, and the
    /// UI report "Could not dispatch — orchestration preparation failed". Seeding both sides is
    /// what the real provider looks like.
    /// </para>
    /// </summary>
    private void SeedPrAsIssue(string identifier, string title, string description) =>
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = identifier,
            Title = title,
            Description = description,
            Labels = new[] { "agent:next" }
        });

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
        SeedPrAsIssue("99", "Fix null reference in handler", "Resolves #42");

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = "E2E Test Template",
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

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: navigate, open PR drawer, select PR, dispatch
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("E2E Test Template");
        await codingPage.ClickBrowsePrsAsync();
        await codingPage.SelectPrAsync("99");
        await codingPage.ClickDispatchPrReviewAsync();

        // Assert: success message. Queued, not dispatched — KubernetesWorkDistributor reports
        // Queued unconditionally, because the work item is written and a pod is started for it
        // afterwards. "dispatched for review" is copy from the SignalR distributor.
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var successText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.Contains("Queued PR #99 for review", successText);

        // Wait for agent to receive job
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(assignment);
        Assert.Equal("99", assignment.IssueIdentifier);
        Assert.Equal(PipelineRunType.Review, assignment.RunType);

        // Agent completes the job
        // TODO: AcceptAndCompleteJobAsync reports implementation-style step transitions (CloningRepository →
        // GeneratingCode → Completed). Consider adding a review-specific completion helper that reports
        // review-appropriate steps for more representative test behavior.
        Exception? completionEx = null;
        try
        {
            await fakeAgent.AcceptAndCompleteJobAsync(assignment.JobId);
        }
        catch (Exception ex)
        {
            completionEx = ex;
        }

        // Verify history (with diagnostic on failure)
        try
        {
            var completedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "99");
            Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);
            Assert.Equal(PipelineRunType.Review, completedRun.RunType);
        }
        catch (TimeoutException)
        {
            var reg = Fixture.AgentRegistry;
            var agent = reg.GetByAgentId("fake-agent-1");
            var history = (await Fixture.Factory.HistoryService.GetRunHistoryAsync());
            Assert.Fail(
                $"PrReview WaitForHistoryAsync timed out. " +
                $"completionEx={completionEx?.GetType().Name}: {completionEx?.Message ?? "none"}, " +
                $"agentStatus={(agent is null ? "NULL" : $"{agent.Status}, job={agent.ActiveJobId ?? "null"}")}, " +
                $"historyCount={history.Count}, " +
                $"historyIssues=[{string.Join(",", history.Select(r => r.IssueIdentifier))}], " +
                $"fakeAgentConnected={fakeAgent.IsConnected}");
        }

        // Verify label transitions were tracked
        var labelAdds = Fixture.RepositoryProvider.PrLabelChanges
            .Where(c => c.Action == "Add" && c.PrNumber == 99).ToList();
        Assert.Contains(labelAdds, c => c.Label == "agent:in-progress");
    }

    [Fact]
    public async Task PrReview_DuplicateDispatch_ShowsQueuedBadge()
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
        SeedPrAsIssue("77", "Add logging middleware", "Adds structured logging");

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

        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: dispatch PR (first time)
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowsePrsAsync();
        await codingPage.SelectPrAsync("77");
        await codingPage.ClickDispatchPrReviewAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });

        // Wait for agent to receive the job (don't complete it — keep it active)
        await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Navigate fresh to re-render the drawer with current state
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowsePrsAsync();

        // Assert: the PR row shows the Queued badge and reduced opacity. "DISPATCHED" was the
        // wording while dispatch pushed to a connected agent; the PR drawer now says "Queued" like
        // the issue drawer, because a pod has yet to be started.
        var prRow = Page.Locator("[data-testid='pr-row-77']");
        var hasQueuedBadge = await prRow.Locator("text=Queued").CountAsync();
        Assert.True(hasQueuedBadge > 0, "PR already being processed should show the Queued badge");

        var opacity = await prRow.EvaluateAsync<string>("el => getComputedStyle(el).opacity");
        Assert.NotEqual("1", opacity);
    }

    /// <summary>
    /// A review dispatched with nothing connected is queued for the Job Controller, not refused.
    ///
    /// <para>
    /// Was <c>PrReview_NoAgentAvailable_ShowsErrorMessage</c>, asserting a red "no agents are
    /// currently connected" banner. That guard is behind
    /// <c>IWorkDistributor.RequiresConnectedAgents</c>, which has been <c>false</c> since Spec 041
    /// removed the distributor that pushed work to a connected agent — so the branch is dead and
    /// the banner unreachable. See <c>DispatchEdgeCaseTests</c> for the same change on the
    /// implementation path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PrReview_NoAgentAvailable_QueuesForTheJobController()
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
        SeedPrAsIssue("55", "Update dependencies", "Bumps all packages");

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

        // Act: attempt dispatch without any agents connected
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Test Template");
        await codingPage.ClickBrowsePrsAsync();
        await codingPage.SelectPrAsync("55");
        await codingPage.ClickDispatchPrReviewAsync();

        // Assert: the review is queued, and the work item is sitting unclaimed because there is
        // no agent to claim it.
        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var statusText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.NotNull(statusText);
        Assert.Contains("Queued PR #55 for review", statusText);

        var pending = await Fixture.WorkItems.GetPendingAsync(50, CancellationToken.None);
        Assert.Contains(pending, w => w.IssueIdentifier == "55");
    }
}
