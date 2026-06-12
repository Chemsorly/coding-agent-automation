using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Happy-path E2E test: issue selected → dispatched to agent → agent completes →
/// success message shown in UI, agent receives and completes job.
/// Uses the multi-agent dispatch path (JobDispatcher → FakeAgentClient → ReportJobCompleted).
/// </summary>
[Trait("Category", "E2E")]
public sealed class HappyPathTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public HappyPathTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task FullPipeline_IssueToCompletion_ShowsPrLink()
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

        // Connect a fake agent (must be done before dispatch so it's available)
        await using var fakeAgent = new FakeAgentClient("fake-agent-1", "e2e");
        await fakeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: navigate and start pipeline
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Select template and browse issues
        await codingPage.SelectTemplateAsync("E2E Test Template");
        await codingPage.ClickBrowseIssuesAsync();

        // Select the issue and dispatch
        await codingPage.SelectIssueAsync("42");
        await codingPage.ClickStartPipelineAsync();

        // Assert: verify dispatch success message appears in the UI
        await Page.WaitForSelectorAsync(
            ".settings-status.status-success",
            new() { Timeout = 10_000 });
        var successText = await Page.TextContentAsync(".settings-status.status-success");
        Assert.Contains("Dispatched #42", successText);

        // Wait for the agent to receive the job assignment
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(assignment);
        Assert.Equal("42", assignment.IssueIdentifier);

        // Agent accepts and completes the job
        await fakeAgent.AcceptAndCompleteJobAsync(assignment.JobId);

        // Verify the run was completed by checking history service
        // (multi-agent runs are moved to history after completion)
        var completedRun = await WaitForHistoryAsync(r => r.IssueIdentifier == "42");
        Assert.Equal(PipelineStep.Completed, completedRun.FinalStep);
        Assert.Equal("https://github.com/e2e-org/e2e-repo/pull/1", completedRun.PullRequestUrl);
    }
}
