using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// DB-mode E2E edge case tests: dedup guards, concurrent dispatch, lifecycle management,
/// stale detection, and multi-agent queue distribution.
/// Exercises paths where race conditions and plumbing bugs commonly occur.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "DbMode")]
public sealed class DbModeEdgeCaseTests : DbModeE2ETestBase, IClassFixture<DbModeE2EFixture>
{
    public DbModeEdgeCaseTests(DbModeE2EFixture fixture) : base(fixture) { }

    private async Task SeedIssueAndProfileAsync(string issueId, string title = "Test issue")
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = title,
            Description = "## Requirements\nDo the thing\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        // Only seed template/profile if not already seeded (shared across tests in same class)
        var templates = await Fixture.ConfigStore.LoadAllTemplatesAsync(CancellationToken.None);
        if (!templates.Any(t => t.Id == "template-edge-e2e"))
        {
            await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
            {
                Id = "template-edge-e2e",
                Name = "Edge Case Template",
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                Enabled = true
            }, CancellationToken.None);
        }

        var profiles = await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None);
        if (!profiles.Any(p => p.Id == "profile-edge-e2e"))
        {
            await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
            {
                Id = "profile-edge-e2e",
                DisplayName = "Edge Case Agent Profile",
                MatchLabels = new[] { "edge-e2e" },
                AgentProviderConfigId = "agent-e2e",
                Enabled = true
            }, CancellationToken.None);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // F2: Dedup — dispatch same issue twice → second rejected
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DbMode_DuplicateDispatch_SecondAttemptRejected()
    {
        // Arrange
        await SeedIssueAndProfileAsync("100");
        await using var agent = new FakeAgentClient("edge-agent-dedup", "edge-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: first dispatch succeeds
        var result1 = await DispatchIssueAsync("100");
        Assert.True(result1.Success, $"First dispatch failed: {result1.ErrorMessage}");

        // Act: second dispatch for the same issue should fail (dedup guard)
        var result2 = await DispatchIssueAsync("100");

        // Assert: second dispatch is rejected (issue already active)
        Assert.False(result2.Success,
            "Second dispatch should fail — issue is already being processed");
    }

    [Fact]
    public async Task DbMode_DuplicateDispatch_AfterCompletion_SecondAttemptSucceeds()
    {
        // Arrange
        await SeedIssueAndProfileAsync("101");
        await using var agent = new FakeAgentClient("edge-agent-dedup2", "edge-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // First dispatch + complete
        var result1 = await DispatchIssueAsync("101");
        Assert.True(result1.Success);

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await agent.AcceptAndCompleteJobAsync(assignment.JobId);

        // Wait for completion to propagate
        await WaitForHistoryAsync(
            r => r.IssueIdentifier == "101" && r.FinalStep == PipelineStep.Completed,
            TimeSpan.FromSeconds(15));

        // Re-seed the issue (it was consumed by the first dispatch)
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "101",
            Title = "Re-dispatched issue",
            Description = "## Requirements\nAgain\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        // Act: second dispatch after completion should succeed
        agent.ResetJobAssigned();
        var result2 = await DispatchIssueAsync("101");

        // Assert: succeeds because issue is no longer active
        Assert.True(result2.Success, $"Re-dispatch after completion should succeed: {result2.ErrorMessage}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // F4: Concurrent drain + manual dispatch — only one path wins
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DbMode_ConcurrentDrainAndDispatch_NoDoubleDispatch()
    {
        // Arrange: seed two separate issues (to avoid dedup) and one agent
        await SeedIssueAndProfileAsync("200");
        await SeedIssueAndProfileAsync("201");

        // Dispatch first issue WITHOUT an agent → goes to Pending
        var result1 = await DispatchIssueAsync("200");
        Assert.True(result1.Success);
        Assert.True(result1.Queued, "Should be queued as Pending (no agent)");

        // Now connect agent — this triggers drain service
        await using var agent = new FakeAgentClient("edge-agent-concurrent", "edge-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Wait for drain to deliver the pending item
        var assignment1 = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("200", assignment1.IssueIdentifier);

        // Agent is now busy — dispatch second issue while agent occupied
        var result2 = await DispatchIssueAsync("201");
        Assert.True(result2.Success);

        // Second issue should be queued (agent is busy with first)
        // OR dispatched if agent completed fast — either way only one job per agent at a time
        var workItemId2 = Guid.Parse(result2.WorkItemId!);

        // Complete first job to free the agent
        await agent.AcceptAndCompleteJobAsync(assignment1.JobId);

        // If the second issue was queued, drain should pick it up now
        agent.ResetJobAssigned();

        // Wait for second job (either via drain or direct dispatch)
        var assignment2 = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("201", assignment2.IssueIdentifier);

        // Complete second job
        await agent.AcceptAndCompleteJobAsync(assignment2.JobId);

        // Assert: both WorkItems eventually reach Succeeded
        await WaitForWorkItemStatusAsync(
            Guid.Parse(result1.WorkItemId!), WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        await WaitForWorkItemStatusAsync(
            workItemId2, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // F5: DispatchOrchestrationService full orchestration test
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DbMode_DispatchOrchestration_PreparesFullRequest_WithProviderConfigs()
    {
        // Arrange
        await SeedIssueAndProfileAsync("300", "Orchestration test issue");
        await using var agent = new FakeAgentClient("edge-agent-orch", "edge-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: dispatch and capture the full assignment
        var result = await DispatchIssueAsync("300");
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: JobAssignmentMessage has all required fields populated by orchestration
        Assert.Equal("300", assignment.IssueIdentifier);
        Assert.Equal("Orchestration test issue", assignment.IssueDetail.Title);
        Assert.NotNull(assignment.ParsedIssue);
        Assert.NotNull(assignment.PipelineConfiguration);
        Assert.NotEmpty(assignment.ProviderConfigs);
        Assert.Equal("repo-e2e", assignment.RepoProviderConfigId);
        Assert.Equal("agent-e2e", assignment.AgentProviderConfigId);
        Assert.NotNull(assignment.ResolvedProfileId);
        Assert.Equal("profile-edge-e2e", assignment.ResolvedProfileId);

        // Provider configs should include at minimum: repo + agent
        Assert.Contains(assignment.ProviderConfigs, c => c.Id == "repo-e2e");
        Assert.Contains(assignment.ProviderConfigs, c => c.Id == "agent-e2e");
    }

    [Fact]
    public async Task DbMode_DispatchOrchestration_IssueNotFound_ReturnsNull()
    {
        // Arrange: seed profile/template but NOT the issue
        await SeedIssueAndProfileAsync("will-be-removed");
        // Remove the issue so PrepareAsync can't fetch it
        Fixture.IssueProvider.Issues.Clear();
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "999-does-not-exist",
            Title = "Wrong issue",
            Description = "Wrong",
            Labels = Array.Empty<string>()
        });

        // Act: dispatch issue that doesn't exist in the provider
        // PrepareDistributionRequestAsync fetches the issue by identifier —
        // if the issue provider doesn't have "301", orchestration should fail gracefully
        try
        {
            var result = await DispatchIssueAsync("301");
            // If dispatch succeeds despite the issue not existing, the issue provider
            // returned data for "301" (unexpected). This test verifies graceful failure.
            // Since InMemoryIssueProvider returns the first matching issue by identifier,
            // and we cleared and added a different one, this should throw in DispatchIssueAsync.
            Assert.False(result.Success, "Dispatch should fail when issue not found in provider");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("PrepareDistributionRequestAsync returned null"))
        {
            // Expected — orchestration returned null because issue wasn't found
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // F6: RunLifecycleManager.FailRunAsync — WorkItem + in-memory + history
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DbMode_RunLifecycleManager_FailRunAsync_AllStoresUpdated()
    {
        // Arrange
        await SeedIssueAndProfileAsync("400");
        await using var agent = new FakeAgentClient("edge-agent-lifecycle", "edge-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("400");
        Assert.True(result.Success);
        var workItemId = Guid.Parse(result.WorkItemId!);

        // Wait for agent to receive job
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await agent.AcceptJobAsync(assignment.JobId);

        // Act: directly invoke FailRunAsync via the lifecycle manager (simulating infrastructure failure)
        var lifecycleManager = Fixture.Factory.Services.GetRequiredService<IRunLifecycleManager>();
        var failedRun = await lifecycleManager.FailRunAsync(
            assignment.JobId,
            "Simulated infrastructure failure",
            CancellationToken.None);

        // Assert: run was processed (not null = first caller won the atomic claim)
        Assert.NotNull(failedRun);
        Assert.Equal("Simulated infrastructure failure", failedRun.FailureReason);
        Assert.Equal(PipelineStep.Failed, failedRun.CurrentStep);

        // Assert: WorkItem in DB is Failed
        var failedItem = await WaitForWorkItemStatusAsync(
            workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(10));
        Assert.Equal(WorkItemStatus.Failed, failedItem.Status);

        // Assert: history has the failed run
        var history = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "400" && r.FinalStep == PipelineStep.Failed,
            TimeSpan.FromSeconds(10));
        Assert.NotNull(history);

        // Assert: agent returned to Idle
        var registry = Fixture.Factory.Services.GetRequiredService<AgentRegistryService>();
        await WaitUntilAsync(
            () => registry.GetByAgentId("edge-agent-lifecycle")?.Status == AgentStatus.Idle,
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DbMode_RunLifecycleManager_DoubleFailRun_SecondReturnsNull()
    {
        // Arrange
        await SeedIssueAndProfileAsync("401");
        await using var agent = new FakeAgentClient("edge-agent-double-fail", "edge-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("401");
        Assert.True(result.Success);

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await agent.AcceptJobAsync(assignment.JobId);

        // Act: call FailRunAsync twice for the same runId
        var lifecycleManager = Fixture.Factory.Services.GetRequiredService<IRunLifecycleManager>();
        var first = await lifecycleManager.FailRunAsync(assignment.JobId, "First failure", CancellationToken.None);
        var second = await lifecycleManager.FailRunAsync(assignment.JobId, "Second failure", CancellationToken.None);

        // Assert: first call succeeds, second returns null (atomic RemoveRun pattern)
        Assert.NotNull(first);
        Assert.Null(second); // Already processed — no double-persist
    }

    // ═══════════════════════════════════════════════════════════════════════
    // F7: Stale dispatch detection — WorkItem stuck in Dispatched → Failed
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DbMode_StaleDispatchDetection_StuckItem_TransitionedToFailed()
    {
        // Arrange: manually insert a WorkItem in Dispatched state with old timestamp
        // (simulating a silent SignalR delivery failure where agent never processed the message)
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var staleWorkItem = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "500-stale",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Dispatched,
            Payload = "{}",
            AgentSelector = "edge-e2e",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-10), // 10 minutes ago = stale
            TimeoutSeconds = 3600
        };
        db.WorkItems.Add(staleWorkItem);
        await db.SaveChangesAsync();

        // Act: invoke stale detection directly
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        var stuckCount = await distributor.ReconcileStuckItemsAsync(CancellationToken.None);

        // Assert: at least one stuck item detected
        Assert.True(stuckCount >= 1, $"Expected at least 1 stuck item, got {stuckCount}");

        // Assert: WorkItem is now Failed
        var failedItem = await WaitForWorkItemStatusAsync(
            staleWorkItem.Id, WorkItemStatus.Failed, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemStatus.Failed, failedItem.Status);
    }

    [Fact]
    public async Task DbMode_StaleDispatchDetection_RecentItem_NotAffected()
    {
        // Arrange: insert a WorkItem in Dispatched state with RECENT timestamp
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var recentWorkItem = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "501-recent",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Dispatched,
            Payload = "{}",
            AgentSelector = "edge-e2e",
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-30), // 30 seconds ago = NOT stale
            TimeoutSeconds = 3600
        };
        db.WorkItems.Add(recentWorkItem);
        await db.SaveChangesAsync();

        // Act: invoke stale detection
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        await distributor.ReconcileStuckItemsAsync(CancellationToken.None);

        // Assert: WorkItem still in Dispatched (not affected by stale detection)
        await using var checkDb = Fixture.DbContextFactory.CreateDbContext();
        var item = await checkDb.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == recentWorkItem.Id);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Dispatched, item.Status);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // F8: Multiple agents connect → queued jobs distributed correctly
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DbMode_MultipleAgents_QueuedJobsDistributedAcrossAgents()
    {
        // Arrange: seed 3 issues
        await SeedIssueAndProfileAsync("600");
        await SeedIssueAndProfileAsync("601");
        await SeedIssueAndProfileAsync("602");

        // Dispatch all 3 without any agent → all go to Pending
        var r1 = await DispatchIssueAsync("600");
        var r2 = await DispatchIssueAsync("601");
        var r3 = await DispatchIssueAsync("602");

        Assert.True(r1.Success && r1.Queued, "Issue 600 should be queued");
        Assert.True(r2.Success && r2.Queued, "Issue 601 should be queued");
        Assert.True(r3.Success && r3.Queued, "Issue 602 should be queued");

        // Connect 2 agents — drain service should distribute 2 jobs
        await using var agent1 = new FakeAgentClient("edge-multi-1", "edge-e2e");
        await using var agent2 = new FakeAgentClient("edge-multi-2", "edge-e2e");
        await agent1.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await agent2.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Wait for both agents to receive jobs
        var job1 = await agent1.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var job2 = await agent2.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Assert: each agent got a different job
        Assert.NotEqual(job1.IssueIdentifier, job2.IssueIdentifier);

        // Assert: third issue should still be Pending (only 2 agents)
        var allIssuesDispatched = new HashSet<string> { job1.IssueIdentifier, job2.IssueIdentifier };
        var remainingIssue = new[] { "600", "601", "602" }.First(i => !allIssuesDispatched.Contains(i));

        // Find the WorkItem for the remaining issue
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var pendingItem = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IssueIdentifier == remainingIssue);
        Assert.NotNull(pendingItem);
        Assert.Equal(WorkItemStatus.Pending, pendingItem.Status);

        // Complete both jobs, then the third should drain
        await agent1.AcceptAndCompleteJobAsync(job1.JobId);
        agent1.ResetJobAssigned();

        // Wait for third job to be drained to agent1 (now idle)
        var job3 = await agent1.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(remainingIssue, job3.IssueIdentifier);

        await agent1.AcceptAndCompleteJobAsync(job3.JobId);
        await agent2.AcceptAndCompleteJobAsync(job2.JobId);

        // Assert: all 3 WorkItems eventually Succeeded
        await WaitForWorkItemStatusAsync(Guid.Parse(r1.WorkItemId!), WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        await WaitForWorkItemStatusAsync(Guid.Parse(r2.WorkItemId!), WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        await WaitForWorkItemStatusAsync(Guid.Parse(r3.WorkItemId!), WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task DbMode_LabelRouting_AgentOnlyReceivesMatchingJobs()
    {
        // Arrange: two agents with different labels
        // Seed a special profile that matches "special-label"
        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-special-e2e",
            DisplayName = "Special Label Profile",
            MatchLabels = new[] { "special-label" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Seed template that requires "special-label" (via RequiredLabels on repo provider config)
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-special",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Special Repo",
            RequiredLabels = new[] { "special-label" }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-special-e2e",
            Name = "Special Label Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-special",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "700",
            Title = "Special label issue",
            Description = "## Requirements\nSpecial\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        // Connect agents: one with matching label, one without
        await using var matchingAgent = new FakeAgentClient("edge-matching", "special-label");
        await using var nonMatchingAgent = new FakeAgentClient("edge-non-matching", "other-label");
        await matchingAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await nonMatchingAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: dispatch the special-label issue
        var orchService = Fixture.Factory.Services.GetRequiredService<IDispatchOrchestrationService>();
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        var project = await Fixture.ConfigStore.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);

        var request = await orchService.PrepareDistributionRequestAsync(
            "700", "issue-e2e", "repo-special", null, null, "label-routing-test", project!, ct: CancellationToken.None);
        Assert.NotNull(request);

        var distResult = await distributor.DistributeAsync(request, CancellationToken.None);
        Assert.True(distResult.Success, $"Dispatch failed: {distResult.ErrorMessage}");

        // Assert: matching agent receives the job
        var job = await matchingAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("700", job.IssueIdentifier);

        // Assert: non-matching agent does NOT receive the job
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Empty(nonMatchingAgent.ReceivedJobIds);
    }
}
