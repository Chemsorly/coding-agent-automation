using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Tests that validate agent run progress tracking and step transitions.
/// Verifies that dispatched jobs appear as active runs and that step transitions
/// are correctly reflected in the OrchestratorRunService.
/// Uses the multi-agent dispatch path (JobDispatcher → FakeAgentClient → ReportStepTransition).
/// </summary>
[Trait("Category", "E2E")]
public sealed class AgentRunProgressTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public AgentRunProgressTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task AgentRun_InProgress_ShowsActiveRun()
    {
        // Arrange: seed test data
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "42",
            Title = "Add input validation",
            Description = "## Requirements\nAdd null checks to all public methods\n\n## Acceptance Criteria\n- [ ] All public methods validate inputs",
            Labels = new[] { "enhancement", "agent:next" }
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

        // Wait for dispatch success
        await Page.WaitForSelectorAsync(
            ".settings-status.status-success",
            new() { Timeout = 10_000 });

        // Wait for the agent to receive the job assignment
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(assignment);
        Assert.Equal("42", assignment.IssueIdentifier);

        // Agent accepts the job and reports step transitions with metadata
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.CloningRepository);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.CreatingBranch);

        // Transition to VerifyingBaseline with BranchName metadata (from completed CreatingBranch)
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.VerifyingBaseline,
            new Dictionary<string, string> { ["BranchName"] = "feature/auto-42-add-validation" });

        // Transition to AnalyzingCode with BaselineHealthPassed metadata
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.AnalyzingCode,
            new Dictionary<string, string>
            {
                ["BranchName"] = "feature/auto-42-add-validation",
                ["BaselineHealthPassed"] = "True"
            });

        // Wait briefly for the hub to process the step transitions
        var runService = Fixture.Factory.Services.GetRequiredService<IOrchestratorRunService>();
        await WaitUntilAsync(() =>
        {
            var run = runService.GetActiveRuns().FirstOrDefault(r => r.IssueIdentifier == "42");
            return run is not null && run.CurrentStep == PipelineStep.AnalyzingCode && run.BaselineHealthPassed == true;
        });

        // Assert: the run appears in active runs with metadata applied
        var activeRuns = runService.GetActiveRuns();
        Assert.True(activeRuns.Count > 0, "Expected at least one active run");

        var activeRun = activeRuns.FirstOrDefault(r => r.IssueIdentifier == "42");
        Assert.NotNull(activeRun);
        Assert.Equal(PipelineStep.AnalyzingCode, activeRun.CurrentStep);
        Assert.Equal("feature/auto-42-add-validation", activeRun.BranchName);
        Assert.True(activeRun.BaselineHealthPassed);

        // Report file change stats via metadata when transitioning to ReviewingCode
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.GeneratingCode);
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.ReviewingCode,
            new Dictionary<string, string>
            {
                ["BranchName"] = "feature/auto-42-add-validation",
                ["BaselineHealthPassed"] = "True",
                ["FilesChangedCount"] = "3",
                ["LinesAdded"] = "50",
                ["LinesRemoved"] = "10"
            });

        // Wait for file change stats to propagate
        await WaitUntilAsync(() =>
        {
            var run = runService.GetActiveRuns().FirstOrDefault(r => r.IssueIdentifier == "42");
            return run is not null && run.FilesChangedCount == 3;
        });

        // Assert: file change stats are now available on the active run
        activeRuns = runService.GetActiveRuns();
        activeRun = activeRuns.FirstOrDefault(r => r.IssueIdentifier == "42");
        Assert.NotNull(activeRun);
        Assert.Equal(3, activeRun.FilesChangedCount);
        Assert.Equal(50, activeRun.LinesAdded);
        Assert.Equal(10, activeRun.LinesRemoved);

        // Complete the job
        await fakeAgent.ReportStepAsync(assignment.JobId, PipelineStep.Completed);
        await fakeAgent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/1",
            RetryCount = 0,
            FilesChangedCount = 3,
            LinesAdded = 50,
            LinesRemoved = 10,
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

        // Assert: history shows completed run
        var completedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "42");
        Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);
    }

    [Fact]
    public async Task AgentRun_ReportsMultipleSteps_HistoryReflectsCompletion()
    {
        // Arrange: seed test data
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "42",
            Title = "Add input validation",
            Description = "## Requirements\nAdd null checks to all public methods\n\n## Acceptance Criteria\n- [ ] All public methods validate inputs",
            Labels = new[] { "enhancement", "agent:next" }
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

        // Wait for dispatch success
        await Page.WaitForSelectorAsync(
            ".settings-status.status-success",
            new() { Timeout = 10_000 });

        // Wait for the agent to receive the job assignment
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(assignment);
        Assert.Equal("42", assignment.IssueIdentifier);

        // Agent accepts and reports multiple step transitions with small delays
        await fakeAgent.AcceptJobAsync(assignment.JobId);

        var steps = new[]
        {
            PipelineStep.CloningRepository,
            PipelineStep.CreatingBranch,
            PipelineStep.AnalyzingCode,
            PipelineStep.GeneratingCode,
            PipelineStep.RunningQualityGates,
            PipelineStep.CreatingPullRequest,
            PipelineStep.Completed
        };

        foreach (var step in steps)
        {
            await fakeAgent.ReportStepAsync(assignment.JobId, step);
        }

        // Report completion
        await fakeAgent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/1",
            RetryCount = 0,
            FilesChangedCount = 5,
            LinesAdded = 100,
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

        // Assert: history shows completed run
        var completedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "42");
        Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);
    }
}
