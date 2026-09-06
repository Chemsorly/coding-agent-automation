using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
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
[Collection(E2ECollection.Name)]
public sealed class DbModeHappyPathTests : HeadlessE2ETestBase
{
    public DbModeHappyPathTests(E2EFixture fixture) : base(fixture) { }

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
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

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
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

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
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

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
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

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
        // Use a valid GUID as project ID — Projects.Id is UUID in the DB schema,
        // and DispatchOrchestrationService uses Guid.TryParse which silently returns
        // null for non-GUID strings, causing ProjectId to be null on the work item.
        const string secretsProjectId = "11111111-2222-3333-4444-555555555555";

        // Arrange: seed project with secrets
        var projectWithSecrets = new PipelineProject
        {
            Id = secretsProjectId,
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

        await Fixture.ConfigStore.SaveTemplateAsync(secretsProjectId, new PipelineJobTemplate
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
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: dispatch with the secrets project
        var result = await DispatchIssueAsync("46", projectId: secretsProjectId);
        Assert.True(result.Success, $"Distribution failed: {result.ErrorMessage}");

        // Assert: agent received the job with secrets
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(assignment.ProjectSecrets);
        Assert.Equal("secret-value-123", assignment.ProjectSecrets["API_KEY"]);
        Assert.Equal("super-secret", assignment.ProjectSecrets["DB_PASSWORD"]);
    }

    /// <summary>
    /// Integration/E2E test for issue #2359 (ConflictRestart short-circuit).
    ///
    /// Verifies the full pipeline path:
    /// CI never starts + conflicted PR → agent reports ConflictRestart with FinalLabel=agent:next
    /// → PostCompletionBookkeepingAsync reads FinalLabel and swaps issue label to agent:next
    /// → WorkItem transitions to Succeeded (ConflictRestart is a clean termination, not a failure)
    /// → history record has FinalStep=ConflictRestart
    ///
    /// The "outer loop re-dispatch" part (a second job appearing for the same issue after the label swap)
    /// is covered by the closed-loop dispatch tests; this test validates the label-swap and history
    /// segments that the unit tests cannot reach (they mock PostCompletionBookkeepingAsync away).
    /// </summary>
    [Fact]
    public async Task DbMode_ConflictRestart_AgentReportsConflictRestart_LabelSwappedToNext_HistoryRecorded()
    {
        // Arrange
        await SeedTestDataAsync("conflict-restart-1", "ConflictRestart E2E test issue");
        await using var agent = new FakeAgentClient("db-agent-conflict-restart", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: dispatch the issue
        var result = await DispatchIssueAsync("conflict-restart-1");
        Assert.True(result.Success, $"Distribution failed: {result.ErrorMessage}");
        var workItemId = Guid.Parse(result.WorkItemId!);

        // Assert: WorkItem is dispatched
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Dispatched, TimeSpan.FromSeconds(10));

        // Wait for the agent to receive the job
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("conflict-restart-1", assignment.IssueIdentifier);

        // Simulate the conflict-restart path: agent accepts, reports RunningQualityGates step,
        // then completes with ConflictRestart + FinalLabel = agent:next (no PR URL — the run
        // detected the conflict before creating a final PR)
        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, PipelineStep.RunningQualityGates);

        await agent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.ConflictRestart,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = AgentLabels.Next,          // key: overrides step-derived label
            FailureReason = "PR conflicted with main — restarting pipeline",
            PullRequestUrl = null,                  // no final PR for conflict-restart
            RetryCount = 0,                         // ConflictRestart must not increment retry count
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

        // Assert: WorkItem transitions to Succeeded
        // ConflictRestart is a clean (non-error) termination — the pipeline completed its work
        // (detected the conflict, set FinalLabel) and the outer loop will re-dispatch from the queue.
        var completed = await WaitForWorkItemStatusAsync(
            workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        Assert.Equal(WorkItemStatus.Succeeded, completed.Status);

        // Assert: history record has FinalStep = ConflictRestart
        var history = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "conflict-restart-1" && r.FinalStep == PipelineStep.ConflictRestart,
            TimeSpan.FromSeconds(10));
        Assert.NotNull(history);
        Assert.Equal(PipelineStep.ConflictRestart, history.FinalStep);

        // Assert: issue label was swapped to agent:next (FinalLabel override honoured by PostCompletionBookkeepingAsync)
        // LabelChanges records all Add/Remove calls from the SwapLabelAsync path.
        // We expect agent:next was added (the final state) after agent:in-progress was removed.
        var labelChanges = Fixture.IssueProvider.LabelChanges;
        // TODO [WARNING]: This assertion only verifies that agent:next was *added*, not that
        // agent:in-progress was *removed*. A broken SwapLabelAsync that appends agent:next without
        // removing agent:in-progress would still pass. Add a complementary assertion:
        //   Assert.Contains(labelChanges, lc =>
        //       lc.Identifier == "conflict-restart-1" &&
        //       lc.Label == AgentLabels.InProgress &&
        //       !lc.Added);
        // to verify the full swap semantics. (#2359)
        Assert.Contains(labelChanges, lc =>
            lc.Identifier == "conflict-restart-1" &&
            lc.Label == AgentLabels.Next &&
            lc.Added);
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
        await agent1.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
        await agent2.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

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

    // ═══════════════════════════════════════════════════════════════════════
    // Token Refresh — validates that the in-memory PipelineRun path works
    // for DB+SignalR mode (created during dispatch, used by RequestTokenRefresh)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DbMode_TokenRefresh_AfterJobAccepted_ReturnsValidToken()
    {
        // Arrange: seed a repo provider config with a static AccessToken
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-db-token-test",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "DB Token Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "fake-db-mode-token-e2e"
            }
        }, CancellationToken.None);

        // Seed issue + template using the token-bearing repo provider
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "50",
            Title = "Token refresh test issue",
            Description = "## Requirements\nNeeds token\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-db-token-e2e",
            Name = "DB Token Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-db-token-test", // Uses the provider with AccessToken
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-db-token-e2e",
            DisplayName = "DB Token Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Connect agent
        await using var agent = new FakeAgentClient("db-agent-token", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: dispatch issue
        var result = await DispatchIssueAsync("50", templateId: "template-db-token-e2e");
        Assert.True(result.Success, $"Distribution failed: {result.ErrorMessage}");

        // Wait for agent to receive the job
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("50", assignment.IssueIdentifier);

        // Accept the job (this sets ActiveJobId on the agent in the registry)
        await agent.AcceptJobAsync(assignment.JobId);

        // Act: call RequestTokenRefresh — this is the operation missing from all prior E2E tests
        var tokenResponse = await agent.RequestTokenRefreshAsync(assignment.JobId, ProviderKind.Repository);

        // Assert: token vending worked through the in-memory PipelineRun path
        Assert.NotNull(tokenResponse);
        Assert.Equal("fake-db-mode-token-e2e", tokenResponse.Token);
        Assert.True(tokenResponse.ExpiresAt > DateTimeOffset.UtcNow);

        // Cleanup: complete the job so it doesn't block other tests
        await agent.AcceptAndCompleteJobAsync(assignment.JobId);
    }

    [Fact]
    public async Task DbMode_TokenRefresh_BrainProvider_ReturnsSeparateToken()
    {
        // Arrange: seed both repo and brain providers with different tokens
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-db-brain-main",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "DB Brain Main Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "fake-db-repo-token"
            }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "brain-db-e2e",
            Kind = ProviderKind.Repository, // Brain is Repository-kind
            ProviderType = "GitLab",
            DisplayName = "DB Brain Knowledge Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "fake-db-brain-token"
            }
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "51",
            Title = "Brain token test",
            Description = "## Requirements\nNeeds brain\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-db-brain-e2e",
            Name = "DB Brain Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-db-brain-main",
            BrainProviderId = "brain-db-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-db-brain-e2e",
            DisplayName = "DB Brain Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var agent = new FakeAgentClient("db-agent-brain", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Act: dispatch
        var result = await DispatchIssueAsync("51", templateId: "template-db-brain-e2e");
        Assert.True(result.Success, $"Distribution failed: {result.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await agent.AcceptJobAsync(assignment.JobId);

        // Act: request both repo and brain tokens
        var repoToken = await agent.RequestTokenRefreshAsync(assignment.JobId, ProviderKind.Repository);
        var brainToken = await agent.RequestTokenRefreshAsync(assignment.JobId, ProviderKind.Brain);

        // Assert: different tokens for different scopes
        Assert.Equal("fake-db-repo-token", repoToken.Token);
        Assert.Equal("fake-db-brain-token", brainToken.Token);

        // Cleanup
        await agent.AcceptAndCompleteJobAsync(assignment.JobId);
    }
}
