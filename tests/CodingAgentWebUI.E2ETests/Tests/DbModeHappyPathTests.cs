using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// DB-mode E2E tests exercising the full pipeline path:
/// UI dispatch → WorkItem creation → agent receives via SignalR →
/// agent completes → WorkItem transitions → history persisted.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "DbMode")]
public sealed class DbModeHappyPathTests : DbModeE2ETestBase, IClassFixture<DbModeE2EFixture>
{
    public DbModeHappyPathTests(DbModeE2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Seeds a template + profile + issue required for dispatch tests.
    /// </summary>
    private async Task SeedTestDataAsync(string issueId = "42", string issueTitle = "Test issue")
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = issueTitle,
            Description = "## Requirements\nDo the thing\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-db-e2e",
            Name = "DB E2E Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-db-e2e",
            DisplayName = "DB E2E Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DbMode_FullPipeline_DispatchToCompletion_WorkItemTransitions()
    {
        // Arrange
        await SeedTestDataAsync();
        await using var agent = new FakeAgentClient("db-agent-1", "db-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: dispatch issue through the full orchestration path
        var result = await DispatchIssueAsync("42");

        // Assert: distribution succeeded and WorkItem was created
        Assert.True(result.Success, $"Distribution failed: {result.ErrorMessage}");
        Assert.NotNull(result.WorkItemId);
        var workItemId = Guid.Parse(result.WorkItemId);

        // Assert: WorkItem is Dispatched in DB
        var dispatched = await WaitForWorkItemStatusAsync(
            workItemId, WorkItemStatus.Dispatched, TimeSpan.FromSeconds(10));
        Assert.Equal(WorkItemStatus.Dispatched, dispatched.Status);
        Assert.NotNull(dispatched.DispatchedAt);

        // Assert: FakeAgentClient received the job via SignalR
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("42", assignment.IssueIdentifier);

        // Agent accepts and completes the job
        await agent.AcceptAndCompleteJobAsync(assignment.JobId);

        // Assert: WorkItem transitions to Succeeded
        var succeeded = await WaitForWorkItemStatusAsync(
            workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        Assert.Equal(WorkItemStatus.Succeeded, succeeded.Status);
        Assert.NotNull(succeeded.CompletedAt);

        // Assert: history has completed run
        var history = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "42" && r.FinalStep == PipelineStep.Completed,
            TimeSpan.FromSeconds(10));
        Assert.NotNull(history);
    }

    [Fact]
    public async Task DbMode_FullPipeline_AgentFails_WorkItemTransitionedToFailed()
    {
        // Arrange
        await SeedTestDataAsync("43", "Failing issue");
        await using var agent = new FakeAgentClient("db-agent-fail", "db-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: dispatch
        var result = await DispatchIssueAsync("43");
        Assert.True(result.Success, $"Distribution failed: {result.ErrorMessage}");
        var workItemId = Guid.Parse(result.WorkItemId!);

        // Wait for agent to receive job
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Agent reports failure
        await agent.AcceptAndCompleteJobAsync(assignment.JobId, PipelineStep.Failed);

        // Assert: WorkItem transitions to Failed
        var failed = await WaitForWorkItemStatusAsync(
            workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(15));
        Assert.Equal(WorkItemStatus.Failed, failed.Status);
    }

    [Fact]
    public async Task DbMode_NoAgentAvailable_WorkItemQueuedAsPending_DrainedWhenAgentConnects()
    {
        // Arrange: seed data but do NOT connect agent yet
        await SeedTestDataAsync("44", "Pending issue");

        // Act: dispatch without any agent connected
        var result = await DispatchIssueAsync("44");
        Assert.True(result.Success, $"Distribution failed: {result.ErrorMessage}");
        var workItemId = Guid.Parse(result.WorkItemId!);

        // Assert: WorkItem is Pending (no agent available)
        var pending = await WaitForWorkItemStatusAsync(
            workItemId, WorkItemStatus.Pending, TimeSpan.FromSeconds(10));
        Assert.Equal(WorkItemStatus.Pending, pending.Status);
        Assert.Null(pending.DispatchedAt); // Reset to null when moved to Pending

        // NOW connect a FakeAgentClient
        await using var agent = new FakeAgentClient("db-agent-drain", "db-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Wait for PendingWorkItemDrainService to pick up the pending item
        // (drain interval is 5 seconds by default, but also wakes on agent signal)
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("44", assignment.IssueIdentifier);

        // Agent completes the job
        await agent.AcceptAndCompleteJobAsync(assignment.JobId);

        // Assert: WorkItem transitions to Succeeded
        var succeeded = await WaitForWorkItemStatusAsync(
            workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        Assert.Equal(WorkItemStatus.Succeeded, succeeded.Status);
    }

    [Fact]
    public async Task DbMode_AgentDisconnects_HeartbeatMonitorFailsRun_WorkItemFailed()
    {
        // Arrange: configure short grace period for faster test
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            AgentDisconnectGracePeriod = TimeSpan.FromSeconds(1),
            HeartbeatTimeoutSeconds = 2,
            HeartbeatSweepIntervalSeconds = 5
        }, CancellationToken.None);

        await SeedTestDataAsync("45", "Disconnect issue");

        // Connect agent and dispatch
        var agent = new FakeAgentClient("db-agent-disconnect", "db-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("45");
        Assert.True(result.Success, $"Distribution failed: {result.ErrorMessage}");
        var workItemId = Guid.Parse(result.WorkItemId!);

        // Agent receives and accepts job (but doesn't complete)
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await agent.AcceptJobAsync(assignment.JobId);

        // Disconnect the agent (dispose closes the connection)
        await agent.DisposeAsync();

        // Wait for HeartbeatMonitor to detect disconnect and fail the run
        // HeartbeatSweepIntervalSeconds=5 (set in InMemoryConfigurationStore defaults),
        // grace period is 1s, so detection takes at most ~12s.
        var failed = await WaitForWorkItemStatusAsync(
            workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(20));
        Assert.Equal(WorkItemStatus.Failed, failed.Status);
    }

    [Fact]
    public async Task DbMode_ProjectSecrets_DeliveredToAgent()
    {
        // Arrange: seed project with secrets
        var projectWithSecrets = new PipelineProject
        {
            Id = "project-secrets-e2e",
            Name = "Secrets Test Project",
            Enabled = true,
            TemplateIds = new List<string>(),
            Secrets = new Dictionary<string, string>
            {
                ["API_KEY"] = "secret-value-123",
                ["DB_PASSWORD"] = "super-secret"
            }
        };
        await Fixture.ConfigStore.SaveProjectAsync(projectWithSecrets, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "46",
            Title = "Secrets test issue",
            Description = "## Requirements\nNeed secrets\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        await Fixture.ConfigStore.SaveTemplateAsync("project-secrets-e2e", new PipelineJobTemplate
        {
            Id = "template-secrets-e2e",
            Name = "Secrets Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-secrets-e2e",
            DisplayName = "Secrets Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var agent = new FakeAgentClient("db-agent-secrets", "db-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: dispatch with the secrets project
        var result = await DispatchIssueAsync("46", projectId: "project-secrets-e2e");
        Assert.True(result.Success, $"Distribution failed: {result.ErrorMessage}");

        // Assert: agent received the job with secrets
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(assignment.ProjectSecrets);
        Assert.Equal("secret-value-123", assignment.ProjectSecrets["API_KEY"]);
        Assert.Equal("super-secret", assignment.ProjectSecrets["DB_PASSWORD"]);
    }

    [Fact]
    public async Task DbMode_MultiAgent_TwoAgents_BothReceiveJobs_WorkItemsTrack()
    {
        // Arrange: seed two issues
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "47",
            Title = "First multi-agent issue",
            Description = "## Requirements\nTask 1\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "48",
            Title = "Second multi-agent issue",
            Description = "## Requirements\nTask 2\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-multi-e2e",
            Name = "Multi Agent Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-multi-e2e",
            DisplayName = "Multi Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Connect two agents
        await using var agent1 = new FakeAgentClient("db-multi-1", "db-e2e");
        await using var agent2 = new FakeAgentClient("db-multi-2", "db-e2e");
        await agent1.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await agent2.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: dispatch both issues
        var result1 = await DispatchIssueAsync("47");
        Assert.True(result1.Success, $"First dispatch failed: {result1.ErrorMessage}");
        var workItemId1 = Guid.Parse(result1.WorkItemId!);

        var result2 = await DispatchIssueAsync("48");
        Assert.True(result2.Success, $"Second dispatch failed: {result2.ErrorMessage}");
        var workItemId2 = Guid.Parse(result2.WorkItemId!);

        // Both WorkItems should be Dispatched
        await WaitForWorkItemStatusAsync(workItemId1, WorkItemStatus.Dispatched, TimeSpan.FromSeconds(10));
        await WaitForWorkItemStatusAsync(workItemId2, WorkItemStatus.Dispatched, TimeSpan.FromSeconds(10));

        // Wait for both agents to receive jobs (one each)
        var assignment1 = await agent1.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var assignment2 = await agent2.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Both agents complete their jobs
        await agent1.AcceptAndCompleteJobAsync(assignment1.JobId);
        await agent2.AcceptAndCompleteJobAsync(assignment2.JobId);

        // Assert: both WorkItems transition to Succeeded
        var succeeded1 = await WaitForWorkItemStatusAsync(
            workItemId1, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        var succeeded2 = await WaitForWorkItemStatusAsync(
            workItemId2, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        Assert.Equal(WorkItemStatus.Succeeded, succeeded1.Status);
        Assert.Equal(WorkItemStatus.Succeeded, succeeded2.Status);
    }
}
