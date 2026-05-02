using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Extended unit tests for JobDispatcherService — covers queue operations,
/// agent selection, label matching, and edge cases.
/// </summary>
public class JobDispatcherServiceExtendedTests
{
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;
    private readonly Mock<ILogger> _mockLogger;

    public JobDispatcherServiceExtendedTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);
    }

    // ── SelectAgent ─────────────────────────────────────────────────────

    [Fact]
    public void SelectAgent_NoIdleAgents_ReturnsNull()
    {
        var result = _dispatcher.SelectAgent(new[] { "dotnet" });

        result.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_IdleAgentWithMatchingLabels_ReturnsAgent()
    {
        RegisterAgent("agent-1", "conn-1", new[] { "dotnet", "linux" });

        var result = _dispatcher.SelectAgent(new[] { "dotnet" });

        result.Should().NotBeNull();
        result!.AgentId.Should().Be("agent-1");
    }

    [Fact]
    public void SelectAgent_IdleAgentWithoutMatchingLabels_ReturnsNull()
    {
        RegisterAgent("agent-1", "conn-1", new[] { "java" });

        var result = _dispatcher.SelectAgent(new[] { "dotnet" });

        result.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_EmptyRequiredLabels_MatchesAnyAgent()
    {
        RegisterAgent("agent-1", "conn-1", new[] { "java" });

        var result = _dispatcher.SelectAgent(Array.Empty<string>());

        result.Should().NotBeNull();
    }

    [Fact]
    public void SelectAgent_MultipleIdleAgents_SelectsLongestIdle()
    {
        var entry1 = RegisterAgent("agent-1", "conn-1", new[] { "dotnet" });
        entry1.LastJobCompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var entry2 = RegisterAgent("agent-2", "conn-2", new[] { "dotnet" });
        entry2.LastJobCompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        var result = _dispatcher.SelectAgent(new[] { "dotnet" });

        result!.AgentId.Should().Be("agent-1"); // Idle longer
    }

    [Fact]
    public void SelectAgent_DisabledAgent_Skipped()
    {
        var entry = RegisterAgent("agent-1", "conn-1", new[] { "dotnet" });
        entry.Disabled = true;

        var result = _dispatcher.SelectAgent(new[] { "dotnet" });

        result.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_BusyAgent_NotSelected()
    {
        RegisterAgent("agent-1", "conn-1", new[] { "dotnet" });
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var result = _dispatcher.SelectAgent(new[] { "dotnet" });

        result.Should().BeNull();
    }

    // ── EnqueueJob ──────────────────────────────────────────────────────

    [Fact]
    public void EnqueueJob_NewIssue_ReturnsTrue()
    {
        var job = CreatePendingJob("org/repo#1");

        var result = _dispatcher.EnqueueJob(job);

        result.Should().BeTrue();
        _dispatcher.QueueLength.Should().Be(1);
    }

    [Fact]
    public void EnqueueJob_DuplicateIssue_ReturnsFalse()
    {
        var job1 = CreatePendingJob("org/repo#1");
        var job2 = CreatePendingJob("org/repo#1");

        _dispatcher.EnqueueJob(job1);
        var result = _dispatcher.EnqueueJob(job2);

        result.Should().BeFalse();
        _dispatcher.QueueLength.Should().Be(1);
    }

    [Fact]
    public void EnqueueJob_NullJob_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _dispatcher.EnqueueJob(null!));
    }

    // ── DequeueForAgent ─────────────────────────────────────────────────

    [Fact]
    public void DequeueForAgent_CompatibleJob_ReturnsJob()
    {
        var entry = RegisterAgent("agent-1", "conn-1", new[] { "dotnet" });
        _dispatcher.EnqueueJob(CreatePendingJob("org/repo#1", requiredLabels: new[] { "dotnet" }));

        var result = _dispatcher.DequeueForAgent(entry);

        result.Should().NotBeNull();
        result!.IssueIdentifier.Should().Be("org/repo#1");
    }

    [Fact]
    public void DequeueForAgent_IncompatibleJob_ReturnsNull()
    {
        var entry = RegisterAgent("agent-1", "conn-1", new[] { "java" });
        _dispatcher.EnqueueJob(CreatePendingJob("org/repo#1", requiredLabels: new[] { "dotnet" }));

        var result = _dispatcher.DequeueForAgent(entry);

        result.Should().BeNull();
        _dispatcher.QueueLength.Should().Be(1); // Job re-enqueued
    }

    [Fact]
    public void DequeueForAgent_EmptyQueue_ReturnsNull()
    {
        var entry = RegisterAgent("agent-1", "conn-1", new[] { "dotnet" });

        var result = _dispatcher.DequeueForAgent(entry);

        result.Should().BeNull();
    }

    [Fact]
    public void DequeueForAgent_NullAgent_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _dispatcher.DequeueForAgent(null!));
    }

    // ── IsIssueQueued ───────────────────────────────────────────────────

    [Fact]
    public void IsIssueQueued_QueuedIssue_ReturnsTrue()
    {
        _dispatcher.EnqueueJob(CreatePendingJob("org/repo#1"));

        _dispatcher.IsIssueQueued("org/repo#1").Should().BeTrue();
    }

    [Fact]
    public void IsIssueQueued_NotQueued_ReturnsFalse()
    {
        _dispatcher.IsIssueQueued("org/repo#1").Should().BeFalse();
    }

    [Fact]
    public void IsIssueQueued_NullIdentifier_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _dispatcher.IsIssueQueued(null!));
    }

    // ── RemoveFromQueue ─────────────────────────────────────────────────

    [Fact]
    public void RemoveFromQueue_QueuedIssue_ReturnsTrue()
    {
        _dispatcher.EnqueueJob(CreatePendingJob("org/repo#1"));

        var result = _dispatcher.RemoveFromQueue("org/repo#1");

        result.Should().BeTrue();
        _dispatcher.QueueLength.Should().Be(0);
        _dispatcher.IsIssueQueued("org/repo#1").Should().BeFalse();
    }

    [Fact]
    public void RemoveFromQueue_NotQueued_ReturnsFalse()
    {
        var result = _dispatcher.RemoveFromQueue("org/repo#1");

        result.Should().BeFalse();
    }

    [Fact]
    public void RemoveFromQueue_NullIdentifier_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _dispatcher.RemoveFromQueue(null!));
    }

    [Fact]
    public void RemoveFromQueue_PreservesOtherJobs()
    {
        _dispatcher.EnqueueJob(CreatePendingJob("org/repo#1"));
        _dispatcher.EnqueueJob(CreatePendingJob("org/repo#2"));
        _dispatcher.EnqueueJob(CreatePendingJob("org/repo#3"));

        _dispatcher.RemoveFromQueue("org/repo#2");

        _dispatcher.QueueLength.Should().Be(2);
        _dispatcher.IsIssueQueued("org/repo#1").Should().BeTrue();
        _dispatcher.IsIssueQueued("org/repo#3").Should().BeTrue();
    }

    // ── MarkIssueComplete ───────────────────────────────────────────────

    [Fact]
    public void MarkIssueComplete_RemovesFromProcessing()
    {
        _dispatcher.EnqueueJob(CreatePendingJob("org/repo#1"));

        _dispatcher.MarkIssueComplete("org/repo#1");

        _dispatcher.IsIssueQueued("org/repo#1").Should().BeFalse();
    }

    [Fact]
    public void MarkIssueComplete_NullIdentifier_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => _dispatcher.MarkIssueComplete(null!));
    }

    // ── GetQueuedJobs ───────────────────────────────────────────────────

    [Fact]
    public void GetQueuedJobs_ReturnsSnapshot()
    {
        _dispatcher.EnqueueJob(CreatePendingJob("org/repo#1"));
        _dispatcher.EnqueueJob(CreatePendingJob("org/repo#2"));

        var jobs = _dispatcher.GetQueuedJobs();

        jobs.Should().HaveCount(2);
    }

    [Fact]
    public void GetQueuedJobs_WhenEmpty_ReturnsEmptyList()
    {
        var jobs = _dispatcher.GetQueuedJobs();

        jobs.Should().BeEmpty();
    }

    // ── ResolveRequiredLabels ────────────────────────────────────────────

    [Fact]
    public void ResolveRequiredLabels_NullRepoConfig_UsesDefaults()
    {
        var config = new PipelineConfiguration();

        var labels = JobDispatcherService.ResolveRequiredLabels(null, config);

        labels.Should().NotBeNull();
    }

    [Fact]
    public void ResolveRequiredLabels_RepoConfigWithLabels_UsesRepoLabels()
    {
        var repoConfig = new ProviderConfig
        {
            Id = "rp-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo",
            Settings = new Dictionary<string, string>(),
            RequiredLabels = new[] { "custom-label" }
        };
        var config = new PipelineConfiguration();

        var labels = JobDispatcherService.ResolveRequiredLabels(repoConfig, config);

        labels.Should().Contain("custom-label");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private AgentEntry RegisterAgent(string agentId, string connectionId, IReadOnlyList<string> labels)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            AgentType = "kiro-dotnet",
            Labels = labels
        }, connectionId);
    }

    private static PendingJob CreatePendingJob(string issueIdentifier, IReadOnlyList<string>? requiredLabels = null)
    {
        return new PendingJob
        {
            IssueIdentifier = issueIdentifier,
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "manual",
            RequiredLabels = requiredLabels ?? Array.Empty<string>()
        };
    }
}
