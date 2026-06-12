using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Tests that validate multi-agent dispatch behavior:
/// - Agent visibility in the monitoring page after connection
/// - Multiple agents receiving separate jobs concurrently
/// Uses FakeAgentClient to simulate real SignalR agent connections.
/// </summary>
[Trait("Category", "E2E")]
public sealed class MultiAgentDispatchTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public MultiAgentDispatchTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task MultiAgent_AgentConnects_ShowsInMonitoringPage()
    {
        // Arrange: connect a fake agent
        await using var fakeAgent = new FakeAgentClient("monitor-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: navigate to the monitoring page
        var monitoringPage = new AgentMonitoringPage(Page, BaseUrl);
        await monitoringPage.NavigateAsync();

        // Assert: the agent is visible with "Idle" status
        var isVisible = await monitoringPage.IsAgentVisibleAsync("monitor-agent-1");
        Assert.True(isVisible, "Expected agent 'monitor-agent-1' to be visible on the monitoring page");

        await monitoringPage.WaitForAgentStatusAsync("monitor-agent-1", "Idle", timeoutMs: 15_000);

        var agentCount = await monitoringPage.GetRegisteredAgentCountAsync();
        Assert.True(agentCount >= 1, $"Expected at least 1 registered agent, got {agentCount}");
    }

    [Fact]
    public async Task MultiAgent_TwoAgents_BothReceiveJobs()
    {
        // Arrange: seed two issues
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "42",
            Title = "First issue for agent 1",
            Description = "## Requirements\nFirst task\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "43",
            Title = "Second issue for agent 2",
            Description = "## Requirements\nSecond task\n\n## Acceptance Criteria\n- [ ] Done",
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

        // Add an agent profile matching both agents' labels
        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Connect two fake agents with the same labels
        await using var agent1 = new FakeAgentClient("multi-agent-1", "e2e");
        await using var agent2 = new FakeAgentClient("multi-agent-2", "e2e");
        await agent1.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await agent2.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: dispatch first issue from the UI
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

        // Wait for one of the agents to receive the first job
        var firstAssignment = await Task.WhenAny(
            agent1.JobAssigned.Task,
            agent2.JobAssigned.Task
        );
        var assignment1 = await firstAssignment;
        Assert.Equal("42", assignment1.IssueIdentifier);

        // Determine which agent got the first job and which is still free
        FakeAgentClient busyAgent;
        FakeAgentClient freeAgent;
        if (agent1.ReceivedJobIds.Count > 0)
        {
            busyAgent = agent1;
            freeAgent = agent2;
        }
        else
        {
            busyAgent = agent2;
            freeAgent = agent1;
        }

        // First agent accepts the job (keeps it busy)
        await busyAgent.AcceptJobAsync(assignment1.JobId);
        await busyAgent.ReportStepAsync(assignment1.JobId, PipelineStep.CloningRepository);

        // Wait for the busy agent to be marked as Busy in registry before dispatching second job
        var registry = Fixture.Factory.AgentRegistry;
        await WaitUntilAsync(() => registry.GetByAgentId(busyAgent.AgentId)?.Status == AgentStatus.Busy);

        // Navigate back to dispatch the second issue
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync("E2E Test Template");
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync("43");
        await codingPage.ClickStartPipelineAsync();

        // Wait for dispatch success of second issue
        await Page.WaitForSelectorAsync(
            ".settings-status.status-success",
            new() { Timeout = 10_000 });

        // Wait for the free agent to receive the second job
        var assignment2 = await freeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("43", assignment2.IssueIdentifier);

        // Both agents complete their jobs
        await busyAgent.ReportStepAsync(assignment1.JobId, PipelineStep.GeneratingCode);
        await busyAgent.ReportStepAsync(assignment1.JobId, PipelineStep.Completed);
        await busyAgent.ReportCompletionAsync(assignment1.JobId, CreateCompletionPayload("https://github.com/e2e-org/e2e-repo/pull/1"));

        await freeAgent.AcceptJobAsync(assignment2.JobId);
        await freeAgent.ReportStepAsync(assignment2.JobId, PipelineStep.CloningRepository);
        await freeAgent.ReportStepAsync(assignment2.JobId, PipelineStep.GeneratingCode);
        await freeAgent.ReportStepAsync(assignment2.JobId, PipelineStep.Completed);
        await freeAgent.ReportCompletionAsync(assignment2.JobId, CreateCompletionPayload("https://github.com/e2e-org/e2e-repo/pull/2"));

        // Assert: history shows two completed runs
        await WaitForHistoryAsync(r => r.IssueIdentifier == "43"); // wait for second run
        var history = Fixture.Factory.HistoryService;
        var runs = history.GetRunHistory();
        Assert.True(runs.Count >= 2, $"Expected at least 2 completed runs in history, got {runs.Count}");

        var run42 = runs.FirstOrDefault(r => r.IssueIdentifier == "42");
        var run43 = runs.FirstOrDefault(r => r.IssueIdentifier == "43");
        Assert.NotNull(run42);
        Assert.NotNull(run43);
        Assert.Equal(PipelineStep.Completed, run42.FinalStep);
        Assert.Equal(PipelineStep.Completed, run43.FinalStep);
    }

    private static JobCompletionPayload CreateCompletionPayload(string pullRequestUrl) => new()
    {
        FinalStep = PipelineStep.Completed,
        CompletedAt = DateTimeOffset.UtcNow,
        PullRequestUrl = pullRequestUrl,
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
    };
}
