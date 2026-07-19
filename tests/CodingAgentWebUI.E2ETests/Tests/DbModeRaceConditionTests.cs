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
/// DB-mode E2E race condition tests: concurrent dispatch, simultaneous finalization,
/// parallel agent registration, queue ordering under drain pressure, and terminal-state
/// completion reporting.
/// These exercise the atomic claim patterns (RemoveRun, ConcurrentDictionary dedup,
/// _selectionLock) under actual concurrency — not just mocked sequential calls.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "DbMode")]
[Trait("Feature", "RaceCondition")]
public sealed class DbModeRaceConditionTests : DbModeE2ETestBase, IClassFixture<DbModeE2EFixture>
{
    public DbModeRaceConditionTests(DbModeE2EFixture fixture) : base(fixture) { }

    private async Task SeedIssueAndProfileAsync(string issueId, string title = "Race test issue")
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = title,
            Description = "## Requirements\nRace condition test\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        var templates = await Fixture.ConfigStore.LoadAllTemplatesAsync(CancellationToken.None);
        if (!templates.Any(t => t.Id == "template-race-e2e"))
        {
            await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
            {
                Id = "template-race-e2e",
                Name = "Race Condition Template",
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                Enabled = true
            }, CancellationToken.None);
        }

        var profiles = await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None);
        if (!profiles.Any(p => p.Id == "profile-race-e2e"))
        {
            await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
            {
                Id = "profile-race-e2e",
                DisplayName = "Race Condition Agent Profile",
                MatchLabels = new[] { "race-e2e" },
                AgentProviderConfigId = "agent-e2e",
                Enabled = true
            }, CancellationToken.None);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // C1: Concurrent dispatch to same issue — only one wins dedup
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Race_ConcurrentDispatchSameIssue_OnlyOneActiveWorkItem()
    {
        // Arrange: seed issue + connect agent so dispatch path is fully exercised
        await SeedIssueAndProfileAsync("2000", "Concurrent dispatch race");
        await using var agent = new FakeAgentClient("race-agent-c1", "race-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: fire 5 concurrent dispatches for the same issue
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => DispatchIssueAsync("2000")))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Assert: at most 1 succeeds with a non-null WorkItemId
        var successfulDispatches = results.Where(r => r.Success && r.WorkItemId is not null).ToList();
        Assert.True(successfulDispatches.Count <= 1,
            $"Expected at most 1 successful dispatch, got {successfulDispatches.Count}. " +
            "Dedup guard should prevent concurrent duplicates.");

        // Assert: DB has at most 1 non-terminal WorkItem for this issue
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var activeItems = await db.WorkItems.AsNoTracking()
            .Where(w => w.IssueIdentifier == "2000" &&
                        w.Status != WorkItemStatus.Failed &&
                        w.Status != WorkItemStatus.Cancelled)
            .ToListAsync();
        Assert.True(activeItems.Count <= 1,
            $"Expected at most 1 active WorkItem, got {activeItems.Count}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // C4: Simultaneous job completion + heartbeat timeout — only one finalizes
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Race_CompletionAndHeartbeatTimeout_OnlyOneFinalizes()
    {
        // Arrange: configure short heartbeat timeout
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            HeartbeatTimeoutSeconds = 2,
            AgentDisconnectGracePeriod = TimeSpan.FromSeconds(1),
            HeartbeatSweepIntervalSeconds = 5
        }, CancellationToken.None);

        await SeedIssueAndProfileAsync("2001", "Completion vs timeout race");
        await using var agent = new FakeAgentClient("race-agent-c4", "race-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("2001");
        Assert.True(result.Success);
        var workItemId = Guid.Parse(result.WorkItemId!);

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await agent.AcceptJobAsync(assignment.JobId);

        // Stop sending heartbeats (let timeout begin) but immediately complete the job
        // This creates a race: HeartbeatMonitor may try to fail the run at the same time
        // the completion path processes it.
        await Task.Delay(TimeSpan.FromSeconds(3)); // Let heartbeat go stale

        // Complete the job — this races with HeartbeatMonitor sweep
        await agent.AcceptAndCompleteJobAsync(assignment.JobId);

        // Assert: WorkItem reaches a terminal state (either Succeeded or Failed — not both)
        await WaitUntilAsync(async () =>
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
            return item?.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed;
        }, TimeSpan.FromSeconds(20));

        // Assert: exactly one history entry (no double-persist from racing paths)
        await Task.Delay(TimeSpan.FromSeconds(2)); // Allow any trailing async work to complete
        var historyEntries = (await Fixture.HistoryService.GetRunHistoryAsync())
            .Where(r => r.IssueIdentifier == "2001")
            .ToList();
        Assert.Single(historyEntries);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // C8: RunLifecycleManager double-finalize from two threads
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Race_DoubleFinalize_ConcurrentFailAndComplete_OnlyOneWins()
    {
        // Arrange
        await SeedIssueAndProfileAsync("2002", "Double finalize race");
        await using var agent = new FakeAgentClient("race-agent-c8", "race-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("2002");
        Assert.True(result.Success);
        var workItemId = Guid.Parse(result.WorkItemId!);

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await agent.AcceptJobAsync(assignment.JobId);

        // Act: race FailRunAsync against CompleteRunAsync from two threads
        var lifecycleManager = Fixture.Factory.Services.GetRequiredService<IRunLifecycleManager>();

        var failTask = Task.Run(() => lifecycleManager.FailRunAsync(
            assignment.JobId, "Heartbeat timeout", CancellationToken.None));
        var completeTask = Task.Run(() => lifecycleManager.CompleteRunAsync(
            assignment.JobId, WorkItemStatus.Succeeded, CancellationToken.None));

        var results2 = await Task.WhenAll(failTask, completeTask);

        // Assert: exactly one returned a non-null run (the winner), one returned null (the loser)
        var nonNullResults = results2.Where(r => r is not null).ToList();
        Assert.Single(nonNullResults);

        // Assert: WorkItem is in exactly one terminal state
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        Assert.NotNull(item);
        Assert.True(item.Status is WorkItemStatus.Succeeded or WorkItemStatus.Failed,
            $"Expected terminal state, got {item.Status}");

        // Assert: history has exactly one entry
        await Task.Delay(TimeSpan.FromSeconds(1));
        var history = (await Fixture.HistoryService.GetRunHistoryAsync())
            .Where(r => r.IssueIdentifier == "2002")
            .ToList();
        Assert.Single(history);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // C12: Concurrent agent registration — all appear in registry
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Race_ConcurrentAgentRegistration_AllAppearInRegistry()
    {
        // Act: register 5 agents concurrently
        var agents = new List<FakeAgentClient>();
        var connectTasks = new List<Task>();

        for (var i = 0; i < 5; i++)
        {
            var agent = new FakeAgentClient($"race-concurrent-{i}", "race-e2e");
            agents.Add(agent);
            connectTasks.Add(agent.ConnectAsync(BaseUrl, Fixture.ApiKey));
        }

        await Task.WhenAll(connectTasks);

        try
        {
            // Assert: all 5 agents appear in the registry
            var registry = Fixture.Factory.Services.GetRequiredService<AgentRegistryService>();
            var allAgents = registry.GetAllAgents();

            for (var i = 0; i < 5; i++)
            {
                var entry = registry.GetByAgentId($"race-concurrent-{i}");
                Assert.NotNull(entry);
                Assert.Equal(AgentStatus.Idle, entry.Status);
            }

            // Assert: all are connected
            Assert.True(agents.All(a => a.IsConnected));
        }
        finally
        {
            foreach (var agent in agents)
                await agent.DisposeAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // C13: Queue ordering under concurrent drain — FIFO preserved
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Race_QueueOrdering_JobsDispatchedInFIFOOrder()
    {
        // Arrange: seed 3 issues and dispatch them in order WITHOUT agents
        await SeedIssueAndProfileAsync("2010", "FIFO first");
        await SeedIssueAndProfileAsync("2011", "FIFO second");
        await SeedIssueAndProfileAsync("2012", "FIFO third");

        // Dispatch in strict order — all go to Pending
        var r1 = await DispatchIssueAsync("2010");
        await Task.Delay(50); // Small delay to ensure CreatedAt ordering in DB
        var r2 = await DispatchIssueAsync("2011");
        await Task.Delay(50);
        var r3 = await DispatchIssueAsync("2012");

        Assert.True(r1.Success && r1.Queued);
        Assert.True(r2.Success && r2.Queued);
        Assert.True(r3.Success && r3.Queued);

        // Connect 2 agents simultaneously — drain service distributes
        await using var agent1 = new FakeAgentClient("race-fifo-1", "race-e2e");
        await using var agent2 = new FakeAgentClient("race-fifo-2", "race-e2e");
        await agent1.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await agent2.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Wait for both to receive jobs
        var job1 = await agent1.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var job2 = await agent2.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Assert: the first two issues dispatched (2010 and 2011) are the ones delivered
        // (FIFO by CreatedAt). The third (2012) remains Pending.
        var deliveredIssues = new HashSet<string> { job1.IssueIdentifier, job2.IssueIdentifier };
        Assert.Contains("2010", deliveredIssues);
        Assert.Contains("2011", deliveredIssues);

        // Third issue should still be Pending
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var thirdItem = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IssueIdentifier == "2012");
        Assert.NotNull(thirdItem);
        Assert.Equal(WorkItemStatus.Pending, thirdItem.Status);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // C2: Agent becomes idle during drain — job picked up within interval
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Race_AgentBecomesIdleDuringDrain_PendingJobPickedUp()
    {
        // Arrange: seed 2 issues, connect 1 agent
        await SeedIssueAndProfileAsync("2020", "Drain pickup first");
        await SeedIssueAndProfileAsync("2021", "Drain pickup second");

        await using var agent = new FakeAgentClient("race-drain-idle", "race-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Dispatch first job — agent receives it immediately
        var r1 = await DispatchIssueAsync("2020");
        Assert.True(r1.Success);
        var job1 = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Dispatch second job while agent is busy — goes to Pending
        var r2 = await DispatchIssueAsync("2021");
        Assert.True(r2.Success);
        // May or may not be queued depending on timing, but agent is busy so can't receive

        // Agent completes first job → becomes Idle
        await agent.AcceptAndCompleteJobAsync(job1.JobId);
        agent.ResetJobAssigned();

        // Assert: drain service picks up second job and delivers to now-idle agent
        var job2 = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("2021", job2.IssueIdentifier);

        // Complete and verify terminal state
        await agent.AcceptAndCompleteJobAsync(job2.JobId);
        await WaitForWorkItemStatusAsync(
            Guid.Parse(r2.WorkItemId!), WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // C10: WorkItem already terminal — agent reports completion, no crash
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Race_WorkItemAlreadyFailed_AgentReportsCompletion_NoCrash()
    {
        // Arrange: dispatch and immediately fail via lifecycle manager (simulating heartbeat timeout)
        await SeedIssueAndProfileAsync("2030", "Already failed issue");
        await using var agent = new FakeAgentClient("race-terminal-agent", "race-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("2030");
        Assert.True(result.Success);
        var workItemId = Guid.Parse(result.WorkItemId!);

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await agent.AcceptJobAsync(assignment.JobId);

        // Force-fail the run via lifecycle manager (simulating heartbeat sweep racing)
        var lifecycleManager = Fixture.Factory.Services.GetRequiredService<IRunLifecycleManager>();
        var failedRun = await lifecycleManager.FailRunAsync(
            assignment.JobId, "Forced failure by test", CancellationToken.None);
        Assert.NotNull(failedRun);

        // Verify WorkItem is already Failed
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(5));

        // Act: agent (unaware of the failure) tries to report completion
        // This should NOT crash the server — it should be a no-op or graceful rejection
        await agent.AcceptAndCompleteJobAsync(assignment.JobId);

        // Assert: no crash — server is still responsive
        // Verify by connecting another agent (proves hub is alive)
        await using var probeAgent = new FakeAgentClient("race-probe-agent", "race-e2e");
        await probeAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);
        Assert.True(probeAgent.IsConnected);

        // Assert: WorkItem remains Failed (not overwritten to Succeeded)
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Failed, item.Status);

        // Assert: history still has exactly one entry (no double-write)
        var history = (await Fixture.HistoryService.GetRunHistoryAsync())
            .Where(r => r.IssueIdentifier == "2030")
            .ToList();
        Assert.Single(history);
        Assert.Equal(PipelineStep.Failed, history[0].FinalStep);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Additional: Rapid sequential dispatch-complete cycles don't leak state
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Race_RapidDispatchCompleteCycles_NoStateLeaks()
    {
        // Arrange: run 5 dispatch→complete cycles rapidly on the same agent
        await using var agent = new FakeAgentClient("race-rapid-agent", "race-e2e");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        for (var i = 0; i < 5; i++)
        {
            var issueId = $"2100-{i}";
            await SeedIssueAndProfileAsync(issueId, $"Rapid cycle {i}");

            var result = await DispatchIssueAsync(issueId);
            Assert.True(result.Success, $"Cycle {i} dispatch failed: {result.ErrorMessage}");

            var job = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(issueId, job.IssueIdentifier);

            await agent.AcceptAndCompleteJobAsync(job.JobId);

            // Wait for completion to propagate before next cycle
            await WaitForWorkItemStatusAsync(
                Guid.Parse(result.WorkItemId!), WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

            agent.ResetJobAssigned();
        }

        // Assert: agent is Idle after all cycles (no leaked Busy state)
        var registry = Fixture.Factory.Services.GetRequiredService<AgentRegistryService>();
        await WaitUntilAsync(
            () => registry.GetByAgentId("race-rapid-agent")?.Status == AgentStatus.Idle,
            TimeSpan.FromSeconds(5));

        // Assert: all 5 runs in history
        var history = (await Fixture.HistoryService.GetRunHistoryAsync())
            .Where(r => r.IssueIdentifier.StartsWith("2100-"))
            .ToList();
        Assert.Equal(5, history.Count);
        Assert.All(history, h => Assert.Equal(PipelineStep.Completed, h.FinalStep));

        // Assert: no orphaned in-memory runs
        var runService = Fixture.Factory.Services.GetRequiredService<IOrchestratorRunService>();
        var activeRuns = runService.GetActiveRuns();
        var leakedRuns = activeRuns.Where(r => r.IssueIdentifier.Value.StartsWith("2100-")).ToList();
        Assert.Empty(leakedRuns);
    }
}
