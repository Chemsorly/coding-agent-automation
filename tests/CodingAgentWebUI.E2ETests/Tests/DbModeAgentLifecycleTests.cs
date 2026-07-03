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
/// DB-mode E2E agent lifecycle tests: registration, label routing, FIFO selection,
/// disabled agents, heartbeat keepalive, duplicate AgentId handling.
/// Validates the multi-agent orchestration plumbing that routes jobs correctly.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "DbMode")]
[Trait("Feature", "AgentLifecycle")]
public sealed class DbModeAgentLifecycleTests : DbModeE2ETestBase, IClassFixture<DbModeE2EFixture>
{
    public DbModeAgentLifecycleTests(DbModeE2EFixture fixture) : base(fixture) { }

    private async Task SeedIssueAsync(string issueId, string title = "Lifecycle test issue")
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = title,
            Description = "## Requirements\nLifecycle test\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });
    }

    private async Task EnsureTemplateAndProfileAsync(string profileId, string[] matchLabels)
    {
        var templates = await Fixture.ConfigStore.LoadAllTemplatesAsync(CancellationToken.None);
        if (!templates.Any(t => t.Id == "template-lifecycle-e2e"))
        {
            await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
            {
                Id = "template-lifecycle-e2e",
                Name = "Lifecycle Template",
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                Enabled = true
            }, CancellationToken.None);
        }

        var profiles = await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None);
        if (!profiles.Any(p => p.Id == profileId))
        {
            await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
            {
                Id = profileId,
                DisplayName = $"Profile {profileId}",
                MatchLabels = matchLabels,
                AgentProviderConfigId = "agent-e2e",
                Enabled = true
            }, CancellationToken.None);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // D1: Agent registration — connects, appears in registry with Idle + labels
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AgentLifecycle_Registration_AppearsInRegistryWithIdleAndLabels()
    {
        // Act
        await using var agent = new FakeAgentClient("lifecycle-reg-1", "dotnet", "linux");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Assert
        var registry = Fixture.Factory.Services.GetRequiredService<AgentRegistryService>();
        var entry = registry.GetByAgentId("lifecycle-reg-1");

        Assert.NotNull(entry);
        Assert.Equal(AgentStatus.Idle, entry.Status);
        Assert.Equal("lifecycle-reg-1", entry.AgentId);
        Assert.Contains("dotnet", entry.Labels);
        Assert.Contains("linux", entry.Labels);
        Assert.True(agent.IsConnected);
    }

    [Fact]
    public async Task AgentLifecycle_Registration_MultipleAgents_AllTracked()
    {
        // Act: register 3 agents
        await using var a1 = new FakeAgentClient("lifecycle-multi-1", "team-a");
        await using var a2 = new FakeAgentClient("lifecycle-multi-2", "team-b");
        await using var a3 = new FakeAgentClient("lifecycle-multi-3", "team-a", "team-b");
        await a1.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await a2.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await a3.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Assert
        var registry = Fixture.Factory.Services.GetRequiredService<AgentRegistryService>();
        var all = registry.GetAllAgents();

        Assert.Contains(all, a => a.AgentId == "lifecycle-multi-1");
        Assert.Contains(all, a => a.AgentId == "lifecycle-multi-2");
        Assert.Contains(all, a => a.AgentId == "lifecycle-multi-3");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // D2: Label-based routing — agent only receives matching jobs
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AgentLifecycle_LabelRouting_OnlyMatchingAgentReceivesJob()
    {
        // Arrange: profile requires "dotnet-special" label
        await EnsureTemplateAndProfileAsync("profile-label-routing", new[] { "dotnet-special" });
        await SeedIssueAsync("3000", "Label routing issue");

        // Create repo provider with required labels
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-label-routing",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Label Routing Repo",
            RequiredLabels = new[] { "dotnet-special" }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-label-routing",
            Name = "Label Routing Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-label-routing",
            Enabled = true
        }, CancellationToken.None);

        // Connect two agents: one matching, one not
        await using var matchingAgent = new FakeAgentClient("lifecycle-match", "dotnet-special");
        await using var otherAgent = new FakeAgentClient("lifecycle-other", "python");
        await matchingAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await otherAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: dispatch with label requirement
        var orchService = Fixture.Factory.Services.GetRequiredService<IDispatchOrchestrationService>();
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        var project = await Fixture.ConfigStore.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);

        var request = await orchService.PrepareDistributionRequestAsync(
            "3000", "issue-e2e", "repo-label-routing", null, null, "label-test",
            project!, ct: CancellationToken.None);
        Assert.NotNull(request);
        await distributor.DistributeAsync(request, CancellationToken.None);

        // Assert: matching agent receives job
        var job = await matchingAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("3000", job.IssueIdentifier);

        // Assert: non-matching agent does NOT receive job
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Empty(otherAgent.ReceivedJobIds);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // D3: Multi-agent label routing — each gets matching jobs only
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AgentLifecycle_MultiAgentLabelRouting_EachGetsOwnJobs()
    {
        // Arrange: two profiles with different labels
        await EnsureTemplateAndProfileAsync("profile-frontend", new[] { "frontend" });
        await EnsureTemplateAndProfileAsync("profile-backend", new[] { "backend" });

        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-frontend",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Frontend Repo",
            RequiredLabels = new[] { "frontend" }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-backend",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Backend Repo",
            RequiredLabels = new[] { "backend" }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-frontend",
            Name = "Frontend Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-frontend",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-backend",
            Name = "Backend Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-backend",
            Enabled = true
        }, CancellationToken.None);

        await SeedIssueAsync("3010", "Frontend issue");
        await SeedIssueAsync("3011", "Backend issue");

        // Connect agents with different labels
        await using var frontendAgent = new FakeAgentClient("lifecycle-frontend", "frontend");
        await using var backendAgent = new FakeAgentClient("lifecycle-backend", "backend");
        await frontendAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await backendAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var orchService = Fixture.Factory.Services.GetRequiredService<IDispatchOrchestrationService>();
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        var project = await Fixture.ConfigStore.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);

        // Dispatch frontend issue
        var reqFrontend = await orchService.PrepareDistributionRequestAsync(
            "3010", "issue-e2e", "repo-frontend", null, null, "routing-test",
            project!, ct: CancellationToken.None);
        Assert.NotNull(reqFrontend);
        await distributor.DistributeAsync(reqFrontend, CancellationToken.None);

        // Dispatch backend issue
        var reqBackend = await orchService.PrepareDistributionRequestAsync(
            "3011", "issue-e2e", "repo-backend", null, null, "routing-test",
            project!, ct: CancellationToken.None);
        Assert.NotNull(reqBackend);
        await distributor.DistributeAsync(reqBackend, CancellationToken.None);

        // Assert: each agent gets its own job
        var frontendJob = await frontendAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var backendJob = await backendAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("3010", frontendJob.IssueIdentifier);
        Assert.Equal("3011", backendJob.IssueIdentifier);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // D4: Disabled agent not selected — skipped even if idle
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AgentLifecycle_DisabledAgent_NotSelectedForDispatch()
    {
        // Arrange
        await EnsureTemplateAndProfileAsync("profile-disabled-test", new[] { "disabled-test" });
        await SeedIssueAsync("3020", "Disabled agent issue");

        // Connect two agents
        await using var disabledAgent = new FakeAgentClient("lifecycle-disabled", "disabled-test");
        await using var enabledAgent = new FakeAgentClient("lifecycle-enabled", "disabled-test");
        await disabledAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await enabledAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Disable the first agent via registry
        var registry = Fixture.Factory.Services.GetRequiredService<AgentRegistryService>();
        var entry = registry.GetByAgentId("lifecycle-disabled");
        Assert.NotNull(entry);
        entry.Disabled = true;

        // Act: dispatch
        var result = await DispatchIssueAsync("3020");
        Assert.True(result.Success);

        // Assert: enabled agent receives the job (disabled is skipped)
        var job = await enabledAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("3020", job.IssueIdentifier);

        // Disabled agent should NOT have received anything
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Empty(disabledAgent.ReceivedJobIds);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // D5: Heartbeat keeps alive — never marked Disconnected
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AgentLifecycle_HeartbeatKeepsAlive_NeverDisconnected()
    {
        // Arrange: configure aggressive heartbeat timeout
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            HeartbeatTimeoutSeconds = 3,
            HeartbeatSweepIntervalSeconds = 5
        }, CancellationToken.None);

        await using var agent = new FakeAgentClient("lifecycle-heartbeat", "disabled-test");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var registry = Fixture.Factory.Services.GetRequiredService<AgentRegistryService>();

        // Act: send heartbeats faster than the timeout, wait through multiple sweep cycles
        for (var i = 0; i < 4; i++)
        {
            await agent.SendHeartbeatAsync();
            await Task.Delay(TimeSpan.FromSeconds(2)); // Within 3s timeout
        }

        // Assert: agent is still Idle (heartbeats prevented Disconnected transition)
        var entry = registry.GetByAgentId("lifecycle-heartbeat");
        Assert.NotNull(entry);
        Assert.Equal(AgentStatus.Idle, entry.Status);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // D8: Agent FIFO selection — longest-idle agent receives next job
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AgentLifecycle_FIFOSelection_LongestIdleAgentGetsJob()
    {
        // Arrange
        await EnsureTemplateAndProfileAsync("profile-fifo-selection", new[] { "fifo-sel" });
        await SeedIssueAsync("3030", "FIFO selection issue 1");
        await SeedIssueAsync("3031", "FIFO selection issue 2");

        // Connect agent A first, then B — A has been idle longer
        await using var agentA = new FakeAgentClient("lifecycle-fifo-a", "fifo-sel");
        await agentA.ConnectAsync(BaseUrl, Fixture.ApiKey);
        await Task.Delay(TimeSpan.FromMilliseconds(100)); // Ensure ordering

        await using var agentB = new FakeAgentClient("lifecycle-fifo-b", "fifo-sel");
        await agentB.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: dispatch first issue
        var r1 = await DispatchIssueAsync("3030");
        Assert.True(r1.Success);

        // Assert: agent A (longest idle) should get the first job
        var jobA = await agentA.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("3030", jobA.IssueIdentifier);

        // Verify B didn't get it
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Empty(agentB.ReceivedJobIds);

        // Dispatch second issue — B is now the only idle agent
        var r2 = await DispatchIssueAsync("3031");
        Assert.True(r2.Success);

        var jobB = await agentB.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("3031", jobB.IssueIdentifier);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // D9: Busy agent not selected — ignored for new dispatch
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AgentLifecycle_BusyAgent_NotSelectedForNewDispatch()
    {
        // Arrange
        await EnsureTemplateAndProfileAsync("profile-busy-test", new[] { "busy-test" });
        await SeedIssueAsync("3040", "First job for busy test");
        await SeedIssueAsync("3041", "Second job while busy");

        await using var agent = new FakeAgentClient("lifecycle-busy", "busy-test");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Dispatch first job — agent becomes busy
        var r1 = await DispatchIssueAsync("3040");
        Assert.True(r1.Success);
        var job1 = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await agent.AcceptJobAsync(job1.JobId);

        // Agent is now Busy — verify
        var registry = Fixture.Factory.Services.GetRequiredService<AgentRegistryService>();
        await WaitUntilAsync(
            () => registry.GetByAgentId("lifecycle-busy")?.Status == AgentStatus.Busy,
            TimeSpan.FromSeconds(5));

        // Act: dispatch second job — should go to Pending (no idle agent)
        var r2 = await DispatchIssueAsync("3041");
        Assert.True(r2.Success);
        Assert.True(r2.Queued, "Second job should be queued — agent is busy");

        // Verify WorkItem is Pending
        var workItemId2 = Guid.Parse(r2.WorkItemId!);
        var pending = await WaitForWorkItemStatusAsync(workItemId2, WorkItemStatus.Pending, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemStatus.Pending, pending.Status);

        // Complete first job → agent becomes Idle → drain picks up second job
        await agent.AcceptAndCompleteJobAsync(job1.JobId);
        agent.ResetJobAssigned();

        var job2 = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("3041", job2.IssueIdentifier);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // D10: Agent ForceDisconnect on duplicate ID — second forces first off
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AgentLifecycle_DuplicateAgentId_FirstConnectionForced()
    {
        // Arrange: connect first agent
        var firstAgent = new FakeAgentClient("lifecycle-dup-id", "dup-test");
        await firstAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);
        Assert.True(firstAgent.IsConnected);

        // Verify first agent is registered
        var registry = Fixture.Factory.Services.GetRequiredService<AgentRegistryService>();
        var entry1 = registry.GetByAgentId("lifecycle-dup-id");
        Assert.NotNull(entry1);

        // Act: connect second agent with SAME AgentId
        await using var secondAgent = new FakeAgentClient("lifecycle-dup-id", "dup-test");
        await secondAgent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Wait for ForceDisconnect to propagate to first agent
        await WaitUntilAsync(
            () => !firstAgent.IsConnected,
            TimeSpan.FromSeconds(15));

        // Assert: first agent is disconnected
        Assert.False(firstAgent.IsConnected);

        // Assert: second agent is connected and in registry
        Assert.True(secondAgent.IsConnected);
        var entry2 = registry.GetByAgentId("lifecycle-dup-id");
        Assert.NotNull(entry2);
        Assert.Equal(AgentStatus.Idle, entry2.Status);

        // Cleanup
        await firstAgent.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Additional: Agent disconnect and removal from idle pool
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AgentLifecycle_AgentDisconnects_RemovedFromIdlePool()
    {
        // Arrange: configure short heartbeat
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            HeartbeatTimeoutSeconds = 2,
            HeartbeatSweepIntervalSeconds = 5
        }, CancellationToken.None);

        var agent = new FakeAgentClient("lifecycle-disconnect-pool", "pool-test");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var registry = Fixture.Factory.Services.GetRequiredService<AgentRegistryService>();
        Assert.Contains(registry.GetIdleAgents(), a => a.AgentId == "lifecycle-disconnect-pool");

        // Act: disconnect
        await agent.DisposeAsync();

        // Wait for heartbeat sweep to detect disconnect
        await WaitUntilAsync(
            () => !registry.GetIdleAgents().Any(a => a.AgentId == "lifecycle-disconnect-pool"),
            TimeSpan.FromSeconds(15));

        // Assert: agent no longer in idle pool
        Assert.DoesNotContain(registry.GetIdleAgents(), a => a.AgentId == "lifecycle-disconnect-pool");

        // Assert: agent is Disconnected (not removed entirely — grace period)
        var entry = registry.GetByAgentId("lifecycle-disconnect-pool");
        if (entry is not null)
            Assert.Equal(AgentStatus.Disconnected, entry.Status);
    }
}
