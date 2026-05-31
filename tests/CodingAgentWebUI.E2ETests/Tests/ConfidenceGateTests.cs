using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Confidence gate E2E tests: validates that the system correctly handles
/// analysis-phase rejections where the confidence gate determines an issue
/// is not ready or should not be worked on.
/// Uses the multi-agent dispatch path (JobDispatcher → FakeAgentClient → ReportJobCompleted).
/// </summary>
[Trait("Category", "E2E")]
public sealed class ConfidenceGateTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public ConfidenceGateTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ConfidenceGate_NotReady_ShowsFailedWithRecommendation()
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

        // Wait for agent to be registered in the registry
        await Task.Delay(500);

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

        // Agent reports failure with "not_ready" analysis recommendation
        await fakeAgent.AcceptAndCompleteJobWithPayloadAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "Analysis determined issue needs refinement",
            PullRequestUrl = null,
            RetryCount = 0,
            IsDraftPr = false,
            FilesChangedCount = 0,
            LinesAdded = 0,
            LinesRemoved = 0,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.NotReady,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>(),
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 0,
            CodeReviewSuggestionCount = 0
        });

        // Allow time for hub processing
        await Task.Delay(500);

        // Assert: verify the run was recorded as failed with not_ready recommendation
        var history = Fixture.Factory.HistoryService;
        var runs = history.GetRunHistory();
        Assert.True(runs.Count > 0, "Expected at least one run in history");

        var failedRun = runs.FirstOrDefault(r => r.IssueIdentifier == "42");
        Assert.NotNull(failedRun);
        Assert.Equal(PipelineStep.Failed, failedRun.FinalStep);
        Assert.Equal(AnalysisGateResult.NotReady, failedRun.AnalysisRecommendation);
        Assert.Equal(0, failedRun.RetryCount);
        Assert.Null(failedRun.PullRequestUrl);
    }

    [Fact]
    public async Task ConfidenceGate_WontDo_ShowsFailedWithRecommendation()
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

        // Wait for agent to be registered in the registry
        await Task.Delay(500);

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

        // Agent reports failure with "wont_do" analysis recommendation
        await fakeAgent.AcceptAndCompleteJobWithPayloadAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "Analysis determined issue is out of scope",
            PullRequestUrl = null,
            RetryCount = 0,
            IsDraftPr = false,
            FilesChangedCount = 0,
            LinesAdded = 0,
            LinesRemoved = 0,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.WontDo,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>(),
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 0,
            CodeReviewSuggestionCount = 0
        });

        // Allow time for hub processing
        await Task.Delay(500);

        // Assert: verify the run was recorded as failed with wont_do recommendation
        var history = Fixture.Factory.HistoryService;
        var runs = history.GetRunHistory();
        Assert.True(runs.Count > 0, "Expected at least one run in history");

        var failedRun = runs.FirstOrDefault(r => r.IssueIdentifier == "42");
        Assert.NotNull(failedRun);
        Assert.Equal(PipelineStep.Failed, failedRun.FinalStep);
        Assert.Equal(AnalysisGateResult.WontDo, failedRun.AnalysisRecommendation);
        Assert.Equal(0, failedRun.RetryCount);
        Assert.Null(failedRun.PullRequestUrl);
    }
}
