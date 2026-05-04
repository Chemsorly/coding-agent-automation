using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="JobDispatcherService"/>.
/// </summary>
public class JobDispatcherServiceTests
{
    private static AgentRegistryService CreateRegistry() =>
        new(new Mock<ILogger>().Object);

    private static JobDispatcherService CreateService(AgentRegistryService? registry = null) =>
        new(registry ?? CreateRegistry(), new Mock<ILogger>().Object);

    private static PendingJob CreateJob(string issueId = "issue-1", IReadOnlyList<string>? labels = null) => new()
    {
        IssueIdentifier = issueId,
        IssueProviderId = "ip",
        RepoProviderId = "rp",
        EnqueuedAt = DateTimeOffset.UtcNow,
        InitiatedBy = "test",
        RequiredLabels = labels ?? Array.Empty<string>()
    };

    private static AgentEntry CreateAgent(string agentId = "agent-1", IReadOnlyList<string>? labels = null) => new()
    {
        AgentId = agentId,
        ConnectionId = $"conn-{agentId}",
        Hostname = "host",
        AgentType = "kiro-dotnet",
        Labels = labels ?? new[] { "kiro", "dotnet" },
        RegisteredAt = DateTimeOffset.UtcNow,
        LastHeartbeatAt = DateTimeOffset.UtcNow
    };

    #region EnqueueJob

    [Fact]
    public void EnqueueJob_NewJob_ReturnsTrue()
    {
        var service = CreateService();
        service.EnqueueJob(CreateJob()).Should().BeTrue();
    }

    [Fact]
    public void EnqueueJob_DuplicateIssue_ReturnsFalse()
    {
        var service = CreateService();
        service.EnqueueJob(CreateJob("issue-1")).Should().BeTrue();
        service.EnqueueJob(CreateJob("issue-1")).Should().BeFalse();
    }

    [Fact]
    public void EnqueueJob_DifferentIssues_BothSucceed()
    {
        var service = CreateService();
        service.EnqueueJob(CreateJob("issue-1")).Should().BeTrue();
        service.EnqueueJob(CreateJob("issue-2")).Should().BeTrue();
        service.QueueLength.Should().Be(2);
    }

