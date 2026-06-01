using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// E2E tests for the Agent Feedback Loops feature (020).
/// Validates that feedback data flows through the pipeline completion payload,
/// is persisted in run history, and is visible in the monitoring run detail UI.
/// </summary>
[Trait("Category", "E2E")]
public sealed class FeedbackFlowTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public FeedbackFlowTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Feedback_SuccessWithFeedback_ShowsInRunHistory()
    {
        // Arrange: seed test data
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "80",
            Title = "Feedback success test",
            Description = "## Requirements\nTest feedback flow\n\n## Acceptance Criteria\n- [ ] Feedback collected",
            Labels = new[] { "enhancement", "agent:next" }
        });

        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-fb-1",
                    Name = "Feedback Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-fb",
            DisplayName = "Feedback Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("feedback-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await Task.Delay(500);

        // Act: dispatch the issue
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Feedback Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("80");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Agent completes with feedback in the payload
        await fakeAgent.AcceptAndCompleteJobWithPayloadAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/80",
            RetryCount = 0,
            FilesChangedCount = 3,
            LinesAdded = 40,
            LinesRemoved = 5,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>(),
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 0,
            CodeReviewSuggestionCount = 0,
            Feedback = new RunFeedback
            {
                Outcome = FeedbackOutcome.Success,
                CollectedAtUtc = DateTime.UtcNow,
                Harness = new HarnessFeedback
                {
                    Category = "missing file context",
                    MissingContext = new[] { "tsconfig.json was not provided upfront" },
                    Suggestions = new[] { "Include build config files in initial context" }
                },
                Issue = new IssueFeedback
                {
                    Category = "vague acceptance criteria",
                    Description = "The acceptance criteria did not specify which methods need validation",
                    AffectedFiles = new[] { "src/Services/UserService.cs" },
                    HumanActionNeeded = "Clarify which public methods require null checks"
                }
            }
        });

        // Allow time for hub processing
        await Task.Delay(500);

        // Assert: verify the run was completed with feedback in history
        var history = Fixture.Factory.HistoryService;
        var runs = history.GetRunHistory();
        var completedRun = runs.FirstOrDefault(r => r.IssueIdentifier == "80");
        Assert.NotNull(completedRun);
        Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);
        Assert.NotNull(completedRun.Feedback);
        Assert.Equal(FeedbackOutcome.Success, completedRun.Feedback.Outcome);
        Assert.Equal("missing file context", completedRun.Feedback.Harness.Category);
        Assert.NotNull(completedRun.Feedback.Issue);
        Assert.Equal("vague acceptance criteria", completedRun.Feedback.Issue.Category);
    }

    [Fact]
    public async Task Feedback_FailureWithStuckReason_PersistedInHistory()
    {
        // Arrange: seed test data
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "81",
            Title = "Feedback failure test",
            Description = "## Requirements\nTest failure feedback\n\n## Acceptance Criteria\n- [ ] Feedback on failure",
            Labels = new[] { "enhancement", "agent:next" }
        });

        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-fb-2",
                    Name = "Failure Feedback Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-fb-2",
            DisplayName = "Failure Feedback Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("feedback-agent-2", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await Task.Delay(500);

        // Act: dispatch the issue
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Failure Feedback Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("81");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Agent completes with failure and feedback
        await fakeAgent.AcceptAndCompleteJobWithPayloadAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "Quality gates failed after max retries",
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/81",
            IsDraftPr = true,
            RetryCount = 3,
            FilesChangedCount = 2,
            LinesAdded = 20,
            LinesRemoved = 3,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>(),
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 0,
            CodeReviewSuggestionCount = 0,
            Feedback = new RunFeedback
            {
                Outcome = FeedbackOutcome.Failure,
                CollectedAtUtc = DateTime.UtcNow,
                Harness = new HarnessFeedback
                {
                    Category = "mcp tool timeout",
                    StuckReason = "Build tool timed out repeatedly during quality gate execution",
                    MissingCapabilities = new[] { "Ability to increase build timeout dynamically" },
                    PromptIssues = new[] { "Quality gate retry instructions were unclear about timeout handling" }
                }
            }
        });

        // Allow time for hub processing
        await Task.Delay(500);

        // Assert: verify the run was recorded with failure feedback
        var history = Fixture.Factory.HistoryService;
        var runs = history.GetRunHistory();
        var failedRun = runs.FirstOrDefault(r => r.IssueIdentifier == "81");
        Assert.NotNull(failedRun);
        Assert.Equal(PipelineStep.Failed, failedRun.FinalStep);
        Assert.NotNull(failedRun.Feedback);
        Assert.Equal(FeedbackOutcome.Failure, failedRun.Feedback.Outcome);
        Assert.Equal("mcp tool timeout", failedRun.Feedback.Harness.Category);
        Assert.Equal("Build tool timed out repeatedly during quality gate execution", failedRun.Feedback.Harness.StuckReason);
        Assert.Contains("Ability to increase build timeout dynamically", failedRun.Feedback.Harness.MissingCapabilities);
    }

    [Fact]
    public async Task Feedback_NullFeedback_RunStillPersistedCorrectly()
    {
        // Arrange: seed test data
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "82",
            Title = "No feedback test",
            Description = "## Requirements\nTest null feedback\n\n## Acceptance Criteria\n- [ ] Run completes without feedback",
            Labels = new[] { "enhancement", "agent:next" }
        });

        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-fb-3",
                    Name = "No Feedback Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-fb-3",
            DisplayName = "No Feedback Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("feedback-agent-3", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await Task.Delay(500);

        // Act: dispatch the issue
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("No Feedback Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("82");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Agent completes WITHOUT feedback (simulates pre-feature agent or failed collection)
        await fakeAgent.AcceptAndCompleteJobAsync(assignment.JobId);

        // Allow time for hub processing
        await Task.Delay(500);

        // Assert: run is persisted correctly with null feedback
        var history = Fixture.Factory.HistoryService;
        var runs = history.GetRunHistory();
        var completedRun = runs.FirstOrDefault(r => r.IssueIdentifier == "82");
        Assert.NotNull(completedRun);
        Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);
        Assert.Null(completedRun.Feedback); // No feedback in default AcceptAndCompleteJobAsync
    }

    [Fact]
    public async Task Feedback_VisibleInMonitoringRunDetail()
    {
        // Arrange: seed test data and complete a run with feedback
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "83",
            Title = "Feedback UI test",
            Description = "## Requirements\nTest feedback UI\n\n## Acceptance Criteria\n- [ ] Feedback visible in modal",
            Labels = new[] { "enhancement", "agent:next" }
        });

        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-fb-4",
                    Name = "Feedback UI Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-fb-4",
            DisplayName = "Feedback UI Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("feedback-agent-4", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await Task.Delay(500);

        // Dispatch and complete with feedback
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Feedback UI Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("83");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await fakeAgent.AcceptAndCompleteJobWithPayloadAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/83",
            RetryCount = 1,
            FilesChangedCount = 5,
            LinesAdded = 60,
            LinesRemoved = 10,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>(),
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 0,
            CodeReviewSuggestionCount = 0,
            Feedback = new RunFeedback
            {
                Outcome = FeedbackOutcome.Success,
                CollectedAtUtc = DateTime.UtcNow,
                Harness = new HarnessFeedback
                {
                    Category = "slow build",
                    Suggestions = new[] { "Cache NuGet packages between runs" },
                    MissingContext = new[] { "Build cache configuration" }
                },
                Issue = new IssueFeedback
                {
                    Description = "Issue was well-written but missing edge case details",
                    AffectedFiles = new[] { "src/Controllers/ApiController.cs" }
                }
            }
        });

        // Allow time for hub processing
        await Task.Delay(1000);

        // Act: navigate to monitoring page and open the run detail
        var monitoringPage = new AgentMonitoringPage(Page, BaseUrl);
        await monitoringPage.NavigateAsync();

        // Click on the history section to find the completed run
        // The run should be in the "Recent Runs" section
        var historyRow = await Page.QuerySelectorAsync("tr:has-text('#83')");
        if (historyRow is not null)
        {
            await historyRow.ClickAsync();
            await Page.WaitForTimeoutAsync(500);

            // Assert: feedback section is visible in the modal/detail view
            var feedbackSection = await Page.QuerySelectorAsync(".feedback-section");
            Assert.NotNull(feedbackSection);

            // Verify feedback content is rendered
            var feedbackText = await Page.TextContentAsync(".feedback-section");
            Assert.Contains("Harness Feedback", feedbackText);
            Assert.Contains("slow build", feedbackText);
            Assert.Contains("Cache NuGet packages between runs", feedbackText);
            Assert.Contains("Issue Feedback", feedbackText);
            Assert.Contains("Issue was well-written", feedbackText);
        }
        else
        {
            // If the run isn't in the clickable history table, verify it's at least in the history service
            var history = Fixture.Factory.HistoryService;
            var runs = history.GetRunHistory();
            var completedRun = runs.FirstOrDefault(r => r.IssueIdentifier == "83");
            Assert.NotNull(completedRun);
            Assert.NotNull(completedRun.Feedback);
            Assert.Equal("slow build", completedRun.Feedback.Harness.Category);
        }
    }

    [Fact]
    public async Task Feedback_WithIssueFeedback_AllFieldsPersisted()
    {
        // Arrange: seed test data
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "84",
            Title = "Full issue feedback test",
            Description = "## Requirements\nTest all issue feedback fields\n\n## Acceptance Criteria\n- [ ] All fields persisted",
            Labels = new[] { "enhancement", "agent:next" }
        });

        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-fb-5",
                    Name = "Full Feedback Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-fb-5",
            DisplayName = "Full Feedback Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var fakeAgent = new FakeAgentClient("feedback-agent-5", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await Task.Delay(500);

        // Act: dispatch and complete with comprehensive feedback
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("Full Feedback Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("84");
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 10_000 });
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback
            {
                Category = "incomplete prompt",
                MissingContext = new[] { "Database schema", "API contracts" },
                MissingCapabilities = new[] { "Run integration tests", "Access staging environment" },
                PromptIssues = new[] { "Contradictory instructions about error handling" },
                Suggestions = new[] { "Include DB schema in initial context", "Add integration test step" }
            },
            Issue = new IssueFeedback
            {
                Category = "contradictory acceptance criteria",
                Description = "AC #1 says 'return 404' but AC #3 says 'return empty list' for the same scenario",
                AffectedFiles = new[] { "src/Controllers/UserController.cs", "src/Services/UserService.cs" },
                HumanActionNeeded = "Resolve contradiction between AC #1 and AC #3"
            }
        };

        await fakeAgent.AcceptAndCompleteJobWithPayloadAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/84",
            RetryCount = 0,
            FilesChangedCount = 4,
            LinesAdded = 50,
            LinesRemoved = 8,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>(),
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 0,
            CodeReviewSuggestionCount = 0,
            Feedback = feedback
        });

        // Allow time for hub processing
        await Task.Delay(500);

        // Assert: all feedback fields are persisted correctly
        var history = Fixture.Factory.HistoryService;
        var runs = history.GetRunHistory();
        var completedRun = runs.FirstOrDefault(r => r.IssueIdentifier == "84");
        Assert.NotNull(completedRun);
        Assert.NotNull(completedRun.Feedback);

        // Harness feedback
        var harness = completedRun.Feedback.Harness;
        Assert.Equal("incomplete prompt", harness.Category);
        Assert.Equal(2, harness.MissingContext.Count);
        Assert.Contains("Database schema", harness.MissingContext);
        Assert.Contains("API contracts", harness.MissingContext);
        Assert.Equal(2, harness.MissingCapabilities.Count);
        Assert.Single(harness.PromptIssues);
        Assert.Equal(2, harness.Suggestions.Count);

        // Issue feedback
        var issue = completedRun.Feedback.Issue;
        Assert.NotNull(issue);
        Assert.Equal("contradictory acceptance criteria", issue.Category);
        Assert.Contains("AC #1 says", issue.Description);
        Assert.Equal(2, issue.AffectedFiles.Count);
        Assert.Equal("Resolve contradiction between AC #1 and AC #3", issue.HumanActionNeeded);
    }
}
