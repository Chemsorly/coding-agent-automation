using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Quality gate retry E2E tests: validates that the system correctly handles
/// retry scenarios where quality gates fail and the agent retries or exhausts retries.
/// Uses the multi-agent dispatch path (JobDispatcher → FakeAgentClient → ReportJobCompleted).
/// </summary>
[Trait("Category", "E2E")]
public sealed class QualityGateRetryTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public QualityGateRetryTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task QualityGateRetry_PassesOnSecondAttempt_ShowsCompleted()
    {
        // Arrange: seed test data
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "42",
            Title = "Add input validation",
            Description = "## Requirements\nAdd null checks to all public methods\n\n## Acceptance Criteria\n- [ ] All public methods validate inputs",
            Labels = new[] { "enhancement", "agent:next" }
        });

        // Add a pipeline job template
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

        // Add an agent profile that matches the fake agent's labels
        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Connect a fake agent
        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: navigate and dispatch
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        await codingPage.SelectTemplateAsync("E2E Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("42");
        await codingPage.ClickStartPipelineAsync();

        // Assert: verify dispatch success message appears in the UI
        await Page.WaitForSelectorAsync(
            ".settings-status.status-success",
            new() { Timeout = 10_000 });
        var successText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.Contains("Dispatched #42", successText);

        // Wait for the agent to receive the job assignment
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(assignment);
        Assert.Equal("42", assignment.IssueIdentifier);

        // Agent completes with retry count = 1 (passed on second attempt)
        await fakeAgent.AcceptAndCompleteJobWithPayloadAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/2",
            RetryCount = 1,
            IsDraftPr = false,
            FilesChangedCount = 5,
            LinesAdded = 80,
            LinesRemoved = 20,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>(),
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 0,
            CodeReviewSuggestionCount = 0
        });

        // Assert: verify the run was completed in history
        var completedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "42");
        Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);
        Assert.Equal(1, completedRun.RetryCount);
        Assert.Equal("https://github.com/e2e-org/e2e-repo/pull/2", completedRun.PullRequestUrl);
    }

    [Fact]
    public async Task QualityGateRetry_ExhaustsRetries_ShowsFailedWithDraftPr()
    {
        // Arrange: seed test data
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "42",
            Title = "Add input validation",
            Description = "## Requirements\nAdd null checks to all public methods\n\n## Acceptance Criteria\n- [ ] All public methods validate inputs",
            Labels = new[] { "enhancement", "agent:next" }
        });

        // Add a pipeline job template
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

        // Add an agent profile that matches the fake agent's labels
        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Connect a fake agent
        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: navigate and dispatch
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        await codingPage.SelectTemplateAsync("E2E Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("42");
        await codingPage.ClickStartPipelineAsync();

        // Assert: verify dispatch success message appears in the UI
        await Page.WaitForSelectorAsync(
            ".settings-status.status-success",
            new() { Timeout = 10_000 });
        var successText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.Contains("Dispatched #42", successText);

        // Wait for the agent to receive the job assignment
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(assignment);
        Assert.Equal("42", assignment.IssueIdentifier);

        // Agent completes with failure after exhausting retries
        await fakeAgent.AcceptAndCompleteJobWithPayloadAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "Quality gates failed after max retries; draft PR created.",
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/3",
            IsDraftPr = true,
            RetryCount = 3,
            FilesChangedCount = 4,
            LinesAdded = 60,
            LinesRemoved = 15,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>(),
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 0,
            CodeReviewSuggestionCount = 0
        });

        // Assert: verify the run was recorded as failed in history
        var failedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "42");
        Assert.Equal(PipelineStep.Failed, failedRun.FinalStep);
        Assert.Equal(3, failedRun.RetryCount);
        Assert.Equal("https://github.com/e2e-org/e2e-repo/pull/3", failedRun.PullRequestUrl);
    }
}