    [Fact]
    public void EnqueueJob_NullJob_Throws()
    {
        var service = CreateService();
        var act = () => service.EnqueueJob(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region QueueLength

    [Fact]
    public void QueueLength_EmptyQueue_ReturnsZero()
    {
        var service = CreateService();
        service.QueueLength.Should().Be(0);
    }

    [Fact]
    public void QueueLength_AfterEnqueue_ReflectsCount()
    {
        var service = CreateService();
        service.EnqueueJob(CreateJob("a"));
        service.EnqueueJob(CreateJob("b"));
        service.QueueLength.Should().Be(2);
    }

    #endregion

    #region IsIssueQueued

    [Fact]
    public void IsIssueQueued_QueuedIssue_ReturnsTrue()
    {
        var service = CreateService();
        service.EnqueueJob(CreateJob("issue-1"));
        service.IsIssueQueued("issue-1").Should().BeTrue();
    }

    [Fact]
    public void IsIssueQueued_NotQueued_ReturnsFalse()
    {
        var service = CreateService();
        service.IsIssueQueued("issue-1").Should().BeFalse();
    }

    [Fact]
    public void IsIssueQueued_NullIdentifier_Throws()
    {
        var service = CreateService();
        var act = () => service.IsIssueQueued(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region MarkIssueComplete

    [Fact]
    public void MarkIssueComplete_RemovesFromProcessing()
    {
        var service = CreateService();
        service.EnqueueJob(CreateJob("issue-1"));
        service.IsIssueQueued("issue-1").Should().BeTrue();

        service.MarkIssueComplete("issue-1");
        service.IsIssueQueued("issue-1").Should().BeFalse();
    }

    [Fact]
    public void MarkIssueComplete_NonExistentIssue_DoesNotThrow()
    {
        var service = CreateService();
        var act = () => service.MarkIssueComplete("nonexistent");
        act.Should().NotThrow();
    }

    [Fact]
    public void MarkIssueComplete_AllowsReEnqueue()
    {
        var service = CreateService();
        service.EnqueueJob(CreateJob("issue-1"));
        service.MarkIssueComplete("issue-1");
        service.EnqueueJob(CreateJob("issue-1")).Should().BeTrue();
    }

    #endregion

    #region RemoveFromQueue

    [Fact]
    public void RemoveFromQueue_ExistingJob_ReturnsTrue()
    {
        var service = CreateService();
        service.EnqueueJob(CreateJob("issue-1"));
        service.RemoveFromQueue("issue-1").Should().BeTrue();
    }

    [Fact]
    public void RemoveFromQueue_NonExistentJob_ReturnsFalse()
    {
        var service = CreateService();
        service.RemoveFromQueue("nonexistent").Should().BeFalse();
    }

    [Fact]
    public void RemoveFromQueue_RemovesFromQueueAndDedup()
    {
        var service = CreateService();
        service.EnqueueJob(CreateJob("issue-1"));
        service.RemoveFromQueue("issue-1");

        service.QueueLength.Should().Be(0);
        service.IsIssueQueued("issue-1").Should().BeFalse();
    }

    [Fact]
    public void RemoveFromQueue_PreservesOtherJobs()
    {
        var service = CreateService();
        service.EnqueueJob(CreateJob("issue-1"));
        service.EnqueueJob(CreateJob("issue-2"));
        service.EnqueueJob(CreateJob("issue-3"));

        service.RemoveFromQueue("issue-2");

        service.QueueLength.Should().Be(2);
        service.IsIssueQueued("issue-1").Should().BeTrue();
        service.IsIssueQueued("issue-3").Should().BeTrue();
    }

    #endregion

    #region SelectAgent

    [Fact]
    public void SelectAgent_NoIdleAgents_ReturnsNull()
    {
        var registry = CreateRegistry();
        var service = CreateService(registry);

        service.SelectAgent(Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void SelectAgent_EmptyLabels_MatchesAnyAgent()
    {
        var registry = CreateRegistry();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            AgentType = "kiro",
            Labels = new[] { "dotnet" }
        }, "conn-1");

        var service = CreateService(registry);
        var agent = service.SelectAgent(Array.Empty<string>());
        agent.Should().NotBeNull();
        agent!.AgentId.Should().Be("agent-1");
    }

    [Fact]
    public void SelectAgent_MatchingLabels_ReturnsAgent()
    {
        var registry = CreateRegistry();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            AgentType = "kiro",
            Labels = new[] { "dotnet", "linux" }
        }, "conn-1");

        var service = CreateService(registry);
        var agent = service.SelectAgent(new[] { "dotnet" });
        agent.Should().NotBeNull();
    }

    [Fact]
    public void SelectAgent_NonMatchingLabels_ReturnsNull()
    {
        var registry = CreateRegistry();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            AgentType = "kiro",
            Labels = new[] { "python" }
        }, "conn-1");

        var service = CreateService(registry);
        var agent = service.SelectAgent(new[] { "dotnet" });
        agent.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_NullLabels_Throws()
    {
        var service = CreateService();
        var act = () => service.SelectAgent(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region DequeueForAgent

    [Fact]
    public void DequeueForAgent_CompatibleJob_ReturnsJob()
    {
        var registry = CreateRegistry();
        var service = CreateService(registry);
        var agent = CreateAgent(labels: new[] { "dotnet", "linux" });

        service.EnqueueJob(CreateJob("issue-1", labels: new[] { "dotnet" }));

        var job = service.DequeueForAgent(agent);
        job.Should().NotBeNull();
        job!.IssueIdentifier.Should().Be("issue-1");
    }

    [Fact]
    public void DequeueForAgent_NoCompatibleJob_ReturnsNull()
    {
        var service = CreateService();
        var agent = CreateAgent(labels: new[] { "python" });

        service.EnqueueJob(CreateJob("issue-1", labels: new[] { "dotnet" }));

        service.DequeueForAgent(agent).Should().BeNull();
    }

    [Fact]
    public void DequeueForAgent_EmptyQueue_ReturnsNull()
    {
        var service = CreateService();
        var agent = CreateAgent();
        service.DequeueForAgent(agent).Should().BeNull();
    }

    [Fact]
    public void DequeueForAgent_SkipsIncompatibleAndFindsCompatible()
    {
        var service = CreateService();
        var agent = CreateAgent(labels: new[] { "dotnet", "linux" });

        service.EnqueueJob(CreateJob("issue-python", labels: new[] { "python" }));
        service.EnqueueJob(CreateJob("issue-dotnet", labels: new[] { "dotnet" }));

        var job = service.DequeueForAgent(agent);
        job.Should().NotBeNull();
        job!.IssueIdentifier.Should().Be("issue-dotnet");

        // The incompatible job should still be in the queue
        service.QueueLength.Should().Be(1);
    }

    [Fact]
    public void DequeueForAgent_JobWithNoLabels_MatchesAnyAgent()
    {
        var service = CreateService();
        var agent = CreateAgent(labels: new[] { "anything" });

        service.EnqueueJob(CreateJob("issue-1", labels: Array.Empty<string>()));

        var job = service.DequeueForAgent(agent);
        job.Should().NotBeNull();
    }

    [Fact]
    public void DequeueForAgent_NullAgent_Throws()
    {
        var service = CreateService();
        var act = () => service.DequeueForAgent(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region GetQueuedJobs

    [Fact]
    public void GetQueuedJobs_ReturnsSnapshot()
    {
        var service = CreateService();
        service.EnqueueJob(CreateJob("issue-1"));
        service.EnqueueJob(CreateJob("issue-2"));

        var jobs = service.GetQueuedJobs();
        jobs.Should().HaveCount(2);
    }

    [Fact]
    public void GetQueuedJobs_EmptyQueue_ReturnsEmpty()
    {
        var service = CreateService();
        service.GetQueuedJobs().Should().BeEmpty();
    }

    #endregion

    #region Concurrency Tests

    [Fact]
    public async Task ConcurrentEnqueueAndDequeue_MaintainsConsistency()
    {
        var registry = CreateRegistry();
        var service = CreateService(registry);
        var agent = CreateAgent(labels: new[] { "dotnet", "linux", "python" });

        const int enqueueCount = 50;

        // Enqueue jobs concurrently
        var enqueueTasks = Enumerable.Range(0, enqueueCount)
            .Select(i => Task.Run(() => service.EnqueueJob(CreateJob($"issue-{i}", labels: Array.Empty<string>()))))
            .ToArray();

        await Task.WhenAll(enqueueTasks);

        // All should have been enqueued (unique issue identifiers)
        var enqueuedCount = enqueueTasks.Count(t => t.Result);
        enqueuedCount.Should().Be(enqueueCount);

        // Dequeue concurrently from multiple threads
        var dequeuedJobs = new System.Collections.Concurrent.ConcurrentBag<PendingJob>();
        var dequeueTasks = Enumerable.Range(0, enqueueCount)
            .Select(_ => Task.Run(() =>
            {
                var job = service.DequeueForAgent(agent);
                if (job != null)
                    dequeuedJobs.Add(job);
            }))
            .ToArray();

        await Task.WhenAll(dequeueTasks);

        // All dequeued jobs should have unique issue identifiers (no duplicates)
        dequeuedJobs.Select(j => j.IssueIdentifier).Should().OnlyHaveUniqueItems();

        // Total dequeued + remaining in queue should equal original count
        (dequeuedJobs.Count + service.QueueLength).Should().Be(enqueueCount);
    }

    #endregion

    #region ResolveRequiredLabels

    [Fact]
    public void ResolveRequiredLabels_RepoConfigHasLabels_ReturnsRepoLabels()
    {
        var repoConfig = new ProviderConfig
        {
            Id = "repo",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingsKeys.RequiredAgentLabels] = "dotnet, linux"
            }
        };
        var pipelineConfig = new PipelineConfiguration();

        var labels = JobDispatcherService.ResolveRequiredLabels(repoConfig, pipelineConfig);
        labels.Should().BeEquivalentTo(new[] { "dotnet", "linux" });
    }

    [Fact]
    public void ResolveRequiredLabels_NoRepoLabels_FallsToPipelineDefault()
    {
        var repoConfig = new ProviderConfig
        {
            Id = "repo",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>()
        };
        var pipelineConfig = new PipelineConfiguration
        {
            Agent = new AgentConfiguration { DefaultRequiredAgentLabels = "kiro, agent" }
        };

        var labels = JobDispatcherService.ResolveRequiredLabels(repoConfig, pipelineConfig);
        labels.Should().BeEquivalentTo(new[] { "kiro", "agent" });
    }

    [Fact]
    public void ResolveRequiredLabels_NoLabelsAnywhere_ReturnsEmpty()
    {
        var repoConfig = new ProviderConfig
        {
            Id = "repo",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>()
        };
        var pipelineConfig = new PipelineConfiguration();

        var labels = JobDispatcherService.ResolveRequiredLabels(repoConfig, pipelineConfig);
        labels.Should().BeEmpty();
    }

    [Fact]
    public void ResolveRequiredLabels_NullRepoConfig_FallsToPipelineDefault()
    {
        var pipelineConfig = new PipelineConfiguration
        {
            Agent = new AgentConfiguration { DefaultRequiredAgentLabels = "default-label" }
        };

        var labels = JobDispatcherService.ResolveRequiredLabels(null, pipelineConfig);
        labels.Should().BeEquivalentTo(new[] { "default-label" });
    }

    #endregion
}
