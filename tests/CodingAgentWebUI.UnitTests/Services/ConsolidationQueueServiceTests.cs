using System.Collections.Concurrent;
using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>Unit tests for <see cref="ConsolidationQueueService"/>.</summary>
public sealed class ConsolidationQueueServiceTests
{
    private readonly ConsolidationQueueService _sut;
    private readonly Mock<ILogger> _mockLogger = new();

    public ConsolidationQueueServiceTests()
    {
        _sut = new ConsolidationQueueService(_mockLogger.Object);
    }

    [Fact]
    public void CancelRun_MarksRunAsCancelled()
    {
        _sut.CancelRun("run-1");

        _sut.IsRunCancelled("run-1").Should().BeTrue();
    }

    [Fact]
    public void IsRunCancelled_ReturnsFalse_WhenNotCancelled()
    {
        _sut.IsRunCancelled("run-unknown").Should().BeFalse();
    }

    [Fact]
    public void CancelRun_RemovesJobFromQueue()
    {
        _sut.EnqueueJob(CreateJob("run-1"));
        _sut.QueueLength.Should().Be(1);

        _sut.CancelRun("run-1");

        _sut.QueueLength.Should().Be(0);
    }

    [Fact]
    public void EnqueueJob_SkipsDuplicate()
    {
        _sut.EnqueueJob(CreateJob("run-1"));
        _sut.EnqueueJob(CreateJob("run-1"));

        _sut.QueueLength.Should().Be(1);
    }

    [Fact]
    public void DequeueForAgent_ReturnsNull_WhenQueueEmpty()
    {
        _sut.DequeueForAgent(CreateAgent()).Should().BeNull();
    }

    [Fact]
    public void DequeueForAgent_MatchesLabels()
    {
        _sut.EnqueueJob(CreateJob("run-1", requiredLabels: new[] { "dotnet" }));

        var noMatch = CreateAgent(labels: new[] { "python" });
        _sut.DequeueForAgent(noMatch).Should().BeNull();

        var match = CreateAgent(labels: new[] { "dotnet" });
        _sut.DequeueForAgent(match).Should().NotBeNull();
    }

    [Fact]
    public void DequeueForAgent_PrunesExpiredCancelledRunIds()
    {
        // Insert an old entry directly into the dictionary via reflection
        var dict = GetCancelledRunIds();
        dict.TryAdd("old-run", DateTimeOffset.UtcNow - ConsolidationQueueService.EvictionWindow - TimeSpan.FromSeconds(1));
        dict.TryAdd("recent-run", DateTimeOffset.UtcNow);

        // Trigger pruning
        _sut.DequeueForAgent(CreateAgent());

        _sut.IsRunCancelled("old-run").Should().BeFalse();
        _sut.IsRunCancelled("recent-run").Should().BeTrue();
    }

    [Fact]
    public void ReEnqueue_AllowsJobToBeDequeued()
    {
        var job = CreateJob("run-1");
        _sut.EnqueueJob(job);

        var dequeued = _sut.DequeueForAgent(CreateAgent());
        dequeued.Should().NotBeNull();
        _sut.QueueLength.Should().Be(0);

        _sut.ReEnqueue(dequeued!);
        _sut.QueueLength.Should().Be(1);
    }

    private ConcurrentDictionary<string, DateTimeOffset> GetCancelledRunIds()
    {
        var field = typeof(ConsolidationQueueService)
            .GetField("_cancelledRunIds", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ConcurrentDictionary<string, DateTimeOffset>)field.GetValue(_sut)!;
    }

    private static PendingConsolidationJob CreateJob(string runId, IReadOnlyList<string>? requiredLabels = null) => new()
    {
        RunId = runId,
        Type = ConsolidationRunType.BrainConsolidation,
        WorkspacePath = "/tmp/test",
        RequiredLabels = requiredLabels ?? Array.Empty<string>(),
        EnqueuedAt = DateTimeOffset.UtcNow
    };

    private static AgentEntry CreateAgent(IReadOnlyList<string>? labels = null) => new()
    {
        AgentId = "agent-1",
        ConnectionId = "conn-1",
        Hostname = "host",
        Labels = labels ?? new[] { "kiro", "dotnet" },
        RegisteredAt = DateTimeOffset.UtcNow
    };
}
